using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 模组打印机 (v0.5.4): machines/*.json 声明 printer 字段的多夜进度机器，独立于配方管线。
    ///
    /// 模型: 模板槽放任意功能模组 (如 system_module_efficiancy)，空白槽放打印材料
    /// (如 blank_module)，每晚进度 +progressPerNight；累计到 progressMax 时产出同型模组
    /// 复制品到输出槽并消耗空白材料，进度归零。进度存机器自身 TagSystem
    /// (PSAPI_PRINTER_PROGRESS, 原生机器 state.dict 即存档序列化载体 — 探针 §9h
    /// desequencer state.dict count=22 实证)，随存档保留。
    ///
    /// ⚠ v0.5.7 已知限制: 不再覆写槽位过滤器 (mayInventoryAddItemFunc 覆写会破坏原生
    /// 闭包委托组装协议 → 拖拽系统失灵, 详见 RecipeService.ProbeOutputSpace 注释)。
    /// desequencer 模板槽位原生只收芯片/门禁卡, 玩家无法手动把模组放进 2/3 号槽 —
    /// 打印机当前只能处理已在槽内的物品 (控制台/存档修改器放入)。槽位交互待原生
    /// 委托协议调研后再开放。
    /// randomEffects: N → 打印成品附加 N 层随机已注册品质(无品质注册则跳过)。
    ///
    /// 隔夜驱动: onCycleEndSlotItemFunc 委托替换原生闭包(与 RecipeService 配方机同机制，
    /// keycard_writer/chip_desequencer 已实测 EndNight 会回调该字段)。委托 pin 进 pinned 列表防 GC。
    /// 存档重载: 委托不序列化 → SaveLoadPatches.ReattachOne 重挂(见 save reattach: printer 日志)。
    /// </summary>
    internal static class ModulePrinterService
    {
        private const string ProgressTag = "PSAPI_PRINTER_PROGRESS";
        private static MelonLogger.Instance _log;
        private static readonly Random _rng = new Random();

        internal static void Init(MelonLogger.Instance log) => _log = log;

        /// <summary>该物品是否声明了 printer (SaveLoadPatches 重挂判定用)。</summary>
        internal static bool IsPrinter(string itemId)
        {
            return !string.IsNullOrWhiteSpace(itemId)
                && RecipeService.TryGetMachineSpecPublic(itemId, out var spec)
                && spec.Printer != null;
        }

        // ==================== 挂载 ====================

        /// <summary>
        /// BuildItem / 存档重挂两处调用。安装隔夜回调 + 覆写三槽过滤器。
        /// 幂等: 重复调用只是覆盖回调与过滤器，无累积副作用。
        /// </summary>
        internal static bool TryAttach(GameItem item, string itemId, List<object> pinned)
        {
            if (item == null || string.IsNullOrWhiteSpace(itemId)) return false;
            if (!RecipeService.TryGetMachineSpecPublic(itemId, out var spec) || spec.Printer == null) return false;

            var capturedSpec = spec;
            var capturedId = itemId;
            var managed = (Action<GameItem, GameInventory, SlotMarker>)((machine, _, __) =>
            {
                try { RunCycle(machine ?? item, capturedId, capturedSpec); }
                catch (Exception e) { PsApi.Warn(_log, $"printer cycle failed ({capturedId}): {e.Message}"); }
            });
            var callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameItem, GameInventory, SlotMarker>>(managed);
            pinned.Add(managed);
            pinned.Add(callback);
            try { item.onCycleEndSlotItemFunc = callback; }
            catch (Exception e) { PsApi.Warn(_log, $"printer cycle callback install failed ({itemId}): {e.Message}"); return false; }

            PsApi.Log(_log, $"printer attached: {itemId} tpl={spec.Printer.Tpl} blank={spec.Printer.Blank} out={spec.Printer.Out} progress={spec.Printer.PerNight}/{spec.Printer.Max}");
            return true;
        }

        // ==================== 隔夜周期 ====================

        private static void RunCycle(GameItem machine, string itemId, RecipeService.MachineDefJson spec)
        {
            var p = spec.Printer;
            PsApi.Log(_log, $"printer cycle fired: {itemId} progress={GetProgress(machine)}/{p.Max}");

            GameInventory tplSlot, blankSlot, outSlot;
            try
            {
                tplSlot = SlotBridge.GetSlot(machine, p.Tpl);
                blankSlot = SlotBridge.GetSlot(machine, p.Blank);
                outSlot = SlotBridge.GetSlot(machine, p.Out);
            }
            catch (Exception e)
            {
                PsApi.Warn(_log, $"printer slots unavailable ({itemId}, tpl={p.Tpl} blank={p.Blank} out={p.Out}): {e.Message}");
                return;
            }
            if (tplSlot == null || blankSlot == null || outSlot == null)
            {
                PsApi.Warn(_log, $"printer aborted ({itemId}): slot null tpl={tplSlot == null} blank={blankSlot == null} out={outSlot == null}");
                return;
            }

            GameItem template = FirstChild(tplSlot);
            GameItem blank = FirstChild(blankSlot);
            if (template == null || blank == null)
            {
                PsApi.Log(_log, $"printer idle ({itemId}): template={(template == null ? "missing" : template.identifier)} blank={(blank == null ? "missing" : blank.identifier)}");
                return;
            }

            int progress = GetProgress(machine) + p.PerNight;
            bool complete = progress >= p.Max;

            // 扣电 (不足则整夜跳过, 不涨进度)
            if (!TryConsumePower(machine, spec))
            {
                PsApi.Log(_log, $"printer stop ({itemId}): insufficient power");
                return;
            }

            if (!complete)
            {
                SetProgress(machine, progress);
                PsApi.Log(_log, $"printer progress: {progress}/{p.Max} ({itemId})");
                return;
            }

            // 完成夜: 产出同型模组复制品 (v0.5.7: UncheckedAccept 尝试法替代预检 —
            // 放置失败则销毁产出保进度保材料, 不依赖任何槽位过滤器状态)
            GameItem copy;
            try { copy = DirectoryMaster.Item(template.identifier, true); }
            catch (Exception e) { PsApi.Warn(_log, $"printer spawn failed ({itemId}): {e.Message}"); return; }
            if (copy == null)
            {
                PsApi.Warn(_log, $"printer spawn null ({itemId}): {template.identifier} not in directory");
                return;
            }

            if (!outSlot.UncheckedAccept(copy))
            {
                var discard = new Il2CppSystem.Collections.Generic.List<GameItem>();
                discard.Add(copy);
                GeneralHelper.DestroyGameItems(discard);
                PsApi.Log(_log, $"printer skip ({itemId}): output slot full, progress kept; materials preserved");
                return;
            }

            ApplyRandomEffects(copy, template.identifier, p.RandomEffects ?? 0);
            Consume(blankSlot, blank);
            SetProgress(machine, 0);
            PsApi.Log(_log, $"printer crafted: {template.identifier} -> slot {p.Out} ({itemId})");
        }

        /// <summary>优先级: batteryPowered=false 免电 → drawnPower 指定值 → 原生 TryDrawCyclePower 规则 (与 RecipeService 同序)。</summary>
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

        private static GameItem FirstChild(GameInventory inv)
        {
            try
            {
                var kids = inv.childItems;
                if (kids == null || kids.Count == 0) return null;
                return kids[0];
            }
            catch { return null; }
        }

        /// <summary>消耗空白材料: 原生 GeneralHelper.DestroyGameItems 销毁实例。</summary>
        private static void Consume(GameInventory slot, GameItem item)
        {
            try
            {
                if (!slot.Expel(item)) PsApi.Warn(_log, "printer blank expel failed");
                var list = new Il2CppSystem.Collections.Generic.List<GameItem>();
                list.Add(item);
                GeneralHelper.DestroyGameItems(list);
            }
            catch (Exception e) { PsApi.Warn(_log, $"printer blank consume failed: {e.Message}"); }
        }

        /// <summary>打印成品附加 N 层随机已注册品质 (无注册品质则跳过)。</summary>
        private static void ApplyRandomEffects(GameItem copy, string itemId, int count)
        {
            if (count <= 0 || copy == null) return;
            var pool = QualityService.AllQualityIds;
            if (pool.Count == 0) return;
            for (int i = 0; i < count; i++)
            {
                string qid = pool[_rng.Next(pool.Count)];
                QualityService.SetQuality(copy, itemId, qid);
                PsApi.Log(_log, $"printer random effect: {qid} -> {itemId}");
            }
        }

        // ==================== 进度存取 ====================

        /// <summary>v0.5.20: 托管进度表 (key=uniqueId)。原版在任意槽位内容变化时重建 modifiedState 会连坐清标签 (§-1.12),
        /// 托管表为真相源, 标签只作存档载体 best-effort 回写。与 ProgressRecipeService 同机制。</summary>
        private static readonly Dictionary<int, int> _progress = new Dictionary<int, int>();

        /// <summary>v0.9.4: 新读档时清托管进度表 —— uniqueId 档内自增, 跨档必撞,
        /// 不清则 B 档打印机继承 A 档进度(与 ProgressRecipeService 同病同修)。</summary>
        internal static void ClearRuntimeState() => _progress.Clear();

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

        /// <summary>进度写托管表 (真相源) + 标签 (存档载体); 写完读回校验。</summary>
        private static void SetProgress(GameItem machine, int value)
        {
            try
            {
                int uid = GetUid(machine);
                if (uid != 0) _progress[uid] = value;
                WriteTag(machine, value);
                int verify = GetProgress(machine);
                if (verify != value) PsApi.Warn(_log, $"printer progress write verify mismatch: wrote {value} read {verify}");
            }
            catch (Exception e) { PsApi.Warn(_log, $"printer progress write failed: {e.Message}"); }
        }

        /// <summary>tooltip 进度行 (ModHook.OnCreateTooltipLate 调用; 非打印机返回 false)。</summary>
        internal static bool TryGetProgressLine(GameItem item, out string line)
        {
            line = null;
            if (item == null) return false;
            string id;
            try { id = item.identifier; } catch { return false; }
            if (!IsPrinter(id)) return false;
            if (!RecipeService.TryGetMachineSpecPublic(id, out var spec)) return false;
            int progress = GetProgress(item);
            line = $"打印进度: {progress}/{spec.Printer.Max}";
            return true;
        }
    }
}
