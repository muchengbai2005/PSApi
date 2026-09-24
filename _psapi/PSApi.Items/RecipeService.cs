using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// JSON 配方/机器编译 + 隔夜加工运行时 (P2)。
    /// v0.5.6-recover: 从 v0.5.2 反编译基线 + v0.5.3/0.5.4/0.5.5 设计记录完整重建
    /// (v0.7.0 曾把 CreateRecipe stub 化、丢掉 machines 加载导致整个配方系统静默失效，已回滚重写)。
    /// 流程: machines/*.json + recipes/*.json → LoadAll(含 Audit 自检) → BuildItem 时
    /// Attach(编译 CraftingRecipeManager + onCycleEndSlotItemFunc) → EndNight 原生驱动 RunCycle。
    /// </summary>
    internal static class RecipeService
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
        };

        private static readonly Dictionary<string, RecipeDefJson> Recipes =
            new Dictionary<string, RecipeDefJson>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> MachineItemRefs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, MachineDefJson> MachineSpecsByItem =
            new Dictionary<string, MachineDefJson>(StringComparer.OrdinalIgnoreCase);

        private static MelonLogger.Instance _log;

        internal static int Count => Recipes.Count;
        internal static void Init(MelonLogger.Instance log) => _log = log;

        internal static void Clear()
        {
            Recipes.Clear();
            MachineItemRefs.Clear();
            MachineSpecsByItem.Clear();
        }

        // ==================== 加载 ====================

        internal static void LoadAll(List<PackInfo> packs, List<string> errors)
        {
            Clear();
            if (packs == null) return;
            foreach (var pack in packs)
            {
                if (pack != null && pack.Valid)
                {
                    LoadMachines(pack, errors);
                    LoadRecipes(pack, errors);
                }
            }
            PsApi.Log(_log, $"recipes loaded: {Recipes.Count}, machines={MachineItemRefs.Count}");
            Audit();
        }

        private static void LoadRecipes(PackInfo pack, List<string> errors)
        {
            if (!pack.Source.HasDir("recipes")) return;

            foreach (var file in pack.Source.ListFiles("recipes", ".json"))
            {
                RecipeFile parsed;
                try { parsed = JsonSerializer.Deserialize<RecipeFile>(pack.Source.ReadText(file), JsonOpts); }
                catch (Exception e) { errors.Add($"[{pack.Id}] {Path.GetFileName(file)} parse failed: {e.Message}"); continue; }
                if (parsed?.Recipes == null) continue;

                foreach (var r in parsed.Recipes)
                {
                    if (r == null || string.IsNullOrWhiteSpace(r.Id))
                    {
                        errors.Add($"[{pack.Id}] {Path.GetFileName(file)}: recipe missing id, skipped");
                        continue;
                    }
                    r.Id = NormalizeId(r.Id, pack.Id);
                    r.Machine = NormalizeId(r.Machine, pack.Id);
                    if (r.Output != null) r.Output.Id = ResolveItemId(r.Output.Id, pack.Id);
                    if (r.Inputs != null)
                        foreach (var input in r.Inputs)
                            if (input != null) input.Id = ResolveItemId(input.Id, pack.Id);
                    if (r.Tools != null)
                        foreach (var tool in r.Tools)
                            if (tool != null) tool.Id = ResolveItemId(tool.Id, pack.Id);
                    if (Recipes.ContainsKey(r.Id))
                        PsApi.Warn(_log, $"recipe '{r.Id}' overwritten by pack {pack.Id}");
                    Recipes[r.Id] = r;
                }
            }
        }

        private static void LoadMachines(PackInfo pack, List<string> errors)
        {
            if (!pack.Source.HasDir("machines")) return;

            foreach (var file in pack.Source.ListFiles("machines", ".json"))
            {
                MachineFile parsed;
                try { parsed = JsonSerializer.Deserialize<MachineFile>(pack.Source.ReadText(file), JsonOpts); }
                catch (Exception e) { errors.Add($"[{pack.Id}] {Path.GetFileName(file)} parse failed: {e.Message}"); continue; }
                if (parsed?.Machines == null) continue;

                foreach (var m in parsed.Machines)
                {
                    if (m == null || string.IsNullOrWhiteSpace(m.Id))
                    {
                        errors.Add($"[{pack.Id}] {Path.GetFileName(file)}: machine missing id, skipped");
                        continue;
                    }
                    var machineId = NormalizeId(m.Id, pack.Id);
                    var itemRef = NormalizeId(string.IsNullOrWhiteSpace(m.ItemRef) ? machineId : m.ItemRef, pack.Id);
                    if (MachineItemRefs.ContainsKey(machineId))
                        PsApi.Warn(_log, $"machine '{machineId}' overwritten by pack {pack.Id}");
                    MachineItemRefs[machineId] = itemRef;
                    m.Id = machineId;
                    m.ItemRef = itemRef;
                    if (MachineSpecsByItem.ContainsKey(itemRef))
                        PsApi.Warn(_log, $"machine item '{itemRef}' overwritten by pack {pack.Id}");
                    MachineSpecsByItem[itemRef] = m;
                }
            }
        }

        // ==================== 原生机器工厂桥接 (v0.5.3 收口: 只认 machines/*.json) ====================

        /// <summary>
        /// 用 machines/*.json.ui 指定的原版工厂创建机器 (保留真实 PixelWindow/槽位/电池/模组闭包) 再改写 identifier。
        /// v0.5.3 起: 没有声明就一定不是机器 —— 不再按模板名猜 (InferUi 仅用于 plain item 提示日志)。
        /// v0.6.0: ui == "custom" 分流 CustomMachineFactory (自装配窗口, 不调原版工厂, identifier 由工厂直接写)。
        /// v0.8.0: ui == "psui" 分流 Events 装配 (机器绑定 PSUI 面板, ItemsFacade.PsuiMachineAttach hook)。
        /// </summary>
        internal static bool TryCreateNativeMachine(string itemId, string templateId, out GameItem machine)
        {
            machine = null;
            string ui = null;
            MachineDefJson spec = null;
            if (!string.IsNullOrWhiteSpace(itemId) && TryGetMachineSpec(itemId, out spec))
                ui = spec.Ui;
            if (string.IsNullOrWhiteSpace(ui) || string.Equals(ui, "none", StringComparison.OrdinalIgnoreCase))
            {
                // 仅对"模板名像机器"的物品打提示 (普通物品零噪音)
                if (!string.IsNullOrWhiteSpace(templateId) && InferUi(templateId) != null)
                    PsApi.Log(_log, $"plain item: {itemId} built as decoration (template '{templateId}' looks like a machine but has no machines/*.json declaration)");
                return false;
            }
            try
            {
                if (IsPsuiUi(ui))
                {
                    // v0.8.0: ui="psui" — 机器绑定 PSUI 面板。Items 建空物品, Events 经
                    // ItemsFacade.PsuiMachineAttach hook 装配图元树窗口 + SetContentWindow
                    // (双击原生开窗; 槽内物品随机器子物品序列化)。hook 未注册(Events 缺失) = 失败回退。
                    machine = ItemDirectory.CreateEmptyItem(itemId);
                    if (machine == null || ItemsFacade.PsuiMachineAttach == null
                        || !ItemsFacade.PsuiMachineAttach(machine, itemId, spec?.Panel))
                    {
                        PsApi.Warn(_log, $"psui machine attach failed ({itemId}, panel={spec?.Panel ?? "null"}) — Events 未注册 hook 或面板缺失, 回退 plain item");
                        machine = null;
                        return false;
                    }
                }
                else
                {
                    machine = IsCustomUi(ui) ? CustomMachineFactory.Build(itemId, spec) : CreateByUi(ui);
                }
                if (machine == null) return false;
                if (!string.IsNullOrWhiteSpace(itemId))
                {
                    try { machine.identifier = itemId; }
                    catch (Exception e) { PsApi.Warn(_log, $"machine identifier rewrite failed ({itemId}): {e.Message}"); }
                }
                PsApi.Log(_log, $"native machine factory: {itemId} ui={ui} template={templateId ?? "null"}");
                return true;
            }
            catch (Exception e)
            {
                PsApi.Warn(_log, $"native machine factory failed ({itemId}, ui={ui}): {e.Message}");
                machine = null;
                return false;
            }
        }

        /// <summary>该物品是否有 machines/*.json 声明 + 非空 ui (SaveLoadPatches 存档捕获判定用)。</summary>
        internal static bool HasNativeMachineUi(string itemId)
        {
            return !string.IsNullOrWhiteSpace(itemId)
                && TryGetMachineSpec(itemId, out var spec)
                && !string.IsNullOrWhiteSpace(spec.Ui)
                && !string.Equals(spec.Ui, "none", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetMachineSpec(string itemId, out MachineDefJson spec)
        {
            if (MachineSpecsByItem.TryGetValue(itemId, out spec)) return true;
            foreach (var kvp in MachineSpecsByItem)
            {
                if (kvp.Key.EndsWith(":" + itemId, StringComparison.OrdinalIgnoreCase))
                {
                    spec = kvp.Value;
                    return true;
                }
            }
            spec = null;
            return false;
        }

        /// <summary>仅用于 plain item 提示日志 —— 不参与工厂决策 (v0.5.3 收口)。</summary>
        private static string InferUi(string templateId)
        {
            if (string.IsNullOrWhiteSpace(templateId)) return null;
            string s = templateId.Trim().ToLowerInvariant().Replace("-", "_");
            if (s.Contains("furnace") || s.Contains("smelter")) return "furnace";
            if (s.Contains("purifier")) return "purifier";
            if (s.Contains("moisture")) return "moisturefarm";
            if (s.Contains("projector")) return "projector";
            if (s.Contains("desequencer")) return "desequencer";
            if (s.Contains("wine") || s.Contains("agewell") || s.Contains("wine_rack")) return "winerack";
            if (s.Contains("bottle") && s.Contains("printer")) return "bottleprinter";
            if (s.Contains("recharger") || s.Contains("charger")) return "recharger";
            if (s.Contains("alarm")) return "alarm";
            if (s.Contains("cassette")) return "cassetteplayer";
            if (s.Contains("box") && s.Contains("dispenser")) return "boxdispenser";
            if (s.Contains("feed") && s.Contains("dispenser")) return "feeddispenser";
            return null;
        }

        /// <summary>ui 归一化 (与 CreateByUi 同款) 后判 "custom"。v0.6.3 起 internal (SlotFilterRegistry 排他注册判定用)。</summary>
        internal static bool IsCustomUi(string ui)
            => string.Equals((ui ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", ""), "custom", StringComparison.Ordinal);

        /// <summary>v0.8.0: ui 归一化后判 "psui" (机器绑定 PSUI 面板, 窗口由 Events 经 ItemsFacade.PsuiMachineAttach 装配)。</summary>
        internal static bool IsPsuiUi(string ui)
            => string.Equals((ui ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", ""), "psui", StringComparison.Ordinal);

        private static GameItem CreateByUi(string ui)
        {
            switch ((ui ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", ""))
            {
                case "furnace": return MachineFurnace.Furnace();
                case "alarm":
                case "alarme": return MachineAlarm.Alarme();
                case "bottleprinter":
                case "bottle_printer": return MachineBottlePrinter.BottlePrinter();
                case "box_dispenser":
                case "boxdispenser": return MachineBoxDispenser.BoxDispenser();
                case "cassetteplayer":
                case "cassette_player": return MachineCassettePlayer.CassettePlayer();
                case "desequencer": return MachineDesequencer.Desequencer();
                case "feeddispenser":
                case "feed_dispenser": return MachineFeedDispenser.CreateFeedDispenser();
                case "moisture_farm":
                case "moisturefarm": return MachineMoistureFarm.MoistureFarm();
                case "projector": return MachineProjector.Projector();
                case "waterpurifier":
                case "water_purifier":
                case "purifier": return MachinePurifier.WaterPurifier();
                case "recharger": return MachineRecharger.Recharger();
                case "agewell":
                case "wine_rack":
                case "winerack": return MachineWineRack.AgeWell();
                default: return null;
            }
        }

        // ==================== 配方编译与挂载 ====================

        internal static bool HasRecipesFor(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return false;
            foreach (var r in Recipes.Values)
                if (SameIdentifier(ResolveMachineItem(r.Machine), itemId)) return true;
            return false;
        }

        /// <summary>v0.5.9: 该机器的所有配方信息 (供锁定 UI 列出)。按当前排序策略排序后返回。</summary>
        internal static List<RecipeInfo> GetRecipeInfosForMachine(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return new List<RecipeInfo>();
            var matching = Recipes.Values.Where(r => SameIdentifier(ResolveMachineItem(r.Machine), itemId)).ToList();
            TryGetMachineSpec(itemId, out var sortSpec);
            SortRecipeInfos(matching, sortSpec, itemId);
            return matching.Select(r => new RecipeInfo
            {
                Id = r.Id,
                Name = string.IsNullOrWhiteSpace(r.CustomName) ? r.Id : r.CustomName,
                Description = r.Description ?? "",
                Priority = r.Priority ?? 0,
            }).ToList();
        }

        /// <summary>v0.5.10: 读取机器当前输入槽材料，匹配第一个 CanCraft 的配方，返回其 ID。</summary>
        internal static bool TryMatchCurrentRecipe(GameItem machine, string itemId, out string matchedRecipeId)
        {
            matchedRecipeId = null;
            if (machine == null || string.IsNullOrWhiteSpace(itemId)) return false;
            if (!TryGetMachineSpec(itemId, out var spec)) return false;
            var allItems = GetInputItems(machine, spec);
            if (allItems == null || allItems.Count == 0) return false;
            var matching = Recipes.Values.Where(r => SameIdentifier(ResolveMachineItem(r.Machine), itemId)).ToList();
            if (matching.Count == 0) return false;
            SortRecipes(matching, spec, itemId);
            var tmpPinned = new List<object>();
            foreach (var def in matching)
            {
                try
                {
                    var recipe = Compile(def, tmpPinned);
                    if (recipe != null && recipe.CanCraft(allItems))
                    {
                        matchedRecipeId = def.Id;
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        private static void SortRecipeInfos(List<RecipeDefJson> recipes, MachineDefJson spec, string itemId)
        {
            if (recipes == null || recipes.Count <= 1) return;
            string strategy = (spec?.RecipeSort ?? "priority").Trim().ToLowerInvariant();
            if (strategy == "firstmatch") return;
            List<RecipeDefJson> ordered;
            if (strategy == "value")
            {
                ordered = recipes
                    .OrderByDescending(r => OutputValue(r))
                    .ThenByDescending(r => r?.Priority ?? 0)
                    .ToList();
            }
            else
            {
                ordered = recipes
                    .OrderByDescending(r => r?.Priority ?? 0)
                    .ToList();
            }
            recipes.Clear();
            recipes.AddRange(ordered);
        }

        internal struct RecipeInfo
        {
            public string Id;
            public string Name;
            public string Description;
            public int Priority;
        }

        /// <summary>编译该机器的全部 JSON 配方成独立 CraftingRecipeManager 并挂隔夜回调 (BuildItem / 存档重挂两处调用)。</summary>
        internal static bool Attach(GameItem item, string itemId, GameItem template, List<object> pinned)
        {
            if (item == null || string.IsNullOrWhiteSpace(itemId)) return false;
            var matching = Recipes.Values.Where(r => SameIdentifier(ResolveMachineItem(r.Machine), itemId)).ToList();
            if (matching.Count == 0) return false;

            // v0.5.8: 配方选择策略排序 (以 v0.5.7 RunCycle FirstMatch 为基线, 仅改遍历序; 不引入机器级全局锁)
            TryGetMachineSpec(itemId, out var sortSpec);
            SortRecipes(matching, sortSpec, itemId);

            var manager = new CraftingRecipeManager();
            var compiled = new List<CompiledRecipe>();
            foreach (var def in matching)
            {
                try
                {
                    var recipe = Compile(def, pinned);
                    if (recipe != null)
                    {
                        manager.availableRecipes.Add(recipe);
                        compiled.Add(new CompiledRecipe(def, recipe));
                    }
                }
                catch (Exception e) { PsApi.Warn(_log, $"compile recipe {def.Id} failed: {e.Message}"); }
            }
            if (manager.availableRecipes == null || manager.availableRecipes.Count == 0) return false;

            item.recipeManager = manager;
            AttachCycleHandler(item, itemId, compiled, pinned);
            return true;
        }

        /// <summary>
        /// v0.5.8: 配方选择策略排序。以 v0.5.7 RunCycle FirstMatch 为基线, 仅改遍历序。
        /// 策略 (MachineDefJson.recipeSort, 默认 "priority"):
        ///   "priority"  — 按 recipe.priority 降序, 稳定 (同 priority 保留声明序; 不加 priority 的配方全 0 → 完全向后兼容)
        ///   "value"     — 按产出物品价值降序, 同价值按 priority 降序 (价值读 ItemStore.TryGetDef; 原版产出读不到按 0, 用 priority 补偿)
        ///   "firstMatch"— 纯声明序, 不排序 (完全向后兼容)
        /// 稳定排序保证同 key 的配方保留 Recipes.Values 枚举序 (加载时字典插入序 = 文件名字母序 + 文件内顺序)。
        /// </summary>
        private static void SortRecipes(List<RecipeDefJson> recipes, MachineDefJson spec, string itemId)
        {
            if (recipes == null || recipes.Count <= 1) return;
            string strategy = (spec?.RecipeSort ?? "priority").Trim().ToLowerInvariant();
            if (strategy == "firstmatch") return;
            List<RecipeDefJson> ordered;
            if (strategy == "value")
            {
                ordered = recipes
                    .OrderByDescending(r => OutputValue(r))
                    .ThenByDescending(r => r?.Priority ?? 0)
                    .ToList();
            }
            else
            {
                if (strategy != "priority")
                    PsApi.Warn(_log, $"machine {itemId}: unknown recipeSort '{strategy}', falling back to 'priority'");
                ordered = recipes
                    .OrderByDescending(r => r?.Priority ?? 0)
                    .ToList();
            }
            recipes.Clear();
            recipes.AddRange(ordered);
        }

        private static int OutputValue(RecipeDefJson def)
        {
            try
            {
                string id = def?.Output?.Id;
                if (string.IsNullOrWhiteSpace(id)) return 0;
                return ItemStore.TryGetDef(id, out var d) ? d.Value : 0;
            }
            catch { return 0; }
        }

        private static void AttachCycleHandler(GameItem item, string itemId, List<CompiledRecipe> recipes, List<object> pinned)
        {
            if (item == null || recipes == null || recipes.Count == 0) return;
            MachineDefJson spec = null;
            TryGetMachineSpec(itemId, out spec);

            var capturedRecipes = recipes;
            var capturedSpec = spec;
            var capturedItemId = itemId;
            var managed = (Action<GameItem, GameInventory, SlotMarker>)((machine, _, __) =>
            {
                try { RunCycle(machine ?? item, capturedItemId, capturedSpec, capturedRecipes); }
                catch (Exception e) { PsApi.Warn(_log, $"machine cycle failed ({capturedItemId}): {e.Message}"); }
            });
            var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameItem, GameInventory, SlotMarker>>(managed);
            pinned.Add(managed);
            pinned.Add(callback);
            try { item.onCycleEndSlotItemFunc = callback; }
            catch (Exception e) { PsApi.Warn(_log, $"machine cycle callback install failed ({capturedItemId}): {e.Message}"); }
        }

        /// <summary>JSON 配方 → 原生 CraftingRecipe (纯数据路径: 名称/描述/次数/材料/工具/常量产出)。</summary>
        private static CraftingRecipe Compile(RecipeDefJson def, List<object> pinned)
        {
            if (def.Output == null || string.IsNullOrWhiteSpace(def.Output.Id))
                throw new InvalidDataException("output.id is required");

            var recipe = new CraftingRecipe(def.CustomName ?? def.Id);
            if (!string.IsNullOrWhiteSpace(def.Description))
                recipe.description = new RichTextBuilder().AddLine(def.Description);
            if (def.Uses.HasValue)
                recipe.SetRemainingCraftAmount(def.Uses.Value);
            if (def.Inputs != null)
                foreach (var input in def.Inputs)
                    AddFilter(recipe, input, tool: false);
            if (def.Tools != null)
                foreach (var tool in def.Tools)
                    AddFilter(recipe, tool, tool: true);
            recipe.SetCraftingConstantFunc(def.Output.Id, Math.Max(1, def.Output.Count));
            return recipe;
        }

        private static void AddFilter(CraftingRecipe recipe, FilterDefJson spec, bool tool)
        {
            if (spec == null) return;
            int amount = spec.Count <= 0 ? 1 : spec.Count;
            string displayName = spec.Display ?? spec.Id ?? spec.Tag ?? "requirement";
            CraftingFilter filter;
            if (!string.IsNullOrWhiteSpace(spec.Id))
            {
                filter = new CraftingIdentifierFilter(displayName, amount, spec.Id);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(spec.Tag))
                    throw new InvalidDataException("filter requires id or tag");
                filter = new CraftingTagsFilter(displayName, amount, spec.Tag.ToUpperInvariant());
            }
            if (tool) recipe.AddTool(filter);
            else recipe.AddMaterial(filter);
        }

        // ==================== 隔夜加工 (EndNight 原生驱动) ====================

        private static void RunCycle(GameItem machine, string itemId, MachineDefJson spec, List<CompiledRecipe> recipes)
        {
            PsApi.Log(_log, $"cycle fired: {itemId} recipes={recipes?.Count ?? 0}");
            if (machine == null || recipes == null || recipes.Count == 0) return;

            int inputIndex = spec?.InputSlot ?? 2;
            int outputIndex = spec?.OutputSlot ?? inputIndex + 1;   // v0.5.3: 缺省 = input+1
            if (inputIndex < 0 || outputIndex < 0)
            {
                PsApi.Warn(_log, $"cycle aborted ({itemId}): invalid slots in={inputIndex} out={outputIndex}");
                return;
            }
            bool inPlace = inputIndex == outputIndex;               // v0.5.3: 原位机器 (in == out) 合法

            GameInventory input;
            GameInventory output;
            try
            {
                input = SlotBridge.GetSlot(machine, inputIndex);
                output = SlotBridge.GetSlot(machine, outputIndex);
            }
            catch (Exception e)
            {
                PsApi.Warn(_log, $"machine slots unavailable ({itemId}, in={inputIndex}, out={outputIndex}): {e.Message}");
                return;
            }
            if (input == null || output == null)
            {
                PsApi.Warn(_log, $"cycle aborted ({itemId}): slot null in={input == null} out={output == null} (in={inputIndex}, out={outputIndex})");
                return;
            }

            var battery = (spec?.BatteryPowered ?? true) ? SafeGetBattery(machine) : null;
            PsApi.Log(_log, $"cycle state ({itemId}): inputItems={input.childItems?.Count ?? -1} battery={(battery == null ? "null" : (battery.childItems?.Count ?? 0).ToString())}");

            int batches = Math.Max(1, spec?.ProgressSpeed ?? 1);   // progressSpeed = 每晚最多批次数
            bool anyCrafted = false;
            var locked = RecipeLockService.GetLocked(machine);   // v0.5.9: 该机器锁定的配方 (隔夜跳过)
            for (int i = 0; i < batches; i++)
            {
                var allItems = GetInputItems(machine, spec);
                if (allItems == null || allItems.Count == 0)
                {
                    PsApi.Log(_log, $"cycle stop ({itemId}): input empty at batch {i}");
                    break;
                }

                // 选配方: 首个 CanCraft 命中 (v0.5.9: 跳过锁定配方)
                CompiledRecipe selected = null;
                foreach (var candidate in recipes)
                {
                    if (candidate?.Recipe == null) continue;
                    if (locked.Count > 0 && candidate.Def != null && locked.Contains(candidate.Def.Id)) continue;
                    try
                    {
                        if (candidate.Recipe.CanCraft(allItems))
                        {
                            selected = candidate;
                            break;
                        }
                    }
                    catch (Exception e) { PsApi.Warn(_log, $"recipe check failed ({candidate.Def?.Id}): {e.Message}"); }
                }
                if (selected == null)
                {
                    PsApi.Log(_log, $"cycle stop ({itemId}): no recipe matched inputs=[{DescribeItems(allItems)}]");
                    break;
                }

                // v0.5.2 输出槽空间预检 (满则跳过本批次, 材料保留);
                // v0.5.3: 原位机器跳过预检 (槽被材料占着必然误报满);
                // v0.5.7: 预检改探测法 (spawn→UncheckedAccept→Expel→Destroy)。
                //   原先的 TryFindOneValidInventorySlot 走原生过滤器, 非 MATERIAL 产出 (如报纸)
                //   会被误报"满"永久卡死配方; UncheckedAccept 与 CraftInside 的实际放置同路径, 不查过滤器。
                if (!inPlace)
                {
                    string outputId = selected.Def.Output.Id;
                    GameItem probeItem = null;
                    try { probeItem = DirectoryMaster.Item(outputId); }
                    catch { probeItem = null; }
                    if (probeItem != null && !ProbeOutputSpace(output, probeItem, itemId))
                    {
                        PsApi.Log(_log, $"cycle skip ({itemId}): output slot/grid full for {outputId}, preserving materials");
                        break;
                    }
                }

                // 品质规则: 在材料被 Craft 消耗前选定, 产物落地后应用
                QualityService.QualityDef chosen = SelectQuality(selected.Def, allItems);

                // 扣电 (电池不足不耗料)
                if (!TryConsumeCyclePower(machine, battery, spec))
                {
                    PsApi.Log(_log, $"cycle stop ({itemId}): insufficient power for {selected.Def?.Id}");
                    break;
                }

                var produced = CraftInside(selected.Recipe, output, allItems);
                if (produced == null || produced.Count == 0)
                {
                    PsApi.Warn(_log, $"cycle stop ({itemId}): craft produced nothing for {selected.Def?.Id}");
                    break;
                }
                ApplyQuality(produced, chosen);
                PsApi.Log(_log, $"cycle crafted ({itemId}): {selected.Def?.Id} -> {DescribeItems(produced)}");
                anyCrafted = true;
            }

            if (!anyCrafted) return;
            try { MachineHelper.OnMachineActioned(machine, MachineHelper.GetModuleInv(machine)); }
            catch (Exception e) { PsApi.Warn(_log, $"module action update failed ({itemId}): {e.Message}"); }
        }

        private static GameSlotInventory SafeGetBattery(GameItem machine)
        {
            try { return MachineHelper.GetBatterySlot(machine); }
            catch { return null; }
        }

        private static bool TryConsumeCyclePower(GameItem machine, GameSlotInventory battery, MachineDefJson spec)
        {
            if (spec != null && spec.BatteryPowered.HasValue && !spec.BatteryPowered.Value) return true;
            if (spec != null && spec.DrawnPower.HasValue)
            {
                int amount = Math.Max(0, spec.DrawnPower.Value);
                if (amount == 0) return true;
                if (battery == null || battery.childItems == null || battery.childItems.Count == 0) return false;
                var source = battery.childItems[0];
                return source != null && PowerHelper.DrawPowerSource(source, amount);
            }
            return battery != null && MachineHelper.TryDrawCyclePower(machine, battery);
        }

        private static Il2CppSystem.Collections.Generic.List<GameItem> CraftInside(CraftingRecipe recipe, GameInventory output, Il2CppSystem.Collections.Generic.List<GameItem> allItems)
        {
            var produced = recipe.Craft(allItems);
            if (produced == null || produced.Count == 0) return produced;
            foreach (var it in produced)
            {
                if (it == null || !output.UncheckedAccept(it))
                    PsApi.Warn(_log, "output inventory rejected crafted item (materials were already consumed)");
            }
            return produced;
        }

        private static QualityService.QualityDef SelectQuality(RecipeDefJson def, Il2CppSystem.Collections.Generic.List<GameItem> inputs)
        {
            string rule = (def?.QualityRule ?? "none").Trim().ToLowerInvariant();
            if (rule == "none" || inputs == null) return null;
            QualityService.QualityDef best = null;
            foreach (var it in inputs)
            {
                var q = QualityService.GetQuality(it);
                if (q == null) continue;
                if (rule == "inherit_frame") { best = q; break; }
                if (rule == "inherit_max_input" && (best == null || q.Tier > best.Tier)) best = q;
            }
            return best;
        }

        private static void ApplyQuality(Il2CppSystem.Collections.Generic.List<GameItem> produced, QualityService.QualityDef chosen)
        {
            if (chosen == null || produced == null || produced.Count == 0) return;
            foreach (var it in produced)
            {
                if (it != null) QualityService.SetQuality(it, it.identifier, chosen.Id);
            }
        }

        private static string DescribeItems(Il2CppSystem.Collections.Generic.List<GameItem> items)
        {
            if (items == null) return "null";
            var parts = new List<string>();
            foreach (var it in items)
            {
                try { parts.Add(it == null ? "null" : (it.identifier ?? "?")); }
                catch { parts.Add("?"); }
                if (parts.Count >= 8) { parts.Add("..."); break; }
            }
            return string.Join(",", parts);
        }

        // ==================== v0.5.7 输出槽空位探测 ====================

        /// <summary>
        /// 输出槽空位探测: spawn 一个产出物品 → UncheckedAccept 试放 → Expel + Destroy 回收。
        /// 与 CraftInside/打印机的实际放置完全同路径 (UncheckedAccept 不查原生过滤器),
        /// 探测结果即最终放置结果。
        /// 替代 v0.5.5 的 mayInventoryAddItemFunc 字段覆写: 该覆写破坏原生闭包委托组装协议
        /// (MachineDesequencer 等用 DisplayClass 闭包给槽位装配 mayAdd/mayRemove/onSlotAdd 等
        /// 一整套协作委托, ItemMouseDragHandler 拖拽管线依赖它们), 单独替换 mayAdd 为 interop
        /// 桥恒真谓词导致拖拽状态机中断 → 物品拿不起/ghost 图标残留。v0.5.7 整体撤销。
        /// </summary>
        internal static bool ProbeOutputSpace(GameInventory output, GameItem probeItem, string itemId)
        {
            try
            {
                if (!output.UncheckedAccept(probeItem))
                    return false; // 无空位
                // 有空位: 回收探测物
                if (!output.Expel(probeItem))
                    PsApi.Warn(_log, $"probe expel failed ({itemId}) — a probe item may linger in output slot");
                var list = new Il2CppSystem.Collections.Generic.List<GameItem>();
                list.Add(probeItem);
                GeneralHelper.DestroyGameItems(list);
                return true;
            }
            catch (Exception e)
            {
                PsApi.Warn(_log, $"probe failed ({itemId}): {e.Message} — assuming space available");
                return true; // 探测异常不阻塞: 放置失败时 CraftInside 自会 Warn
            }
        }

        // ==================== v0.5.4 打印机 spec 入口 ====================

        internal static bool TryGetMachineSpecPublic(string itemId, out MachineDefJson spec) => TryGetMachineSpec(itemId, out spec);

        // ==================== v0.5.11 ProgressRecipeService 入口 ====================

        internal static List<RecipeDefJson> GetRecipeDefsForMachine(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId)) return new List<RecipeDefJson>();
            return Recipes.Values.Where(r => SameIdentifier(ResolveMachineItem(r.Machine), itemId)).ToList();
        }

        internal static void SortRecipeDefs(List<RecipeDefJson> recipes, MachineDefJson spec, string itemId) => SortRecipes(recipes, spec, itemId);

        internal static CraftingRecipe CompileRecipe(RecipeDefJson def, List<object> pinned) => Compile(def, pinned);

        /// <summary>v0.5.12: 读取多个输入槽位的物品合并 (desequencer 槽2+槽3各放一种材料)。</summary>
        internal static Il2CppSystem.Collections.Generic.List<GameItem> GetInputItems(GameItem machine, MachineDefJson spec)
        {
            var result = new Il2CppSystem.Collections.Generic.List<GameItem>();
            if (machine == null || spec == null) return result;
            foreach (var kv in GetInputItemsBySlot(machine, spec))
                foreach (var item in kv.Value)
                    result.Add(item);
            return result;
        }

        /// <summary>v0.5.22: 按槽位分组读输入物品 (配方 slot 维度预检用)。键=槽位号 (同 GetElement(index,1) 编号)。</summary>
        internal static Dictionary<int, List<GameItem>> GetInputItemsBySlot(GameItem machine, MachineDefJson spec)
        {
            var bySlot = new Dictionary<int, List<GameItem>>();
            if (machine == null || spec == null) return bySlot;
            var slots = new List<int>();
            if (spec.InputSlots != null && spec.InputSlots.Count > 0)
                slots.AddRange(spec.InputSlots);
            else
                slots.Add(spec.InputSlot ?? 2);
            foreach (var idx in slots)
            {
                var list = new List<GameItem>();
                try
                {
                    var slot = SlotBridge.GetSlot(machine, idx);
                    if (slot?.childItems != null)
                        foreach (var item in slot.childItems)
                            if (item != null) list.Add(item);
                }
                catch { }
                bySlot[idx] = list;
            }
            return bySlot;
        }

        /// <summary>v0.5.22: 输入过滤器与物品匹配 (slot 预检用)。id 剥 game:/包前缀后比 identifier; tag 大写比 itemTypes。</summary>
        internal static bool FilterMatchesItem(FilterDefJson f, GameItem item)
        {
            if (f == null || item == null) return false;
            try
            {
                if (!string.IsNullOrWhiteSpace(f.Id))
                {
                    string want = f.Id;
                    if (want.StartsWith("game:", StringComparison.OrdinalIgnoreCase))
                        want = want.Substring("game:".Length);
                    string have = item.identifier;
                    if (string.Equals(have, want, StringComparison.OrdinalIgnoreCase)) return true;
                    int colon = want.LastIndexOf(':');
                    return colon >= 0 && string.Equals(have, want.Substring(colon + 1), StringComparison.OrdinalIgnoreCase);
                }
                if (!string.IsNullOrWhiteSpace(f.Tag))
                {
                    string wantTag = f.Tag.ToUpperInvariant();
                    var types = item.itemTypes;
                    if (types != null)
                        foreach (var t in types)
                            if (string.Equals(t, wantTag, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        // ==================== v0.5.3 启动自检 ====================

        /// <summary>
        /// LoadAll 末尾逐台体检。排查机器问题第一步就是看这些行:
        /// machine audit: {itemRef} ui=.. in=.. out=.. (in-place) power=.. battery=.. speed=.. recipes=N
        /// 打印机: machine audit: {itemRef} ui=.. printer(tpl=N blank=N out=N progress=N/N) recipes=N
        /// </summary>
        private static void Audit()
        {
            foreach (var kvp in MachineSpecsByItem)
            {
                string itemRef = kvp.Key;
                MachineDefJson spec = kvp.Value;
                int recipeCount = 0;
                foreach (var r in Recipes.Values)
                    if (SameIdentifier(ResolveMachineItem(r.Machine), itemRef)) recipeCount++;

                string line;
                if (spec.Printer != null)
                {
                    var p = spec.Printer;
                    line = $"machine audit: {itemRef} ui={spec.Ui ?? "-"} printer(tpl={p.Tpl} blank={p.Blank} out={p.Out} progress={p.PerNight}/{p.Max}) recipes={recipeCount}";
                }
                else if (spec.Progress != null)
                {
                    var p = spec.Progress;
                    int inIdx = spec.InputSlot ?? 2;
                    int outIdx = spec.OutputSlot ?? inIdx + 1;
                    line = $"machine audit: {itemRef} ui={spec.Ui ?? "-"} progress-recipe(in={inIdx} out={outIdx} progress={p.PerNight}/{p.Max})"
                         + $" power={(spec.DrawnPower.HasValue ? spec.DrawnPower.Value.ToString() : "native")}"
                         + $" battery={spec.BatteryPowered ?? true} recipes={recipeCount}"
                         + $" inWL={(spec.InputWhitelist?.Count ?? 0)} outWL={(spec.OutputWhitelist?.Count ?? 0)}";
                }
                else
                {
                    int inIdx = spec.InputSlot ?? 2;
                    int outIdx = spec.OutputSlot ?? inIdx + 1;
                    line = $"machine audit: {itemRef} ui={spec.Ui ?? "-"} in={inIdx} out={outIdx}{(inIdx == outIdx ? " (in-place)" : "")}"
                         + $" power={(spec.DrawnPower.HasValue ? spec.DrawnPower.Value.ToString() : "native")}"
                         + $" battery={spec.BatteryPowered ?? true} speed={Math.Max(1, spec.ProgressSpeed ?? 1)} sort={(spec.RecipeSort ?? "priority")} recipes={recipeCount}";
                }

                PsApi.Log(_log, line);

                // ---- 警告 ----
                if (string.IsNullOrWhiteSpace(spec.Ui) || string.Equals(spec.Ui, "none", StringComparison.OrdinalIgnoreCase))
                    PsApi.Warn(_log, $"machine audit: {itemRef} has no ui → built as plain item");
                else if (!ItemStore.TryGetDef(itemRef, out _))
                    PsApi.Warn(_log, $"machine audit: {itemRef} has no items/*.json definition → never built");
                if (spec.Printer == null && recipeCount == 0)
                    PsApi.Warn(_log, $"machine audit: {itemRef} has no recipes → native behaviour only" +
                        (HasNativeMachineUi(itemRef) ? "" : " (and no ui → not a machine at all)"));
                if (spec.Toggleable.HasValue)
                    PsApi.Warn(_log, $"machine audit: {itemRef} sets 'toggleable' — parsed but wired to nothing (dead field)");
                if (spec.Printer != null && recipeCount > 0)
                    PsApi.Warn(_log, $"machine audit: {itemRef} declares both printer and recipes — printer slot model ignores recipes");

                // 槽位布局 vs 原版布局 (有配方的配方机才检查; printer 自管槽位)
                if (spec.Printer == null && recipeCount > 0 && !string.IsNullOrWhiteSpace(spec.Ui))
                {
                    int inIdx = spec.InputSlot ?? 2;
                    int outIdx = spec.OutputSlot ?? inIdx + 1;
                    string ui = spec.Ui.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "");
                    bool layoutOk = true;
                    if (ui == "furnace" && !(inIdx == 2 && outIdx == 3)) layoutOk = false;
                    if (ui == "desequencer" && !((inIdx == 2 || inIdx == 3) && outIdx == 4)) layoutOk = false;
                    if ((ui == "purifier" || ui == "water_purifier" || ui == "waterpurifier") && !(inIdx == 2 && outIdx == 2)) layoutOk = false;
                    if (!layoutOk)
                        PsApi.Warn(_log, $"machine audit: {itemRef} slot layout in={inIdx} out={outIdx} deviates from native {ui} layout — materials may not enter or leave the machine");

                    // 配方输入 tag 进不了槽位 (原生过滤器白名单; id 输入无法静态判定, 不检查)
                    foreach (var r in Recipes.Values)
                    {
                        if (!SameIdentifier(ResolveMachineItem(r.Machine), itemRef) || r.Inputs == null) continue;
                        foreach (var input in r.Inputs)
                        {
                            if (input == null || string.IsNullOrWhiteSpace(input.Tag)) continue;
                            string tag = input.Tag.ToUpperInvariant();
                            string expected = ui == "furnace" ? "MATERIAL"
                                : (ui == "desequencer" && inIdx == 2) ? "DESEQUENCER_TARGET_ID_TAG"
                                : (ui == "desequencer" && inIdx == 3) ? "ACCESS_CARD"
                                : (ui == "purifier" || ui == "water_purifier" || ui == "waterpurifier") ? "LIQUID_CONTAINER_TAG"
                                : null;
                            if (expected == null) continue;
                            if (!string.Equals(tag, expected, StringComparison.OrdinalIgnoreCase))
                                PsApi.Warn(_log, $"machine audit: recipe {r.Id} input tag {tag} cannot enter {ui} slot {inIdx} (native filter only accepts {expected})");
                        }
                    }
                }
            }

            // 配方 machine 无声明
            foreach (var r in Recipes.Values)
            {
                var resolved = ResolveMachineItem(r.Machine);
                if (!MachineSpecsByItem.ContainsKey(resolved) && !MachineItemRefs.ContainsValue(resolved))
                    PsApi.Warn(_log, $"machine audit: recipe {r.Id} machine '{r.Machine}' has no machines/*.json declaration → recipe never runs");
            }
        }

        // ==================== 辅助 ====================

        private static string ResolveMachineItem(string machine)
        {
            if (string.IsNullOrWhiteSpace(machine)) return null;
            return MachineItemRefs.TryGetValue(machine, out var item) ? item : machine;
        }

        private static bool SameIdentifier(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            return a.EndsWith(":" + b, StringComparison.OrdinalIgnoreCase)
                || b.EndsWith(":" + a, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeId(string id, string packId)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            id = id.Trim();
            return id.Contains(":", StringComparison.Ordinal) ? id : packId + ":" + id;
        }

        /// <summary>game: 前缀 (大小写不敏感) = 原版裸 id, 剥离前缀; 其余走包命名空间 (v0.3.2)。</summary>
        private static string ResolveItemId(string id, string packId)
        {
            if (string.IsNullOrWhiteSpace(id)) return id;
            id = id.Trim();
            if (id.StartsWith("game:", StringComparison.OrdinalIgnoreCase))
                return id.Substring("game:".Length);
            return NormalizeId(id, packId);
        }

        // ==================== JSON 数据模型 ====================

        private sealed class RecipeFile { [JsonPropertyName("recipes")] public List<RecipeDefJson> Recipes { get; set; } }
        private sealed class MachineFile { [JsonPropertyName("machines")] public List<MachineDefJson> Machines { get; set; } }

        internal sealed class MachineDefJson
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("itemRef")] public string ItemRef { get; set; }
            [JsonPropertyName("ui")] public string Ui { get; set; }
            [JsonPropertyName("drawnPower")] public int? DrawnPower { get; set; }
            [JsonPropertyName("progressSpeed")] public int? ProgressSpeed { get; set; }   // ⚠ 每晚最多批次数, 不是速度
            [JsonPropertyName("batteryPowered")] public bool? BatteryPowered { get; set; }
            [JsonPropertyName("toggleable")] public bool? Toggleable { get; set; }         // ⚠ 死字段: 只解析不生效
            [JsonPropertyName("inputSlot")] public int? InputSlot { get; set; }
            [JsonPropertyName("inputSlots")] public List<int> InputSlots { get; set; } // v0.5.12: 多输入槽位 (desequencer 槽2+槽3各放一种材料)
            [JsonPropertyName("outputSlot")] public int? OutputSlot { get; set; }
            [JsonPropertyName("inputSize")] public string InputSize { get; set; }     // v0.6.1: ui=custom 输入槽尺寸 "WxH" (如 "3x4"), 缺省 3x4 (须容纳 3x2 废金属)
            [JsonPropertyName("outputSize")] public string OutputSize { get; set; }   // v0.6.1: ui=custom 输出槽尺寸 "WxH", 缺省 2x2
            [JsonPropertyName("inputKind")] public string InputKind { get; set; }     // v0.6.4: ui=custom 输入槽类型 "slot"(默认,单物品)|"grid"(网格,可放多物品, 仿原版熔炉输入格)
            [JsonPropertyName("outputKind")] public string OutputKind { get; set; }   // v0.6.4: ui=custom 输出槽类型, 同上
            [JsonPropertyName("ignoreOutputFilter")] public bool? IgnoreOutputFilter { get; set; }  // v0.5.5 死字段: v0.5.7 起不再消费 (槽位覆写已撤销), 输出预检改探测法天然放行非 MATERIAL 产出
            [JsonPropertyName("recipeSort")] public string RecipeSort { get; set; }              // v0.5.8: 配方选择策略 "priority"(默认)|"value"|"firstMatch"
            [JsonPropertyName("printer")] public PrinterDefJson Printer { get; set; }              // v0.5.4: 模组打印机
            [JsonPropertyName("inputWhitelist")] public List<string> InputWhitelist { get; set; }   // v0.5.11: 输入槽白名单 (Harmony patch 放宽 mayAdd, 放行额外物品 id)
            [JsonPropertyName("outputWhitelist")] public List<string> OutputWhitelist { get; set; } // v0.5.11: 输出槽白名单
            [JsonPropertyName("slotWhitelists")] public List<SlotWhitelistDefJson> SlotWhitelists { get; set; } // v0.5.12: 额外槽位白名单 (多输入槽)
            [JsonPropertyName("progress")] public ProgressDefJson Progress { get; set; }            // v0.5.11: 进度+配方结合的多夜加工
            [JsonPropertyName("panel")] public string Panel { get; set; }                  // v0.8.0: ui="psui" 时绑定的 PSUI 面板短名 (同包 ui/*.psui; 可写 "pack:panel" 全限定)
        }

        internal sealed class SlotWhitelistDefJson
        {
            [JsonPropertyName("slot")] public int? Slot { get; set; }
            [JsonPropertyName("whitelist")] public List<string> Whitelist { get; set; }
        }

        internal sealed class ProgressDefJson
        {
            [JsonPropertyName("progressPerNight")] public int? ProgressPerNight { get; set; }
            [JsonPropertyName("progressMax")] public int? ProgressMax { get; set; }

            internal int PerNight => ProgressPerNight ?? 25;
            internal int Max => ProgressMax ?? 100;
        }

        internal sealed class PrinterDefJson
        {
            [JsonPropertyName("templateSlot")] public int? TemplateSlot { get; set; }
            [JsonPropertyName("blankSlot")] public int? BlankSlot { get; set; }
            [JsonPropertyName("outputSlot")] public int? OutputSlot { get; set; }
            [JsonPropertyName("progressPerNight")] public int? ProgressPerNight { get; set; }
            [JsonPropertyName("progressMax")] public int? ProgressMax { get; set; }
            [JsonPropertyName("randomEffects")] public int? RandomEffects { get; set; }

            internal int Tpl => TemplateSlot ?? 2;
            internal int Blank => BlankSlot ?? 3;
            internal int Out => OutputSlot ?? 4;
            internal int PerNight => ProgressPerNight ?? 25;
            internal int Max => ProgressMax ?? 100;
        }

        internal sealed class RecipeDefJson
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("machine")] public string Machine { get; set; }
            [JsonPropertyName("output")] public OutputDefJson Output { get; set; }
            [JsonPropertyName("inputs")] public List<FilterDefJson> Inputs { get; set; }
            [JsonPropertyName("tools")] public List<FilterDefJson> Tools { get; set; }
            [JsonPropertyName("qualityRule")] public string QualityRule { get; set; }
            [JsonPropertyName("customName")] public string CustomName { get; set; }
            [JsonPropertyName("description")] public string Description { get; set; }
            [JsonPropertyName("uses")] public int? Uses { get; set; }
            [JsonPropertyName("priority")] public int? Priority { get; set; }   // v0.5.8: 配方优先级, 数值越大越优先 (默认 0)
        }

        internal sealed class OutputDefJson
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("count")] public int Count { get; set; } = 1;
        }

        internal sealed class FilterDefJson
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("tag")] public string Tag { get; set; }
            [JsonPropertyName("count")] public int Count { get; set; } = 1;
            [JsonPropertyName("display")] public string Display { get; set; }
            [JsonPropertyName("slot")] public int? Slot { get; set; }   // v0.5.22: 限定输入来自指定槽位 (进度机 SelectMatchingRecipe 预检; 批次机不消费)
        }

        private sealed class CompiledRecipe
        {
            public readonly RecipeDefJson Def;
            public readonly CraftingRecipe Recipe;
            public CompiledRecipe(RecipeDefJson def, CraftingRecipe recipe) { Def = def; Recipe = recipe; }
        }
    }
}
