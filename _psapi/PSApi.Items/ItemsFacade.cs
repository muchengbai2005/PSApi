using System;
using System.Collections.Generic;
using Il2Cpp;

namespace PSApi.Items
{
    /// <summary>
    /// E5 跨模块门面: 供 PSApi.Events (PSScript items/quality/machine 命名空间) 等外部程序集调用。
    /// 全部为薄封装: 转调 internal 实现, try/catch 友好返回 (不向外抛异常)。
    /// id 归一化: "game:xxx" (大小写不敏感) = 原版裸 id; 其他 (含 "pack:id" 全限定) 原样。
    /// 发放目标 = EmporiumEntry.backInvinvElement (后仓, F12 实证 UI 可见路径);
    /// 持有/检索范围 = PlayerStore.gridInv (随身背包) + 后仓。
    /// </summary>
    public static class ItemsFacade
    {
        /// <summary>v0.9.3: 无效物品 id 告警去重 (key = 来源|id, 同一来源同一 id 只报一次)。</summary>
        private static readonly HashSet<string> _warnedMissing = new HashSet<string>(StringComparer.Ordinal);

        private static void WarnMissingOnce(string id, string source)
        {
            if (_warnedMissing.Add(source + "|" + id))
                MelonLoader.MelonLogger.Warning($"[psapi] 物品 id '{id}' 在当前版本不存在, 已跳过 (来源: {source})");
        }

        /// <summary>"game:" 前缀 (大小写不敏感) = 原版物品, 剥前缀; 其他 id 原样 (trim)。</summary>
        public static string NormalizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            id = id.Trim();
            return id.StartsWith("game:", StringComparison.OrdinalIgnoreCase) ? id.Substring(5) : id;
        }

        // ==================== 发放 / 持有 / 估价 ====================

        /// <summary>发放 count 个物品到玩家背包。返回最后一个成功发放的实例; 一个都没发成 = null。
        /// 走 F12 实证可见路径: 后仓 backInvinvElement + UncheckedAccept + 末尾 TransferOwnership×2;
        /// gridInv + 槽位放置返回成功但 UI 不可见 (归属未迁移), 勿用。</summary>
        public static GameItem Grant(string itemId, int count) => Grant(itemId, count, 0);

