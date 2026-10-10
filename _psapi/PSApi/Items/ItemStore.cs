using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 物品注册表 + 内容解析(items/07)。
    /// 数据来源：内容包 items/*.json (v0.6.1 起移除 legacy UserData/custom_items 伪包与内置示例包)。
    /// 注册：同步进行。目录就绪信号或 DecodeNodes Prefix 时立即同步 Registration (dir.Add 惰性工厂，代价可忽略，BuildItem 只在 DirectoryMaster.Item 被调用时才执行)。幂等：已有即跳过。
    /// 构建手法全部移植自已成功验证的 ExtraItems(BuildItem/BuildFromJson 合并)。
    /// </summary>
    internal static class ItemStore
    {
        private static readonly List<ItemDef> _defs = new List<ItemDef>();
        private static readonly List<string> _registeredIds = new List<string>();
        private static readonly List<object> _pinned = new List<object>();   // 防 Il2Cpp 委托被 GC
        private static MelonLogger.Instance _log;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
        };

        // 标签 → 目录映射 (移植自 ExtraItems.JsonItems.TagDirMap, 未命中走 Misc)
        private static readonly (string tag, string dir)[] TagDirMap =
        {
            ("FOOD", "FoodItemDirectory"), ("PROCESSED_FOOD", "FoodItemDirectory"), ("BREVAGE", "FoodItemDirectory"),
            ("FIREARM", "GunsItemDirectory"), ("WEAPON", "GunsItemDirectory"),
            ("MELEE_WEAPON", "MeleeWeaponItemDirectory"),
            ("ARMOR", "ArmorItemDirectory"),
            ("MACHINE", "StationMachinery"),
            ("MODULE", "ModuleDirectory"), ("ALARM_MODULE", "ModuleDirectory"),
            ("GUN_MOD", "GunModDirectory"),
            ("TOOL", "ToolDirectory"), ("MEDICAL_TOOL", "ToolDirectory"),
            ("MEDICAL", "MedsItemDirectory"), ("NARCOTIC", "MedsItemDirectory"), ("SUBSTANCE", "MedsItemDirectory"),
            ("SEED", "HydroponicDirectory"),
            ("MATERIAL", "MaterialDirectory"), ("BASIC_MATERIAL", "MaterialDirectory"),
            ("STORAGE", "ContainerItemDirectory"),
            ("DOCUMENT", "KeyItemDirectory"), ("ACCESS_CARD", "KeyItemDirectory"),
        };

        internal static int DefCount => _defs.Count;

        internal static bool TryGetDef(string id, out ItemDef def)
        {
            foreach (var d in _defs)
                if (d != null && string.Equals(d.Id, id, System.StringComparison.OrdinalIgnoreCase))
                {
                    def = d;
                    return true;
                }
            def = null;
            return false;
        }
        internal static IReadOnlyList<string> RegisteredIds => _registeredIds;

        /// <summary>v0.8.0: 该 id 是否标记了 "test": true (F6 物品浏览器"测试"分类, ItemsFacade.Catalog 用)。</summary>
        internal static bool IsTestId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            foreach (var d in _defs)
                if (d != null && d.Test && string.Equals(d.Id, id, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        internal static void Init(MelonLogger.Instance log)
        {
            _log = log;
            // v2.0.8: 向 LocService 注入 def 文本查询, 供 ItemLocPatch 对模组物品未注册 item_ 键兜底
            // (TryFallbackItemLoc — 原版管线对未注册键返回 "Translation Error" 错误串, 模组键绝不放行)。
            LocService.SetModItemLookup(id => TryGetDef(id, out var d) ? (d.Name, d.Desc, d.Flavor) : ((string, string, string)?)null);
        }

        // ==================== 解析 (启动时，纯托管) ====================

        /// <summary>清空并重建全部定义 (仅内容包)。qualities 必须先于本方法加载。</summary>
        internal static void LoadAll(List<PackInfo> packs, List<string> errors)
        {
            _defs.Clear();
            _registeredIds.Clear();
            LocService.Clear();

            foreach (var pack in packs)
            {
                if (!pack.Valid || !pack.HasItems) continue;
                IconService.LoadPackIcons(pack);
                LoadPackItems(pack, errors);
            }

            PsApi.Log(_log, $"items loaded: {_defs.Count} def(s), loc={LocService.Count}, icons={IconService.Count}");
            if (IconService.Count > 0) PsApi.Log(_log, "icon keys: " + IconService.DescribeKeys());
        }

        private static void LoadPackItems(PackInfo pack, List<string> errors)
        {
            // ListFiles 内部已按 OrdinalIgnoreCase 排序; 扫描异常内部吞掉返回空数组
            foreach (var file in pack.Source.ListFiles("items", ".json"))
            {
                ItemFile parsed;
                try { parsed = JsonSerializer.Deserialize<ItemFile>(pack.Source.ReadText(file), JsonOpts); }
                catch (Exception e) { errors.Add($"[{pack.Id}] {Path.GetFileName(file)} parse failed: {e.Message}"); continue; }
                if (parsed?.Items == null) continue;
                foreach (var j in parsed.Items)
                {
                    var def = Convert(j, pack.Id, file, errors);
                    if (def != null) _defs.Add(def);
                }
            }
        }

        /// <summary>ItemDefJson → ItemDef(含校验/默认值/图标键解析); 失败记 errors 返回 null。</summary>
        private static ItemDef Convert(ItemDefJson j, string packId, string file, List<string> errors)
        {
            if (j == null) return null;
            if (string.IsNullOrWhiteSpace(j.Id))
            {
                errors.Add($"[{packId}] {Path.GetFileName(file)}: item missing id, skipped");
                return null;
            }
            var def = new ItemDef
            {
                Id = j.Id.Trim(),
                PackId = packId,
                Directory = j.Directory,
                Template = j.Template,
                Name = j.Name,
                Desc = j.Desc,
                Flavor = j.Flavor,
                Value = j.Value,
                Stack = j.Stack,
                Types = j.Types ?? j.Tags,
                TypesReplace = string.Equals(j.TypesMode, "replace", StringComparison.OrdinalIgnoreCase),
                Qualities = j.Qualities,
                DefaultQuality = j.DefaultQuality,
                ContrabandLevel = ParseContraband(j.Contraband),
                CloneRecipes = j.WantsMachineRecipes,
                Test = j.Test,
                UseCount = j.UseCount,
                UseBaseValue = j.UseBaseValue,
                UseValuePerUse = j.UseValuePerUse,
            };

            // 形状：shape 优先, 空=克隆模板
            if (j.Shape != null && j.Shape.W > 0 && j.Shape.H > 0)
            {
                def.ShapeW = j.Shape.W; def.ShapeH = j.Shape.H; def.ShapeCells = j.Shape.Cells;
            }

            // 图标键："name" → pack:name; "pack:name" 原样; {file} → pack:文件名
            def.IconKey = ResolveIconKey(j.Icon, packId);

            // 目录解析：显式 directory > tags 映射 > Misc
            if (string.IsNullOrWhiteSpace(def.Directory))
                def.Directory = ResolveDirectory(def.Types);

            // 文本进本地化表
            LocService.AddItemTexts(def.Id, def.Name, def.Desc, def.Flavor, packId);

            // 品质互斥组登记 (品质表已先加载)
            if (def.Qualities != null && def.Qualities.Count > 0)
                QualityService.RegisterItemGroup(def.Id, def.Qualities);

            return def;
        }

        private static string ResolveIconKey(JsonElement? icon, string packId)
        {
            if (icon == null || icon.Value.ValueKind == JsonValueKind.Null) return null;
            var el = icon.Value;
            if (el.ValueKind == JsonValueKind.String)
            {
                string s = el.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                return s.Contains(":") ? s : packId + ":" + s;
            }
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("file", out var f))
            {
                string s = f.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                return packId + ":" + Path.GetFileNameWithoutExtension(s);
            }
            return null;
        }

        private static string ResolveDirectory(List<string> tags)
        {
            if (tags != null)
                foreach (var (tag, dir) in TagDirMap)
                    foreach (var t in tags)
                        if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) return dir;
            return "MiscItemDirectory";
        }

        internal static int ParseContraband(string level)
        {
            switch ((level ?? "").Trim().ToLowerInvariant())
            {
                case "low": return 1;
                case "mid": return 2;
                case "high": return 3;
                case "critical": return 4;
                default: return 0;
            }
        }

        // ==================== 注册 (同步)

        /// <summary>同步注册全部定义进游戏目录。注册只是 dir.Add(惰性工厂), 代价可忽略;真正的 BuildItem 在 DirectoryMaster.Item(id) 调用时才执行。
        /// 幂等:dir.Has 命中即跳过 (已注册不再 Add, 仅计数)。返回 false = 目录未就绪。
        /// </summary>
        internal static bool RegisterAllNow(string from)
        {
            var byName = BuildDirectoryMap();
            if (byName == null) return false;

            int before = _registeredIds.Count;
            foreach (var def in _defs) RegisterOne(def, byName);
            int added = _registeredIds.Count - before;
            PsApi.Log(_log, $"item registration via {from}: +{added} ({_registeredIds.Count}/{_defs.Count}) registered total");
            return true;
        }

        private static Dictionary<string, Directory<GameItem>> BuildDirectoryMap()
        {
            try
            {
                var dirs = DirectoryMaster.directories;
                if (dirs == null) return null;
                Il2CppSystem.Collections.Generic.List<Il2CppSystem.Object> list;
                try { list = dirs[Il2CppType.Of<GameItem>()]; }
                catch { return null; }
                if (list == null || list.Count == 0) return null;

                var byName = new Dictionary<string, Directory<GameItem>>(StringComparer.Ordinal);
                foreach (var d in list)
                {
                    if (d == null) continue;
                    var typed = d.TryCast<Directory<GameItem>>();
                    if (typed == null) continue;
                    string n = d.GetIl2CppType().Name;
                    if (!byName.ContainsKey(n)) byName[n] = typed;
                }
                return byName.Count == 0 ? null : byName;
            }
            catch (Exception e)
            {
                PsApi.Warn(_log, "directory map failed: " + e.Message);
                return null;
            }
        }

        private static void RegisterOne(ItemDef def, Dictionary<string, Directory<GameItem>> byName)
        {
            try
            {
                if (!byName.TryGetValue(def.Directory, out var dir))
                {
                    PsApi.Warn(_log, $"directory '{def.Directory}' not found, skip {def.Id}");
                    return;
                }
                if (dir.Has(def.Id))
                {
                    if (!_registeredIds.Contains(def.Id)) _registeredIds.Add(def.Id);
                    return;
                }
                PsApi.Log(_log, $"registering: {def.Id} -> {def.Directory} (tpl={def.Template ?? "auto"})");
                string templateId = def.Template ?? FirstIdOf(def.Directory);
                var captured2 = def;
                var capturedTpl = templateId;
                var managed = (Func<GameItem>)(() =>
                {
                    PsApi.Log(_log, $"build begin: {captured2.Id} (tpl={capturedTpl ?? "null"})");
                    GameItem built = null;
                    try { built = BuildItem(captured2, capturedTpl); }
                    catch (Exception e) { PsApi.Warn(_log, $"build {captured2.Id} failed: {e.Message}"); }
                    PsApi.Log(_log, $"build done: {captured2.Id} null={built == null}");
                    return built;
                });
                var factory = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<GameItem>>(managed);
                _pinned.Add(managed);
                _pinned.Add(factory);
                if (dir.Add(def.Id, factory))
                {
                    _registeredIds.Add(def.Id);
                    PsApi.Log(_log, $"item registered: {def.Id} [{def.PackId}] -> {def.Directory}");
                }
                else PsApi.Warn(_log, $"Directory.Add returned false: {def.Id}");
            }
            catch (Exception e) { PsApi.Warn(_log, $"register {def.Id} failed: {e.Message}"); }
        }

        /// <summary>
        /// 目录内第一个**原版**id 作兜底模板。
        /// 必须跳过已注册的 mod 物品: 否则后注册的物品会拿前一个 mod 物品当模板
        /// (已实测: legacy mcb_machine_spp tpl 变成了 example_hello:chip_desequencer), 形成链式污染。
        /// </summary>
        private static string FirstIdOf(string dirClassName)
        {
            try
            {
                var ids = DirectoryMaster.GetIdentifierList<GameItem>(dirClassName);
                foreach (var id in ids)
                {
                    if (id == null) continue;
                    if (_registeredIds.Contains(id)) continue;   // 跳过 PSApi 自己注册的物品
                    if (id.IndexOf(':') >= 0) continue;          // 跳过任何带命名空间的 mod id
                    return id;
                }
            }
            catch (Exception e) { PsApi.Warn(_log, $"GetIdentifierList({dirClassName}) failed: {e.Message}"); }
            return null;
        }

        // ==================== 构建 (游戏对象，移植自 ExtraItems) ====================

        private static GameItem BuildItem(ItemDef def, string templateId)
        {
            // Machines must be created by the native factory.  A plain
            // CreateEmptyItem has no PixelWindow/slot closures and therefore
            // cannot participate in the game's nightly machine pipeline.
            GameItem item;
            if (!RecipeService.TryCreateNativeMachine(def.Id, templateId, out item))
                item = ItemDirectory.CreateEmptyItem(def.Id);
            GameItem tpl = null;
            if (!string.IsNullOrEmpty(templateId))
            {
                try { tpl = DirectoryMaster.Item(templateId, true); } catch { }
            }

            // 图标：自定义 (IconService 有键) > 模板切片 > 硬兜底
            string atlas, sprite, iconFrom;
            if (def.IconKey != null && IconService.Has(def.IconKey))
            {
                atlas = IconService.MarkerAtlas;
                sprite = def.IconKey;
                iconFrom = "custom";
            }
            else if (tpl != null)
            {
                atlas = tpl.spriteAtlasPath;
                sprite = tpl.spritePath;
                iconFrom = "template";
            }
            else
            {
                atlas = "Items/itematlas";
                sprite = "scav_medal";
                iconFrom = "fallback";
            }
            // 诊断: 图标丢失类问题先看这行 —— key 不为 null 但 from=template 就是键未命中
            PsApi.Log(_log, $"icon: {def.Id} key={def.IconKey ?? "-"} from={iconFrom} atlas={atlas} sprite={sprite}");
            // SetSprite 先置 spriteChanged=1 (后续 Validate* 才会重新解析), SetSpriteQuick 再立即应用到渲染
            bool spriteOk = false;
            try { item.SetSprite(atlas, sprite); spriteOk = true; } catch { }
            try { item.SetSpriteQuick(atlas, sprite); spriteOk = true; } catch { }
            if (!spriteOk) { try { item.spriteAtlasPath = atlas; item.spritePath = sprite; } catch { } }

            // 形状 (在贴图之后，防覆盖): ShapeFactory > 克隆模板
            if (def.ShapeCells != null)
            {
                var shape = ShapeFactory.Create(def.ShapeW, def.ShapeH, def.ShapeCells);
                if (shape != null) { try { item.shape = shape; } catch { } }
            }
            if (item.shape == null && tpl != null)
            {
                try { if (tpl.shape != null) item.shape = tpl.shape.Clone(); } catch { }
            }

            // 机器功能：克隆模板配方管理器 (P2 前最简形态)
            bool attached = false;
            try { attached = RecipeService.Attach(item, def.Id, tpl, _pinned); }
            catch (Exception e) { PsApi.Warn(_log, $"attach recipes failed for {def.Id}: {e.Message}"); }
            // 模组打印机：多夜进度机器，独立于配方管线 (v0.5.4+)
            try { ModulePrinterService.TryAttach(item, def.Id, _pinned); }
            catch (Exception e) { PsApi.Warn(_log, $"attach printer failed for {def.Id}: {e.Message}"); }
            // 进度+配方结合的多夜加工 (v0.5.11)
            try { ProgressRecipeService.TryAttach(item, def.Id, _pinned); }
            catch (Exception e) { PsApi.Warn(_log, $"attach progress-recipe failed for {def.Id}: {e.Message}"); }
            // v0.5.12: Harmony patch 方式放宽槽位过滤器 (不修改 mayAdd 委托字段)
            try { SlotFilterRegistry.RegisterMachine(item, def.Id); }
            catch (Exception e) { PsApi.Warn(_log, $"slot-filter register failed ({def.Id}): {e.Message}"); }
            // v0.5.11-diag: dump 槽位委托字段 (仅 desequencer ui 机器)
            try { DumpSlotDelegates(item, def.Id); }
            catch { }
            if (!attached && def.CloneRecipes && tpl != null)
            {
                try { if (tpl.recipeManager != null) item.recipeManager = tpl.recipeManager; } catch { }
            }

            // 类型标签：merge=模板类型 + 定义类型; replace=仅定义类型
            try
            {
                if (!def.TypesReplace && tpl != null)
                {
                    var types = tpl.itemTypes;
                    if (types != null)
                        foreach (var t in types)
                            item.SetGameItemType(t);
                }
                if (def.Types != null)
                    foreach (var t in def.Types)
                    {
                        if (string.IsNullOrWhiteSpace(t)) continue;
                        item.SetGameItemType(t.Trim().ToUpperInvariant());
                    }
            }
            catch { }

            // 文本: 先查包内登记表 LocService.TryGet, 未命中直接回退 def 文本 (flavor 空 = 不显示风味行,
            // 与原版无 flavor 物品一致——GunsItemDirectory 全 30 枪厂零 set_flavorText 实证空 flavor 是常态)。
            // v2.0.7 修复: 不要再无条件走 LocHelper.GetLocalizedItem — 未注册键落到原版管线会**返回**
            // "Translation Error '...' in Item" 错误字符串而非抛异常, try/catch 拦不住, 错误文本被写进
            // flavorText 直接显示 (没写 flavor 的模组物品信息栏底部全挂错误串)。
            string txtName = LocService.TryGet($"item_{def.Id}_name", out var ln) ? ln : (def.Name ?? def.Id);
            string txtDesc = LocService.TryGet($"item_{def.Id}_desc", out var ld) ? ld : (def.Desc ?? "");
            string txtFlavor = LocService.TryGet($"item_{def.Id}_flavor", out var lf) ? lf : (def.Flavor ?? "");
            try { item.SetName(txtName); } catch { }
            try { item.shortDescription = txtDesc; } catch { }
            try { item.flavorText = txtFlavor; } catch { }

            // 违禁品等级 (官方机制一次性完成标签/特性/tooltip/安检注册)
            if (def.ContrabandLevel > 0)
            {
                try { ContrabandHelper.InitContrabandItem(item, def.ContrabandLevel); }
                catch (Exception e) { PsApi.Warn(_log, $"InitContrabandItem failed for {def.Id}: {e.Message}"); }
            }

            // 使用次数 (v0.8.1: 原生 UseCountHelper; tooltip 自动显示 x/上限, 归零销毁, 次数存 tag 随存档)
            if (def.UseCount > 0)
            {
                try
                {
                    UseCountHelper.InitUseCountItem(item, def.UseCount, true, true,
                        def.UseValuePerUse > 0, def.UseBaseValue, def.UseValuePerUse);
                }
                catch (Exception e) { PsApi.Warn(_log, $"InitUseCountItem failed for {def.Id}: {e.Message}"); }
            }

            // 数值
            try { item.unitValue = def.Value; } catch { }
            if (def.Stack > 1) { try { item.unitCount = def.Stack; } catch { } }

            // 出厂品质 (默认第一层或 defaultQuality 指定)
            if (def.Qualities != null && def.Qualities.Count > 0)
            {
                string qid = def.DefaultQuality;
                if (string.IsNullOrEmpty(qid) || !QualityService.TryGetById(qid, out _)) qid = def.Qualities[0];
                QualityService.SetQuality(item, def.Id, qid);
            }

            return item;
        }

        /// <summary>v0.5.11: 包装机器槽位过滤器，放行白名单中的物品 id。</summary>
        private static void WrapSlotFilters(GameItem item, string itemId)
        {
            if (item == null || string.IsNullOrWhiteSpace(itemId)) return;
            if (!RecipeService.TryGetMachineSpecPublic(itemId, out var spec)) return;

            if (spec.InputWhitelist != null && spec.InputWhitelist.Count > 0)
            {
                int inIdx = spec.InputSlot ?? 2;
                try
                {
                    var slot = SlotBridge.GetSlot(item, inIdx);
                    if (slot != null)
                    {
                        var wl = SlotFilterService.BuildWhitelist(spec.InputWhitelist);
                        SlotFilterService.WrapSlotFilter(slot, wl, $"{itemId}:input({inIdx})", _pinned);
                    }
                }
                catch (Exception e) { PsApi.Warn(_log, $"wrap input filter failed ({itemId}, slot {inIdx}): {e.Message}"); }
            }

            if (spec.OutputWhitelist != null && spec.OutputWhitelist.Count > 0)
            {
                int outIdx = spec.OutputSlot ?? (spec.InputSlot ?? 2) + 1;
                try
                {
                    var slot = SlotBridge.GetSlot(item, outIdx);
                    if (slot != null)
                    {
                        var wl = SlotFilterService.BuildWhitelist(spec.OutputWhitelist);
                        SlotFilterService.WrapSlotFilter(slot, wl, $"{itemId}:output({outIdx})", _pinned);
                    }
                }
                catch (Exception e) { PsApi.Warn(_log, $"wrap output filter failed ({itemId}, slot {outIdx}): {e.Message}"); }
            }
        }

        /// <summary>v0.5.11-diag: dump 槽位委托字段 (仅 desequencer ui 机器)。</summary>
        private static void DumpSlotDelegates(GameItem item, string itemId)
        {
            if (item == null || !RecipeService.TryGetMachineSpecPublic(itemId, out var spec)) return;
            if (string.IsNullOrWhiteSpace(spec.Ui) || !spec.Ui.Contains("desequencer")) return;

            for (int i = 0; i <= 4; i++)
            {
                try
                {
                    var slot = SlotBridge.GetSlot(item, i);
                    if (slot == null) continue;
                    bool mayAdd, mayRemove, mayTarget, canTarget, maySelect, canSelect, mayActivate, mayToggle;
                    try { mayAdd = slot.mayInventoryAddItemFunc != null; } catch { mayAdd = false; }
                    try { mayRemove = slot.mayInventoryRemoveItemFunc != null; } catch { mayRemove = false; }
                    try { mayTarget = slot.mayItemTargetSlotItemFunc != null; } catch { mayTarget = false; }
                    try { canTarget = slot.canItemTargetSlotItemFunc != null; } catch { canTarget = false; }
                    try { maySelect = slot.maySelectSlotItemFunc != null; } catch { maySelect = false; }
                    try { canSelect = slot.canSelectSlotItemFunc != null; } catch { canSelect = false; }
                    try { mayActivate = slot.mayActivateSlotItemFunc != null; } catch { mayActivate = false; }
                    try { mayToggle = slot.mayToggleSlotItemFunc != null; } catch { mayToggle = false; }
                    PsApi.Log(_log, $"slot-delegate diag: {itemId} slot[{i}] mayAdd={mayAdd} mayRemove={mayRemove} mayTarget={mayTarget} canTarget={canTarget} maySelect={maySelect} canSelect={canSelect} mayActivate={mayActivate} mayToggle={mayToggle}");
                }
                catch { }
            }
        }

        // ==================== 调试发放 (F12) ====================

        /// <summary>发放全部已注册物品到玩家背包 (调试用)。返回发放数。</summary>
        internal static int GrantAllToPlayer()
        {
            EmporiumEntry entry = null;
            try { entry = EmporiumEntry.Instance; } catch { }
            if (entry == null || entry.backInvinvElement == null) return 0;

            int n = 0;
            foreach (var grantId in _registeredIds.ToList())
            {
                try
                {
                    var item = DirectoryMaster.Item(grantId, true);
                    if (item == null) { PsApi.Warn(_log, "create null: " + grantId); continue; }
                    item.DisableTag("TAG_NOT_PURCHASED", true);
                    item.DisableTag("not_purchased", true);
                    entry.backInvinvElement.TryFindOneValidInventorySlot(item, false);
                    if (((GameInventory)entry.backInvinvElement).UncheckedAccept(item))
                    {
                        n++;
                        PsApi.Log(_log, "granted: " + grantId);
                    }
                    else PsApi.Warn(_log, "UncheckedAccept failed: " + grantId);
                }
                catch (Exception e) { PsApi.Warn(_log, $"grant {grantId} failed: {e.Message}"); }
            }
            if (n > 0)
            {
                try { entry.TransferOwnershipBackInv(); entry.TransferOwnedItemBackToInv(); } catch { }
            }
            return n;
        }
    }
}
