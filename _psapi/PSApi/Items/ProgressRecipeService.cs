using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 进度+配方结合的多夜加工 (v0.5.11): 像 ModulePrinterService 那样多夜累积进度，
    /// 但产出由 recipes/*.json 配方定义 (输入材料 → 输出物品)，而非"模板槽复制"。
    ///
    /// 工作流程:
    /// 1. 每夜读输入槽物品 → 遍历配方找第一个 CanCraft 的 → 确定产出
    /// 2. 累积进度 += progressPerNight
    /// 3. 进度 >= progressMax 时: 消耗材料 + 产出到输出槽 + 进度归零
    /// 4. 没匹配到配方则不涨进度
    ///
    /// 用途: 基于 desequencer 的自定义读卡器，支持自定义配方 + 多夜进度 + 放宽槽位过滤器。
    /// 进度存机器 state TagSystem (PSAPI_PROGRESS_RECIPE_TAG)，随存档保留。
    ///
    /// 隔夜驱动: onCycleEndSlotItemFunc 委托 (与 RecipeService/ModulePrinterService 同机制)。
    /// 存档重载: 委托不序列化 → SaveLoadPatches.ReattachOne 重挂。
    /// </summary>
    internal static class ProgressRecipeService
    {
        private const string ProgressTag = "PSAPI_PROGRESS_RECIPE";
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) => _log = log;

        /// <summary>该机器是否声明了 progress 配置 (SaveLoadPatches 重挂判定用)。</summary>
        internal static bool HasProgress(string itemId)
        {
            return !string.IsNullOrWhiteSpace(itemId)
                && RecipeService.TryGetMachineSpecPublic(itemId, out var spec)
                && spec.Progress != null;
        }

        // ==================== 挂载 ====================

        /// <summary>
        /// BuildItem / 存档重挂两处调用。安装隔夜回调。
        /// 幂等: 重复调用只是覆盖回调，无累积副作用。
        /// </summary>
        internal static bool TryAttach(GameItem item, string itemId, List<object> pinned)
        {
            if (item == null || string.IsNullOrWhiteSpace(itemId)) return false;
            if (!RecipeService.TryGetMachineSpecPublic(itemId, out var spec) || spec.Progress == null) return false;

            var capturedSpec = spec;
            var capturedId = itemId;
            var managed = (Action<GameItem, GameInventory, SlotMarker>)((machine, _, __) =>
            {
                try { RunCycle(machine ?? item, capturedId, capturedSpec); }
                catch (Exception e) { PsApi.Warn(_log, $"progress-recipe cycle failed ({capturedId}): {e.Message}"); }
            });
            var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameItem, GameInventory, SlotMarker>>(managed);
            pinned.Add(managed);
            pinned.Add(callback);
            try { item.onCycleEndSlotItemFunc = callback; }
            catch (Exception e) { PsApi.Warn(_log, $"progress-recipe callback install failed ({itemId}): {e.Message}"); return false; }

            try
            {
                bool early = item.onCycleEndEarlySlotItemFunc != null;
                bool late = item.onCycleEndLateSlotItemFunc != null;
                PsApi.Log(_log, $"progress-recipe attach diag: {itemId} vanillaEarly={early} vanillaLate={late}");
            }
            catch { }

            // v0.5.18: 材料取出 → 进度清零 (每实例只挂一次, 避免 BuildItem+Reattach 重复链)
            try
            {
                if (_removeHooked.Add(item.Pointer))
                {
                    Il2CppSystem.Action<GameItem, GameInventory, SlotMarker> prevRemove = null;
                    try { prevRemove = item.onSlotRemoveItemFunc; } catch { }
                    var capturedMachine = item;
                    var managedRemove = (Action<GameItem, GameInventory, SlotMarker>)((removedItem, parentInv, slot) =>
                    {
                        try { prevRemove?.Invoke(removedItem, parentInv, slot); } catch { }
                        try { OnInputRemoved(capturedMachine, capturedId, capturedSpec, parentInv); }
                        catch (Exception e) { PsApi.Warn(_log, $"progress-recipe remove-reset failed ({capturedId}): {e.Message}"); }
                    });
                    var removeCallback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameItem, GameInventory, SlotMarker>>(managedRemove);
                    pinned.Add(managedRemove);
                    pinned.Add(removeCallback);
                    item.onSlotRemoveItemFunc = removeCallback;
                }
            }
            catch (Exception e) { PsApi.Warn(_log, $"progress-recipe remove hook install failed ({itemId}): {e.Message}"); }

            // v0.5.21: 读档重挂后按进度恢复输入槽锁定 (锁状态未必随存档持久)
            try { ApplyInputLock(item, spec, GetProgress(item) > 0); } catch { }

            PsApi.Log(_log, $"progress-recipe attached: {itemId} progress={spec.Progress.PerNight}/{spec.Progress.Max}");
            return true;
        }

        private static readonly HashSet<IntPtr> _removeHooked = new HashSet<IntPtr>();

        /// <summary>v0.9.4: 新读档时清托管进度表+移除钩子表 —— uniqueId 是档内自增小整数,
        /// 同进程"读档A→回菜单→读档B"必撞 uid, 不清则 B 档机器继承 A 档进度并回写污染新档;
        /// _removeHooked 是原生指针键, 指针复用后新实例会装不上"取材料清进度"钩子。</summary>
        internal static void ClearRuntimeState()
        {
            _progress.Clear();
            _removeHooked.Clear();
        }

        /// <summary>物品从机器槽位移除时: 若是输入槽且进度>0 → 清零。</summary>
        private static void OnInputRemoved(GameItem machine, string itemId, RecipeService.MachineDefJson spec, GameInventory parentInv)
        {
            if (machine == null || parentInv == null) return;
            if (GetProgress(machine) <= 0) return;
            var slots = new List<int>();
            if (spec.InputSlots != null && spec.InputSlots.Count > 0) slots.AddRange(spec.InputSlots);
            else slots.Add(spec.InputSlot ?? 2);
            foreach (var idx in slots)
            {
                GameInventory s = null;
                try { s = SlotBridge.GetSlot(machine, idx); } catch { }
                if (s != null && s.Pointer == parentInv.Pointer)
                {
                    SetProgress(machine, 0, spec);
                    PsApi.Log(_log, $"progress-recipe reset: material removed from input slot {idx} ({itemId})");
                    return;
                }
            }
        }

        // ==================== 隔夜周期 ====================

        /// <summary>材料变动复位入口 (v0.6.0, CustomMachineFactory 槽位 onSlotAdd/onSlotRemove 委托调用):
        /// 有进度配置且进度>0 的机器进度清零并解锁输入槽; 无进度配置 (批次机) 为 no-op。</summary>
        internal static void ResetProgress(GameItem machine, string itemId)
        {
            if (machine == null || string.IsNullOrWhiteSpace(itemId)) return;
            if (!HasProgress(itemId)) return;
            if (GetProgress(machine) <= 0) return;
            if (!RecipeService.TryGetMachineSpecPublic(itemId, out var spec)) return;
            SetProgress(machine, 0, spec);
            PsApi.Log(_log, $"progress-recipe reset: input materials changed ({itemId})");
        }

        private static void RunCycle(GameItem machine, string itemId, RecipeService.MachineDefJson spec)
        {
            var p = spec.Progress;
            int curProgress = GetProgress(machine);
            PsApi.Log(_log, $"progress-recipe cycle fired: {itemId} progress={curProgress}/{p.Max}");
            PsApi.Log(_log, $"progress-recipe cycle diag: {itemId} tags(mod)=[{DumpTags(machine, true)}] tags(state)=[{DumpTags(machine, false)}]");

            int inputIndex = spec.InputSlot ?? 2;
            int outputIndex = spec.OutputSlot ?? inputIndex + 1;

            GameInventory output;
            try { output = SlotBridge.GetSlot(machine, outputIndex); }
            catch (Exception e)
            {
                PsApi.Warn(_log, $"progress-recipe output slot unavailable ({itemId}, out={outputIndex}): {e.Message}");
                return;
            }
            if (output == null)
            {
                PsApi.Warn(_log, $"progress-recipe aborted ({itemId}): output slot null");
                return;
            }

            var allItems = RecipeService.GetInputItems(machine, spec);
            if (allItems == null || allItems.Count == 0)
            {
                PsApi.Log(_log, $"progress-recipe idle ({itemId}): input empty");
                return;
            }

            var selectedDef = SelectMatchingRecipe(machine, allItems, itemId, spec, out var selectedRecipe);
            if (selectedDef == null || selectedRecipe == null)
            {
                PsApi.Log(_log, $"progress-recipe idle ({itemId}): no recipe matched inputs0");
                return;
            }

            if (!TryConsumePower(machine, spec))
            {
                PsApi.Log(_log, $"progress-recipe stop ({itemId}): insufficient power");
                return;
            }

            int newProgress = curProgress + p.PerNight;
            if (newProgress < p.Max)
            {
                SetProgress(machine, newProgress, spec);
                PsApi.Log(_log, $"progress-recipe progress: {newProgress}/{p.Max} ({itemId})");
                return;
            }

            // 加工中输入槽已 LockInv, 先解锁再让 Craft 消耗材料, 失败则回锁
            ApplyInputLock(machine, spec, false);
            var produced = selectedRecipe.Craft(allItems);
            if (produced == null || produced.Count == 0)
            {
                ApplyInputLock(machine, spec, true);
                PsApi.Warn(_log, $"progress-recipe craft produced nothing ({itemId}, {selectedDef.Id})");
                return;
            }

            foreach (var it in produced)
            {
                if (it == null || !output.UncheckedAccept(it))
                    PsApi.Warn(_log, $"progress-recipe output rejected ({itemId}) — materials already consumed");
            }

            SetProgress(machine, 0, spec);
            PsApi.Log(_log, $"progress-recipe crafted: {selectedDef.Id} -> {DescribeItems(produced)} ({itemId})");

            try { MachineHelper.OnMachineActioned(machine, MachineHelper.GetModuleInv(machine)); }
            catch (Exception e) { PsApi.Warn(_log, $"progress-recipe module action update failed ({itemId}): {e.Message}"); }
        }

        private static bool TryConsumePower(GameItem machine, RecipeService.MachineDefJson spec)
        {
            if (spec.BatteryPowered.HasValue && !spec.BatteryPowered.Value) return true;
            GameSlotInventory battery;
            try { battery = MachineHelper.GetBatterySlot(machine); }
            catch { battery = null; }
            if (spec.DrawnPower.HasValue)
            {
                int amount = Math.Max(0, spec.DrawnPower.Value);
                if (amount == 0) return true;
                if (battery == null || battery.childItems == null || battery.childItems.Count == 0) return false;
                var source = battery.childItems[0];
                return source != null && PowerHelper.DrawPowerSource(source, amount);
            }
            return battery != null && MachineHelper.TryDrawCyclePower(machine, battery);
        }

        /// <summary>选配方: 排序后首个 CanCraft 命中 (RunCycle 与 tooltip 共用)。recipe 仅本次调用内有效, 不缓存。
        /// v0.5.22: 带 slot 约束的输入先做按槽预检 (FilterDefJson.slot), 再交给 CanCraft 验总数。</summary>
        private static RecipeService.RecipeDefJson SelectMatchingRecipe(
            GameItem machine, Il2CppSystem.Collections.Generic.List<GameItem> allItems, string itemId, RecipeService.MachineDefJson spec,
            out CraftingRecipe recipe)
        {
            recipe = null;
            var matching = RecipeService.GetRecipeDefsForMachine(itemId);
            if (matching.Count == 0) return null;
            var tmpPinned = new List<object>();
            RecipeService.SortRecipeDefs(matching, spec, itemId);
            Dictionary<int, List<GameItem>> bySlot = null;
            foreach (var def in matching)
            {
                try
                {
                    if (HasSlotConstraints(def))
                    {
                        if (bySlot == null) bySlot = RecipeService.GetInputItemsBySlot(machine, spec);
                        if (!SlotPreCheck(def, bySlot)) continue;
                    }
                    var r = RecipeService.CompileRecipe(def, tmpPinned);
                    if (r != null && r.CanCraft(allItems))
                    {
                        recipe = r;
                        return def;
                    }
                }
                catch { }
            }
            return null;
        }

        private static bool HasSlotConstraints(RecipeService.RecipeDefJson def)
        {
            if (def?.Inputs == null) return false;
            foreach (var f in def.Inputs)
                if (f?.Slot != null) return true;
            return false;
        }

        /// <summary>slot 预检: 每个带 slot 的过滤器必须在对应槽位内凑够 count。</summary>
        private static bool SlotPreCheck(RecipeService.RecipeDefJson def, Dictionary<int, List<GameItem>> bySlot)
        {
            foreach (var f in def.Inputs)
            {
                if (f?.Slot == null) continue;
                if (!bySlot.TryGetValue(f.Slot.Value, out var items) || items.Count == 0) return false;
                int need = f.Count <= 0 ? 1 : f.Count;
                int have = 0;
                foreach (var it in items)
                    if (RecipeService.FilterMatchesItem(f, it)) have++;
                if (have < need) return false;
            }
            return true;
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

        // ==================== 进度存取 ====================

        /// <summary>v0.5.20: 托管进度表 (key=uniqueId, 存档序列化、跨读档稳定, SaveLoadPatches 同键)。
        /// 原版在任意槽位内容变化时重建 modifiedState (电池槽也连坐, §-1.12 甄别测试实证) → TagSystem 标签不再可信,
        /// 托管表才是真相源; 标签只作存档载体 best-effort 回写。</summary>
        private static readonly Dictionary<int, int> _progress = new Dictionary<int, int>();

        private static int GetUid(GameItem machine)
        {
            try { return machine?.uniqueId ?? 0; } catch { return 0; }
        }

        internal static int GetProgress(GameItem machine)
        {
            int uid = GetUid(machine);
            if (uid != 0 && _progress.TryGetValue(uid, out var v))
            {
                HealTag(machine, v);
                return v;
            }
            int tagVal = ReadTag(machine);
            if (uid != 0 && tagVal > 0) _progress[uid] = tagVal; // 读档后托管表为空, 从存档标签播种
            return tagVal;
        }

        private static int ReadTag(GameItem machine)
        {
            try
            {
                var tag = machine.modifiedState?.GetTag(ProgressTag);
                if (tag == null) tag = machine.state?.GetTag(ProgressTag);
                return tag?.valueInt ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>标签被原版清掉时顺手回写 (值一致则不动)。</summary>
        private static void HealTag(GameItem machine, int value)
        {
            try
            {
                if (ReadTag(machine) == value) return;
                WriteTag(machine, value);
            }
            catch { }
        }

        private static void WriteTag(GameItem machine, int value)
        {
            var ts = machine.modifiedState;
            if (ts == null)
            {
                ts = new TagSystem();
                machine.modifiedState = ts;
            }
            ts.InitTagString(ProgressTag);
            var tag = ts.GetTag(ProgressTag);
            if (tag != null) tag.SetInt(value);
        }

        /// <summary>进度写托管表 (真相源) + 标签 (存档载体); 写完读回校验。v0.5.21: 进度>0 锁定输入槽, =0 解锁 (仿原版 desequencer 加工中锁料)。</summary>
        private static void SetProgress(GameItem machine, int value, RecipeService.MachineDefJson spec = null)
        {
            try
            {
                int uid = GetUid(machine);
                if (uid != 0) _progress[uid] = value;
                WriteTag(machine, value);
                if (spec != null) ApplyInputLock(machine, spec, value > 0);
                int verify = GetProgress(machine);
                if (verify != value) PsApi.Warn(_log, $"progress-recipe write verify mismatch: wrote {value} read {verify}");
            }
            catch (Exception e) { PsApi.Warn(_log, $"progress-recipe write failed: {e.Message}"); }
        }

        /// <summary>输入槽 LockInv/UnlockInv (原版 GameInventory API)。锁定后材料不可取出, 直到加工完成进度归零。</summary>
        private static void ApplyInputLock(GameItem machine, RecipeService.MachineDefJson spec, bool locked)
        {
            if (machine == null || spec == null) return;
            var slots = new List<int>();
            if (spec.InputSlots != null && spec.InputSlots.Count > 0) slots.AddRange(spec.InputSlots);
            else slots.Add(spec.InputSlot ?? 2);
            foreach (var idx in slots)
            {
                try
                {
                    var s = SlotBridge.GetSlot(machine, idx);
                    if (s == null) continue;
                    if (locked) s.LockInv(); else s.UnlockInv();
                }
                catch { }
            }
        }

        private static string DumpTags(GameItem machine, bool modified)
        {
            try
            {
                var ts = modified ? machine.modifiedState : machine.state;
                if (ts == null) return "null";
                var dict = ts.dict;
                if (dict == null || dict.Count == 0) return "empty";
                var keys = new List<string>();
                foreach (var kv in dict) keys.Add(kv.Key);
                if (keys.Count > 12) return string.Join(",", keys.GetRange(0, 12)) + $"...(+{keys.Count - 12})";
                return string.Join(",", keys);
            }
            catch (Exception e) { return "err:" + e.Message; }
        }

        /// <summary>tooltip 进度行 (ModHook.OnCreateTooltipLate 调用; 非进度机器返回 false)。</summary>
        internal static bool TryGetProgressLine(GameItem item, out string line)
        {
            line = null;
            if (item == null) return false;
            string id;
            try { id = item.identifier; } catch { return false; }
            if (!HasProgress(id)) return false;
            if (!RecipeService.TryGetMachineSpecPublic(id, out var spec)) return false;
            int progress = GetProgress(item);
            line = progress > 0
                ? $"加工进度: {progress}/{spec.Progress.Max} (加工中, 材料已锁定)"
                : $"加工进度: {progress}/{spec.Progress.Max}";
            return true;
        }

        /// <summary>tooltip "正在生产" 行 (v0.5.18): 按当前输入槽材料匹配配方, 显示产物品名; 无匹配不显示。</summary>
        internal static bool TryGetProducingLine(GameItem item, out string line)
        {
            line = null;
            if (item == null) return false;
            string id;
            try { id = item.identifier; } catch { return false; }
            if (!HasProgress(id)) return false;
            if (!RecipeService.TryGetMachineSpecPublic(id, out var spec)) return false;

            Il2CppSystem.Collections.Generic.List<GameItem> allItems = null;
            try { allItems = RecipeService.GetInputItems(item, spec); } catch { return false; }
            if (allItems == null || allItems.Count == 0) return false;

            RecipeService.RecipeDefJson def = null;
            try { def = SelectMatchingRecipe(item, allItems, id, spec, out _); } catch { return false; }
            if (def == null) return false;

            line = $"正在生产: {ResolveOutputName(def)}";
            return true;
        }

        /// <summary>E5 Facade 用: 当前输入槽材料匹配的配方产出 id; 无匹配/非进度机器 = false。</summary>
        internal static bool TryGetProducingOutput(GameItem item, out string outputId)
        {
            outputId = null;
            if (item == null) return false;
            string id;
            try { id = item.identifier; } catch { return false; }
            if (!HasProgress(id)) return false;
            if (!RecipeService.TryGetMachineSpecPublic(id, out var spec)) return false;

            Il2CppSystem.Collections.Generic.List<GameItem> allItems = null;
            try { allItems = RecipeService.GetInputItems(item, spec); } catch { return false; }
            if (allItems == null || allItems.Count == 0) return false;

            RecipeService.RecipeDefJson def = null;
            try { def = SelectMatchingRecipe(item, allItems, id, spec, out _); } catch { return false; }
            if (def == null) return false;
            outputId = def.Output?.Id;
            return !string.IsNullOrWhiteSpace(outputId);
        }

        private static readonly Dictionary<string, string> _outputNameCache = new Dictionary<string, string>();

        private static string ResolveOutputName(RecipeService.RecipeDefJson def)
        {
            string id = def?.Output?.Id;
            if (string.IsNullOrWhiteSpace(id)) return "?";
            if (_outputNameCache.TryGetValue(id, out var cached)) return cached;
            string name = null;
            try
            {
                string loc = LocHelper.GetLocalizedItem($"item_{id}_name");
                // v2.0.7: 未注册键原版返回 "Translation Error" 错误字符串而非抛异常, 须剔除
                if (!string.IsNullOrWhiteSpace(loc) && loc != $"item_{id}_name" && !LocService.IsTranslationError(loc)) name = loc;
            }
            catch { }
            if (string.IsNullOrWhiteSpace(name)) name = id;
            _outputNameCache[id] = name;
            return name;
        }
    }
}