        /// <summary>v0.9.1: uses > 0 时每个发放实例启用次数机制(原生 UseCountHelper 归零销毁, 无次数折算价;
        /// 与 ItemStore 注册时 InitUseCountItem 同款)。旧双参签名保留(二进制兼容)。</summary>
        public static GameItem Grant(string itemId, int count, int uses)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId) || count <= 0) return null;
                string id = NormalizeId(itemId);
                EmporiumEntry entry = null;
                try { entry = EmporiumEntry.Instance; } catch { }
                if (entry == null || entry.backInvinvElement == null) return null;
                GameItem last = null;
                for (int i = 0; i < count; i++)
                {
                    GameItem item = null;
                    try { item = DirectoryMaster.Item(id, true); } catch { }
                    if (item == null)
                    {
                        WarnMissingOnce(id, "items.give");
                        break;
                    }
                    try { item.DisableTag("TAG_NOT_PURCHASED", true); } catch { }
                    try { item.DisableTag("not_purchased", true); } catch { }
                    if (uses > 0)
                        try { UseCountHelper.InitUseCountItem(item, uses, true, true, false, 0, 0); } catch { }
                    try { entry.backInvinvElement.TryFindOneValidInventorySlot(item, false); } catch { }
                    bool ok = false;
                    try { ok = ((GameInventory)entry.backInvinvElement).UncheckedAccept(item); } catch { }
                    if (!ok) break;
                    last = item;
                }
                if (last != null)
                {
                    try { entry.TransferOwnershipBackInv(); entry.TransferOwnedItemBackToInv(); } catch { }
                }
                return last;
            }
            catch { return null; }
        }

        /// <summary>v0.9.2: 发放 count 个物品到店门口柜台(桌面直售区), 归玩家所有。
        /// 贴原版送礼路径 CheckSkinningKnifeReward: ItemSpawner.Spawn + AddDirectSellingItemToTable(item, isOwend: true)。
        /// 不在对局(EmporiumEntry/PlayerStore 不可用) = false + 警告; 全部摆上 = true, 一件都没摆上 = false。
        /// 柜台满位时剩余物品悬空(原生 API 不处理满位, 不做回退清理)。uses > 0 时每个实例启用次数机制(同 Grant)。</summary>
        public static bool GrantToCounter(string itemId, int count, int uses = 0)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId) || count <= 0) return false;
                string id = NormalizeId(itemId);
                PlayerStore store = null;
                try
                {
                    if (EmporiumEntry.Instance == null) return false; // 不在对局
                    store = PlayerStore.instance; // 静态字段(安全); Instance 属性会懒创建
                }
                catch { return false; }
                if (store == null)
                {
                    MelonLoader.MelonLogger.Warning("[psapi] items.give_counter: PlayerStore 不可用(不在对局?), 发放取消");
                    return false;
                }
                int n = 0;
                for (int i = 0; i < count; i++)
                {
                    GameItem item = null;
                    try { item = ItemSpawner.Spawn(id); } catch { }
                    if (item == null)
                    {
                        WarnMissingOnce(id, "items.give_counter");
                        break;
                    }
                    if (uses > 0)
                        try { UseCountHelper.InitUseCountItem(item, uses, true, true, false, 0, 0); } catch { }
                    try { store.AddDirectSellingItemToTable(item, true); n++; }
                    catch (Exception e)
                    {
                        MelonLoader.MelonLogger.Warning($"[psapi] items.give_counter: 摆柜失败 {id}: {e.Message} (已摆 {n}/{count}; 满位时剩余物品悬空, 原生不处理)");
                        break;
                    }
                }
                return n > 0;
            }
            catch { return false; }
        }

        /// <summary>玩家是否持有至少 count 个该物品 (堆叠 unitCount 计入; 搜随身背包 + 后仓, 含容器嵌套)。</summary>
        public static bool PlayerHas(string itemId, int count)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId) || count <= 0) return false;
                return CountInPlayer(NormalizeId(itemId)) >= count;
            }
            catch { return false; }
        }

        /// <summary>物品单价: 已注册 mod 物品查定义; 其他建探针实例读 unitValue。未知/不在对局 = -1。</summary>
        public static int ValueOf(string itemId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return -1;
                if (ItemStore.TryGetDef(itemId.Trim(), out var def)) return def.Value;
                var probe = DirectoryMaster.Item(NormalizeId(itemId), true);
                if (probe == null) return -1;
                long v = probe.unitValue;
                return v < 0 ? 0 : v > int.MaxValue ? int.MaxValue : (int)v;
            }
            catch { return -1; }
        }

        private static readonly Dictionary<string, string> _displayNameCache = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>v0.9.3: 物品显示名 (本地化中文名)。已注册 mod 物品先取定义 name, 再查本地化表
        /// item_&lt;id&gt;_name (原版物品同表), 最后建探针实例读 name; 全部落空 = null。结果按 id 缓存。</summary>
        public static string DisplayName(string itemId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return null;
                string id = NormalizeId(itemId);
                if (_displayNameCache.TryGetValue(id, out var cached)) return cached;
                string name = null;
                if (ItemStore.TryGetDef(itemId.Trim(), out var def) && !string.IsNullOrWhiteSpace(def.Name))
                    name = def.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    try
                    {
                        string key = $"item_{id}_name";
                        string loc = LocHelper.GetLocalizedItem(key);
                        if (!string.IsNullOrWhiteSpace(loc) && loc != key) name = loc;
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(name) && Exists(itemId))
                {
                    try
                    {
                        var probe = DirectoryMaster.Item(id, true);
                        if (probe != null && !string.IsNullOrWhiteSpace(probe.name)) name = probe.name;
                    }
                    catch { }
                }
                if (string.IsNullOrWhiteSpace(name)) return null;
                _displayNameCache[id] = name;
                return name;
            }
            catch { return null; }
        }

        /// <summary>v0.9.3: 物品 id 是否存在 (已注册 mod 物品 或 DirectoryMaster 可查)。
        /// 仅对局内调用 — 加载期 DirectoryMaster 未就绪, 结果不可靠。</summary>
        public static bool Exists(string itemId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemId)) return false;
                if (ItemStore.TryGetDef(itemId.Trim(), out _)) return true;
                return DirectoryMaster.Has<GameItem>(NormalizeId(itemId));
            }
            catch { return false; }
        }

        // ==================== 品质 ====================

        /// <summary>给物品打品质层 (tag/feature 两模式由 QualityService 分派)。品质 id 已注册且调用无异常 = true。</summary>
        public static bool SetQuality(GameItem item, string itemId, string qualityId)
        {
            try
            {
                if (item == null || string.IsNullOrWhiteSpace(qualityId)) return false;
                string qid = qualityId.Trim();
                if (!QualityService.TryGetById(qid, out _)) return false;
                if (string.IsNullOrWhiteSpace(itemId))
                    try { itemId = item.identifier; } catch { }
                QualityService.SetQuality(item, itemId, qid);
                return true;
            }
            catch { return false; }
        }

        /// <summary>物品当前品质 id (全限定, 如 "pack:q_xxx"); 无品质/异常 = null。</summary>
        public static string GetQuality(GameItem item)
        {
            try { return QualityService.GetQuality(item)?.Id; }
            catch { return null; }
        }

        /// <summary>品质定价系数 (无品质/异常 = 1)。</summary>
        public static float PriceFactor(GameItem item)
        {
            try { return QualityService.PriceFactor(item); }
            catch { return 1f; }
        }

        // ==================== 机器 ====================

        /// <summary>多夜加工进度 0..1; 非进度机器 (批次机/无 progress 配置)/异常 = -1。</summary>
        public static float MachineProgress(GameItem item)
        {
            try
            {
                if (item == null) return -1f;
                string id = item.identifier;
                if (!ProgressRecipeService.HasProgress(id)) return -1f;
                if (!RecipeService.TryGetMachineSpecPublic(id, out var spec) || spec.Progress == null) return -1f;
                int max = spec.Progress.Max;
                if (max <= 0) return -1f;
                float f = ProgressRecipeService.GetProgress(item) / (float)max;
                return f < 0f ? 0f : f > 1f ? 1f : f;
            }
            catch { return -1f; }
        }

        /// <summary>正在生产的产出物品 id (按当前输入槽材料匹配配方); 无匹配/非进度机器/异常 = null。</summary>
        public static string MachineProducing(GameItem item)
        {
            try
            {
                return ProgressRecipeService.TryGetProducingOutput(item, out var outputId) ? outputId : null;
            }
            catch { return null; }
        }

        // ==================== 检索 ====================

        /// <summary>在玩家背包 (随身 + 后仓, 含容器嵌套) 深搜第一个匹配物品; 未找到 = null。</summary>
        public static GameItem FindInPlayer(string itemId)
        {
            try
            {
                string id = NormalizeId(itemId);
                if (string.IsNullOrWhiteSpace(id)) return null;
                var seen = new HashSet<IntPtr>();
                foreach (var inv in PlayerInvs())
                {
                    var hit = FindInInventory(inv, id, seen);
                    if (hit != null) return hit;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>在全店活动库存 (ApiProbe LiveInventories 全表) 深搜第一个匹配物品/机器; 未找到 = null。</summary>
        public static GameItem FindMachine(string itemId)
        {
            try
            {
                string id = NormalizeId(itemId);
                if (string.IsNullOrWhiteSpace(id)) return null;
                EmporiumEntry entry = null;
                try { entry = EmporiumEntry.Instance; } catch { }
                if (entry == null) return null;
                var seen = new HashSet<IntPtr>();
                foreach (var inv in LiveInventories(entry))
                {
                    var hit = FindInInventory(inv, id, seen);
                    if (hit != null) return hit;
                }
                return null;
            }
            catch { return null; }
        }

        // ==================== 物品目录 (M3 管理面板物品浏览器) ====================

        /// <summary>物品目录条目: id + 显示名 + 单价 + 分类下标 (CatalogKeys 序号; v0.8.0 起 test 标记物品 = TestCategoryIndex)。</summary>
        public sealed class CatalogEntry
        {
            public string Id;
            public string Name;
            public int Cat;
            public long Value;
        }

        /// <summary>29 个物品目录键 (与 NEI/GAME_GUIDE 一致, 序即分类下标)。</summary>
        public static readonly string[] CatalogKeys =
        {
            "MiscItemDirectory", "ToolDirectory", "MedsItemDirectory", "FoodItemDirectory", "WineDirectory",
            "HydroponicDirectory", "HusbandryDirectory", "MaterialDirectory", "GunsItemDirectory", "GunModDirectory",
            "MeleeWeaponItemDirectory", "ExplosiveItemDirectory", "ArmorItemDirectory", "ContainerItemDirectory", "FurnitureItemDirectory",
            "ModuleDirectory", "ModItemDirectory", "StationMachinery", "RuinedMachineDirectory", "KeyItemDirectory",
            "OrganDirectory", "AmenitiesItemDirectory", "ConstructionItemDirectory", "EquipmentDirectory", "ShipItemDirectory",
            "ShipSystemDirectory", "TechnicianBackpackDirectory", "PlayerAbilityItemDirectory", "UnusedDirectory"
        };

        /// <summary>CatalogKeys 对应中文标签 (同序) + 第 29 类 "测试" (v0.8.0: 仅含 items/*.json 标 "test": true 的包物品,
        /// 该分类不混入任何其他物品 — 测试分类是干净的白名单, 不是目录)。</summary>
        public static readonly string[] CatalogLabels =
        {
            "杂项", "工具", "药品", "食物", "酒类",
            "水培", "畜牧", "材料", "枪械", "枪械配件",
            "近战武器", "爆炸物", "护甲", "容器", "家具",
            "模块", "Mod物品", "站台机械", "废弃机械", "钥匙",
            "器官", "便利设施", "建造", "装备", "飞船",
            "飞船系统", "技师背包", "玩家能力", "未使用",
            "测试"
        };

        /// <summary>v0.8.0: "测试" 分类下标 (CatalogLabels 末位; 不在 CatalogKeys 目录枚举内)。</summary>
        public const int TestCategoryIndex = 29;

        private static List<CatalogEntry> _catalog;
        private static string _catalogRunId = null;

        /// <summary>全量物品目录 (跨 29 目录枚举去重, 先注册目录优先; 第二遍取显示名/单价)。
        /// 按 runID 缓存, 换对局自动重建; 不在对局/枚举失败 = 已有缓存或空表。与 NpcManager EnsureCatalog 同路径。</summary>
        public static List<CatalogEntry> Catalog()
        {
            string runId = "";
            try { var ps = PlayerStore.instance; if (ps != null) runId = ps.runID; } catch { }
            if (_catalog != null && _catalogRunId == runId) return _catalog;
            var list = new List<CatalogEntry>();
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int ci = 0; ci < CatalogKeys.Length; ci++)
                {
                    Il2CppSystem.Collections.Generic.List<string> ids = null;
                    try { ids = DirectoryMaster.GetIdentifierList<GameItem>(CatalogKeys[ci]); } catch { }
                    if (ids == null) continue;
                    foreach (var id in ids)
                    {
                        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) continue;
                        string name = id;
                        long val = 0;
                        try
                        {
                            var item = DirectoryMaster.Item(id, true);
                            if (item != null)
                            {
                                try { if (!string.IsNullOrEmpty(item.name)) name = item.name; } catch { }
                                try { val = item.unitValue; if (val < 0) val = 0; } catch { }
                            }
                        }
                        catch { }
                        // v0.8.0: "test": true 的包物品归入"测试"分类 (第 29 类), 不再出现在原目录分类
                        int cat = ItemStore.IsTestId(id) ? TestCategoryIndex : ci;
                        list.Add(new CatalogEntry { Id = id, Name = name, Cat = cat, Value = val });
                    }
                }
            }
            catch { }
            _catalog = list;
            _catalogRunId = runId;
            return list;
        }

        // ==================== U4 图元槽位 (PSUI slot/grid_slot 混合窗, v0.7.0) ====================

        /// <summary>给自装配槽位注册白名单过滤 (SlotFilterRegistry; exclusive=true = 排他: 非白名单全拒,
        /// 连原生放行也压住 — 自装配槽位零委托, 判定全归注册表, 见 AI_HANDOFF §2.1)。
        /// id 归一化剥 "game:" 前缀 (patch 匹配用裸 identifier)。注册成功 = true。</summary>
        public static bool RegisterSlotWhitelist(GameInventory slot, string[] whitelist, bool exclusive)
        {
            try
            {
                if (slot == null || whitelist == null || whitelist.Length == 0) return false;
                var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in whitelist)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    wl.Add(NormalizeId(raw));
                }
                if (wl.Count == 0) return false;
                SlotFilterRegistry.Register(slot.Pointer, wl, exclusive);
                return true;
            }
            catch { return false; }
        }

        /// <summary>槽内物品快照 (childItems 的托管副本, 指针去重); 槽空/异常 = 空表 (非 null)。</summary>
        public static List<GameItem> SlotItems(GameInventory slot)
        {
            var list = new List<GameItem>();
            try
            {
                if (slot == null) return list;
                var items = slot.childItems;
                if (items == null) return list;
                var seen = new HashSet<IntPtr>();
                foreach (var item in items)
                {
                    if (item == null) continue;
                    try { if (!seen.Add(item.Pointer)) continue; } catch { continue; }
                    list.Add(item);
                }
            }
            catch { }
            return list;
        }

        /// <summary>消耗物品: 从全部所属库存 Expel 取出 + GeneralHelper.DestroyGameItems 销毁
        /// (与 RecipeService 探测物回收同路径)。成功销毁 = true。</summary>
        public static bool ConsumeItem(GameItem item)
        {
            try
            {
                if (item == null) return false;
                try
                {
                    var parents = item.parents;
                    if (parents != null)
                        foreach (var p in parents)
                        {
                            if (p == null) continue;
                            GameInventory inv = null;
                            try { inv = p.TryCast<GameInventory>(); } catch { }
                            if (inv != null) { try { inv.Expel(item); } catch { } }
                        }
                }
                catch { }
                var list = new Il2CppSystem.Collections.Generic.List<GameItem>();
                list.Add(item);
                GeneralHelper.DestroyGameItems(list);
                return true;
            }
            catch { return false; }
        }

        /// <summary>把物品退回玩家后仓 (EmporiumEntry.backInvinvElement + UncheckedAccept
        /// + TransferOwnership×2, 与 Grant 同一条 F12 实证 UI 可见路径); 失败 = false。</summary>
        public static bool ReturnToPlayer(GameItem item)
        {
            try
            {
                if (item == null) return false;
                EmporiumEntry entry = null;
                try { entry = EmporiumEntry.Instance; } catch { }
                if (entry == null || entry.backInvinvElement == null) return false;
                try { item.DisableTag("TAG_NOT_PURCHASED", true); } catch { }
                try { item.DisableTag("not_purchased", true); } catch { }
                try { ((GameInventory)entry.backInvinvElement).UncheckedAccept(item); } catch { }
                try { entry.TransferOwnershipBackInv(); entry.TransferOwnedItemBackToInv(); } catch { }
                return true;
            }
            catch { return false; }
        }

        // ==================== v0.8.0: 机器绑定 PSUI 面板 + 槽位锁 + 槽内生成 ====================

        /// <summary>Events 注册的 PSUI 机器装配 hook: machines/*.json 声明 ui="psui" 时,
        /// RecipeService.TryCreateNativeMachine 建空物品后调本委托装配图元树窗口 + SetContentWindow。
        /// 参数 (机器物品, 物品 id, 面板引用短名或 "pack:panel"); 装配成功 = true。
        /// Events 未加载/面板缺失 = false → 机器回退 plain item。OnInitializeMelon 期注册。</summary>
        public static Func<GameItem, string, string, bool> PsuiMachineAttach;

        /// <summary>v0.8.0: 手动插入锁 (SlotFilterRegistry 三 patch 统一拦截; 组装中锁料/成品未取走拒新部件用)。
        /// 与进度机忙锁并列, 不互相覆盖。槽位失效/异常 = false。</summary>
        public static bool SetSlotInsertLock(GameInventory slot, bool locked)
        {
            try
            {
                if (slot == null) return false;
                SlotFilterRegistry.SetInsertLock(slot.Pointer, locked);
                return true;
            }
            catch { return false; }
        }

        /// <summary>v0.8.0: 取出锁 (原生 GameInventory.LockInv/UnlockInv — 原生只锁取出不锁放入;
        /// 输出槽预览武器"取不下来"用)。锁定后玩家无法从槽中取物, 解锁恢复。异常 = false。</summary>
        public static bool SetSlotRemovalLock(GameInventory slot, bool locked)
        {
            try
            {
                if (slot == null) return false;
                if (locked) slot.LockInv(); else slot.UnlockInv();
                return true;
            }
            catch { return false; }
        }

        /// <summary>v0.8.0: 建一个全新物品直接放进指定槽位 (输出槽武器预览用; id 走 NormalizeId 归一化)。
        /// 不经白名单/过滤器 (UncheckedAccept, 与机器隔夜产出同路径)。成功返回物品实例, 失败 = null。</summary>
        public static GameItem CreateIntoSlot(GameInventory slot, string itemId, int count)
        {
            try
            {
                if (slot == null || string.IsNullOrWhiteSpace(itemId) || count <= 0) return null;
                var item = DirectoryMaster.Item(NormalizeId(itemId), true);
                if (item == null) return null;
                if (count > 1) { try { item.unitCount = count; } catch { } }
                return slot.UncheckedAccept(item) ? item : null;
            }
            catch { return null; }
        }

        // ---- v0.8.1: 电力 / 使用次数 (原生 PowerHelper / UseCountHelper 薄封装, 机器隔夜结算用) ----

        /// <summary>v0.8.1: 读电池当前可用电量 (绕过带宽限制)。非电源/异常 = 0。</summary>
        public static int PowerGet(GameItem item)
        {
            try { return item == null ? 0 : PowerHelper.GetAvailableEnergyFromItem(item, false); }
            catch { return 0; }
        }

        /// <summary>v0.8.1: 电池能否一次抽出 amount 电 (全有或全无判定, 不扣电)。</summary>
        public static bool PowerCanDraw(GameItem item, int amount)
        {
            try { return item != null && amount > 0 && PowerHelper.CanDrawPowerSource(item, amount); }
            catch { return false; }
        }

        /// <summary>v0.8.1: 从电池抽电 (全有或全无: 电量不足不动返回 false, 够则扣 amount 返回 true)。</summary>
        public static bool PowerDraw(GameItem item, int amount)
        {
            try { return item != null && amount > 0 && PowerHelper.DrawPowerSource(item, amount); }
            catch { return false; }
        }

        /// <summary>v0.8.1: 读次数物品当前剩余次数 (原生 tag CURRENT_USE_COUNT_INT)。无次数机制 = 0。</summary>
        public static int UseCountGet(GameItem item) => ReadIntTag(item, "CURRENT_USE_COUNT_INT");

        /// <summary>v0.8.1: 读次数物品上限 (原生 tag MAX_USE_COUNT_INT)。无次数机制 = 0。</summary>
        public static int UseCountMax(GameItem item) => ReadIntTag(item, "MAX_USE_COUNT_INT");

        /// <summary>v0.8.1: 能否再消耗 n 次 (原生 UseCountHelper.CanUse)。</summary>
        public static bool UseCountCanUse(GameItem item, int n)
        {
            try { return item != null && UseCountHelper.CanUse(item, n <= 0 ? 1 : n); }
            catch { return false; }
        }

        /// <summary>v0.8.1: 消耗 n 次 (原生 UseCountHelper.Use; 归零按初始化时 destroyOnEmpty 销毁, 本框架注册的次数物品均为归零销毁)。</summary>
        public static bool UseCountUse(GameItem item, int n)
        {
            try
            {
                if (item == null || !UseCountHelper.CanUse(item, n <= 0 ? 1 : n)) return false;
                UseCountHelper.Use(item, n <= 0 ? 1 : n);
                return true;
            }
            catch { return false; }
        }

        /// <summary>v0.8.1: 给已有物品实例启用次数机制 (运行时补启用用; 语义同 JSON useCount/useBaseValue/useValuePerUse 字段)。</summary>
        public static bool UseCountInit(GameItem item, int maxUse, int baseValue, int valuePerUse)
        {
            try
            {
                if (item == null || maxUse <= 0) return false;
                UseCountHelper.InitUseCountItem(item, maxUse, true, true, valuePerUse > 0, baseValue, valuePerUse);
                return true;
            }
            catch { return false; }
        }

        // ---- v0.8.2: 展示柜枚举 / 玩家库存全量查找 (仿制证书等"扫场"脚本用) ----

        /// <summary>v0.8.2: 展示柜 (商品展示区) 内全部物品 (EmporiumEntry.showcaseElement.childItems,
        /// 与 ProbeTools 活库存同路径)。不在对局/异常 = 空表。</summary>
        public static List<GameItem> ShowcaseItems()
        {
            var result = new List<GameItem>();
            try
            {
                var ee = EmporiumEntry.Instance;
                var sc = ee == null ? null : ee.showcaseElement;
                var children = sc == null ? null : sc.childItems;
                if (children != null)
                    foreach (var item in children)
                        if (item != null) result.Add(item);
            }
            catch { }
            return result;
        }

        /// <summary>v0.8.2: 玩家库存 (随身背包+后仓) 里所有 id 匹配的物品 (id 走 NormalizeId; 一个不放过,
        /// 证书衰减等"全场 -1"用)。异常 = 空表。</summary>
        public static List<GameItem> FindAllInPlayer(string itemId)
        {
            var result = new List<GameItem>();
            try
            {
                string id = NormalizeId(itemId);
                if (string.IsNullOrWhiteSpace(id)) return result;
                foreach (var inv in PlayerInvs())
                {
                    Il2CppSystem.Collections.Generic.List<GameItem> children = null;
                    try { children = inv?.childItems; } catch { }
                    if (children == null) continue;
                    foreach (var item in children)
                    {
                        if (item == null) continue;
                        string iid = null;
                        try { iid = item.identifier; } catch { }
                        if (string.Equals(iid, id, StringComparison.Ordinal)) result.Add(item);
                    }
                }
            }
            catch { }
            return result;
        }

        // ==================== 内部 ====================

        /// <summary>读物品整数 tag: modifiedState 优先, 回退 state (同 ModulePrinterService.ReadTag 模式)。</summary>
        private static int ReadIntTag(GameItem item, string key)
        {
            try
            {
                if (item == null) return 0;
                var tag = item.modifiedState?.GetTag(key);
                if (tag == null) tag = item.state?.GetTag(key);
                return tag?.valueInt ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>玩家侧库存: 随身背包 + 后仓 (指针去重)。</summary>
        private static IEnumerable<GameInventory> PlayerInvs()
        {
            GameGridInventory g = null, b = null;
            try { var ps = PlayerStore.instance; if (ps != null) g = ps.gridInv; } catch { }
            try { var e = EmporiumEntry.Instance; if (e != null) b = e.backInvinvElement; } catch { }
            if (g != null) yield return g;
            if (b != null)
            {
                bool dup = false;
                try { dup = g != null && b.Pointer == g.Pointer; } catch { }
                if (!dup) yield return b;
            }
        }

        /// <summary>全店活动库存表 (移植自 ProbeTools.ApiProbe.LiveInventories)。</summary>
        private static List<GameInventory> LiveInventories(EmporiumEntry entry)
        {
            var list = new List<GameInventory>();
            Action<GameInventory> add = inv => { if (inv != null) list.Add(inv); };
            try { add(entry.backInvinvElement); } catch { }
            try { add(entry.backInvinvElementCounter); } catch { }
            try { add(entry.frontInvinvElement); } catch { }
            try { add(entry.invElement); } catch { }
            try { add(entry.showcaseElement); } catch { }
            try { add(entry.hiddenElement); } catch { }
            try { add(entry.soldElement); } catch { }
            try { add(entry.trashcanInvElement); } catch { }
            try { add(entry.trashInvElement); } catch { }
            try { add(entry.docInvElement); } catch { }
            try { add(entry.bazarLeftinvElement); } catch { }
            try { add(entry.afterhourInventory); } catch { }
            try { add(entry.hirelingInv); } catch { }
            try { add(entry.swapBufferElement); } catch { }
            try { add(entry.drainInvElement); } catch { }
            return list;
        }

        private static GameItem FindInInventory(GameInventory inv, string id, HashSet<IntPtr> seen)
        {
            Il2CppSystem.Collections.Generic.List<GameItem> items = null;
            try { if (inv != null) items = inv.childItems; } catch { }
            if (items == null) return null;
            foreach (var item in items)
            {
                var hit = FindDeep(item, id, seen);
                if (hit != null) return hit;
            }
            return null;
        }

        private static GameItem FindDeep(GameItem item, string id, HashSet<IntPtr> seen)
        {
            if (item == null) return null;
            try { if (!seen.Add(item.Pointer)) return null; } catch { return null; }
            try
            {
                if (string.Equals(item.identifier, id, StringComparison.OrdinalIgnoreCase)) return item;
            }
            catch { }
            Il2CppSystem.Collections.Generic.List<GameItem> children = null;
            try { children = item.FindAllChildItems(); } catch { }
            if (children == null) return null;
            foreach (var child in children)
            {
                var hit = FindDeep(child, id, seen);
                if (hit != null) return hit;
            }
            return null;
        }

        private static int CountInPlayer(string id)
        {
            int total = 0;
            var seen = new HashSet<IntPtr>();
            foreach (var inv in PlayerInvs())
            {
                Il2CppSystem.Collections.Generic.List<GameItem> items = null;
                try { if (inv != null) items = inv.childItems; } catch { }
                if (items == null) continue;
                foreach (var item in items) CountDeep(item, id, seen, ref total);
            }
            return total;
        }

        private static void CountDeep(GameItem item, string id, HashSet<IntPtr> seen, ref int total)
        {
            if (item == null) return;
            try { if (!seen.Add(item.Pointer)) return; } catch { return; }
            try
            {
                if (string.Equals(item.identifier, id, StringComparison.OrdinalIgnoreCase))
                {
                    int n = 1;
                    try { n = item.unitCount; } catch { }
                    total += n > 0 ? n : 1;
                }
            }
            catch { }
            Il2CppSystem.Collections.Generic.List<GameItem> children = null;
            try { children = item.FindAllChildItems(); } catch { }
            if (children == null) return;
            foreach (var child in children) CountDeep(child, id, seen, ref total);
        }
    }
}
