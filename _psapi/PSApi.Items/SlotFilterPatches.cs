using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace PSApi.Items
{
    /// <summary>
    /// v0.5.12: Harmony patch 方式放宽槽位过滤器。
    /// 红线: 不修改 mayInventoryAddItemFunc 委托字段 (含包装) — 任何赋值都破坏原生闭包委托协议致拖拽失灵。
    ///
    /// 替代方案: patch 三个 virtual/instance 方法，绕过 mayAdd 检查:
    /// 1. GameSlotInventory.MayHaveValidInventorySlot Postfix → 白名单物品 true (StartDrag 存在性预检)
    /// 2. GameSlotInventory.TryInventorySlot Postfix → 手动创建 SlotMarker (Drag 阶段槽位查找)
    /// 3. SlotMarker.TryAcceptOnce Prefix → 调 AcceptUnchecked 绕过 mayAdd (EndDrag 实际放置)
    ///
    /// 拖拽管线 (native 侧):
    ///   StartDrag → MayHaveValidInventorySlot (预筛高亮)
    ///   Drag      → TryInventorySlot (找具体槽位 → lastSlot)
    ///   EndDrag   → lastSlot.TryAcceptOnce (执行放置)
    /// </summary>

    internal static class SlotFilterRegistry
    {
        private static readonly Dictionary<IntPtr, HashSet<string>> _slotWhitelists =
            new Dictionary<IntPtr, HashSet<string>>();
        // v0.6.3: 排他槽位 (ui=custom 自装配机器) — 非白名单物品一律拒, 连原生放行也压住。
        // 自装配槽位零 interop 委托, 原生 mayAdd 链为空, 原生 MayHave 判定不可靠, 必须排他。
        private static readonly HashSet<IntPtr> _exclusiveSlots = new HashSet<IntPtr>();
        // v0.5.22: 输入类槽位 → 所属机器 (加工中插入锁定用; 输出槽不登记)
        private static readonly Dictionary<IntPtr, GameItem> _slotMachines =
            new Dictionary<IntPtr, GameItem>();
        // v0.8.0: 脚本/机器面板手动插入锁 (ui.machine_lock; psui 机器组装中锁料/成品未取走拒新部件)
        private static readonly HashSet<IntPtr> _insertLocks = new HashSet<IntPtr>();
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) => _log = log;

        /// <summary>v0.8.0: 手动插入锁 (三个 patch 的 IsInsertLocked 统一拦截; 与进度机忙锁并列)。</summary>
        internal static void SetInsertLock(IntPtr slotPtr, bool locked)
        {
            if (slotPtr == IntPtr.Zero) return;
            if (locked) _insertLocks.Add(slotPtr);
            else _insertLocks.Remove(slotPtr);
        }

        internal static void Register(IntPtr slotPtr, HashSet<string> whitelist, bool exclusive = false)
        {
            if (slotPtr != IntPtr.Zero && whitelist != null && whitelist.Count > 0)
            {
                _slotWhitelists[slotPtr] = whitelist;
                if (exclusive) _exclusiveSlots.Add(slotPtr);
            }
        }

        internal static bool IsExclusive(IntPtr slotPtr) => _exclusiveSlots.Contains(slotPtr);

        /// <summary>BuildItem / 存档重挂两处调用。注册机器的输入/输出槽位白名单。</summary>
        internal static void RegisterMachine(GameItem machine, string itemId)
        {
            if (machine == null || string.IsNullOrEmpty(itemId)) return;
            if (!RecipeService.TryGetMachineSpecPublic(itemId, out var spec)) return;

            // v0.6.3: ui=custom 自装配机器的输入/输出槽排他注册 (原生委托链为空, 判定全归本注册表)
            bool custom = RecipeService.IsCustomUi(spec.Ui);

            if (spec.InputWhitelist != null && spec.InputWhitelist.Count > 0)
            {
                int inIdx = spec.InputSlot ?? 2;
                RegisterSlot(machine, itemId, inIdx, spec.InputWhitelist, "input", lockable: true, exclusive: custom);
            }

            if (spec.OutputWhitelist != null && spec.OutputWhitelist.Count > 0)
            {
                int outIdx = spec.OutputSlot ?? (spec.InputSlot ?? 2) + 1;
                RegisterSlot(machine, itemId, outIdx, spec.OutputWhitelist, "output", lockable: false, exclusive: custom);
            }

            if (spec.SlotWhitelists != null)
            {
                foreach (var sw in spec.SlotWhitelists)
                {
                    if (sw?.Slot == null || sw.Whitelist == null || sw.Whitelist.Count == 0) continue;
                    RegisterSlot(machine, itemId, sw.Slot.Value, sw.Whitelist, $"extra[{sw.Slot.Value}]", lockable: true);
                }
            }
        }

        private static void RegisterSlot(GameItem machine, string itemId, int slotIdx, List<string> ids, string label, bool lockable, bool exclusive = false)
        {
            try
            {
                var slot = SlotBridge.GetSlot(machine, slotIdx);
                if (slot == null) return;
                // v0.6.2: 注册时归一化 — 剥 game: 前缀 (patch 匹配用的是裸 identifier;
                // ui=custom 机器示例包写了 "game:scrap_metal" 导致 Postfix 永不命中的事故)
                var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in ids)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string s = raw.Trim();
                    if (s.StartsWith("game:", StringComparison.OrdinalIgnoreCase))
                        s = s.Substring("game:".Length);
                    wl.Add(s);
                }
                if (wl.Count == 0) return;
                Register(slot.Pointer, wl, exclusive);
                if (lockable) _slotMachines[slot.Pointer] = machine;
                PsApi.Log(_log, $"slot-filter register: {itemId} {label} slot[{slotIdx}] wl={wl.Count}{(exclusive ? " exclusive" : "")}");
            }
            catch (Exception e) { PsApi.Warn(_log, $"slot-filter register {label} failed ({itemId}, {slotIdx}): {e.Message}"); }
        }

        internal static bool TryGetWhitelist(IntPtr slotPtr, out HashSet<string> whitelist)
        {
            return _slotWhitelists.TryGetValue(slotPtr, out whitelist);
        }

        /// <summary>v0.5.22: 该槽位是否因机器加工中而禁止放入 (进度机 progress>0 时输入槽连放入也锁, 仿原版机器忙)。
        /// v0.8.0: 手动插入锁 (_insertLocks) 优先判定 — 脚本锁与进度锁并列。</summary>
        internal static bool IsInsertLocked(IntPtr slotPtr)
        {
            if (_insertLocks.Contains(slotPtr)) return true;
            try
            {
                if (!_slotMachines.TryGetValue(slotPtr, out var machine) || machine == null) return false;
                string id = machine.identifier;
                if (string.IsNullOrEmpty(id)) return false;
                if (!RecipeService.TryGetMachineSpecPublic(id, out var spec) || spec.Progress == null) return false;
                return ProgressRecipeService.GetProgress(machine) > 0;
            }
            catch { return false; }   // 场景卸载后机器引用失效 → 不锁
        }

        internal static void Clear()
        {
            _slotWhitelists.Clear();
            _exclusiveSlots.Clear();
            _slotMachines.Clear();
            _insertLocks.Clear();
        }

        /// <summary>patch 诊断日志 (v0.5.14 修复: 之前 PsApi.Log(null,..) 被静默丢弃)。</summary>
        internal static void Dbg(string msg) => PsApi.Log(_log, msg);
        internal static void DbgWarn(string msg) => PsApi.Warn(_log, msg);
    }

    /// <summary>Patch 1: MayHaveValidInventorySlot — StartDrag 存在性预检。</summary>
    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.MayHaveValidInventorySlot))]
    internal static class MayHaveValidSlotPatch
    {
        private static readonly HashSet<string> _probeIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scrap_metal", "common_ore", "newspaper", "bottled_water" };

        private static void Postfix(GameSlotInventory __instance, GameItem item, ref bool __result)
        {
            string probeId = null;
            try { if (item != null && _probeIds.Contains(item.identifier)) probeId = item.identifier; } catch { }

            IntPtr slotPtr;
            try { slotPtr = __instance.Pointer; } catch { return; }

            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                if (__result) __result = false;
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={probeId} slotPtr={slotPtr.ToInt64()} insert-locked (machine busy)");
                return;
            }

            // v0.6.3: 排他槽位先判 — 非白名单一律拒, 原生 true 也压住 (自装配槽位零委托, 原生判定不可靠)
            bool exclusive = SlotFilterRegistry.IsExclusive(slotPtr);
            if (exclusive)
            {
                string exId = null;
                try { if (item != null) exId = item.identifier; } catch { }
                bool wlHit = !string.IsNullOrEmpty(exId)
                    && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var exWl)
                    && exWl.Contains(exId);
                if (!wlHit)
                {
                    if (__result) __result = false;
                    if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={exId} slotPtr={slotPtr.ToInt64()} exclusive-reject");
                    return;
                }
            }

            if (__result)
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={probeId} slotPtr={slotPtr.ToInt64()} native-already-true");
                return;
            }
            if (item == null) return;

            if (!SlotFilterRegistry.TryGetWhitelist(slotPtr, out var whitelist))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={probeId} slotPtr={slotPtr.ToInt64()} NOT-registered");
                return;
            }

            string itemId;
            try { itemId = item.identifier; } catch { return; }
            if (string.IsNullOrEmpty(itemId) || !whitelist.Contains(itemId))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot item={itemId} slotPtr={slotPtr.ToInt64()} registered-but-not-whitelisted");
                return;
            }

            try
            {
                var existing = __instance.childItem;
                if (existing == null) { __result = true; SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot true item={itemId} slotPtr={slotPtr.ToInt64()}"); return; }
                string existingId;
                try { existingId = existing.identifier; } catch { return; }
                if (string.Equals(existingId, itemId, StringComparison.OrdinalIgnoreCase))
                {
                    __result = true;
                    SlotFilterRegistry.Dbg($"slot-filter dbg: MayHaveValidSlot true (same-id) item={itemId} slotPtr={slotPtr.ToInt64()}");
                }
            }
            catch { }
        }
    }

    /// <summary>Patch 2: TryInventorySlot — Drag 阶段槽位查找，手动创建 SlotMarker。</summary>
    [HarmonyPatch(typeof(GameSlotInventory), nameof(GameSlotInventory.TryInventorySlot),
        new[] { typeof(GameItem), typeof(int), typeof(Vector2), typeof(GridShape), typeof(TagSystem) })]
    internal static class TryInventorySlotPatch
    {
        private static readonly HashSet<string> _probeIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scrap_metal", "common_ore", "newspaper", "bottled_water" };

        private static void Postfix(GameSlotInventory __instance, GameItem item, int requestedNum,
            Vector2 itemGraphicsCenterPosition, GridShape tempShape, TagSystem tempState,
            ref SlotMarker __result)
        {
            string probeId = null;
            try { if (item != null && _probeIds.Contains(item.identifier)) probeId = item.identifier; } catch { }

            IntPtr slotPtr;
            try { slotPtr = __instance.Pointer; } catch { return; }

            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                if (__result != null) __result = null;
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={probeId} slotPtr={slotPtr.ToInt64()} insert-locked (machine busy)");
                return;
            }

            // v0.6.3: 排他槽位先判 — 非白名单物品即使有原生 marker 也作废
            bool exclusive = SlotFilterRegistry.IsExclusive(slotPtr);
            if (exclusive)
            {
                string exId = null;
                try { if (item != null) exId = item.identifier; } catch { }
                bool wlHit = !string.IsNullOrEmpty(exId)
                    && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var exWl)
                    && exWl.Contains(exId);
                if (!wlHit)
                {
                    if (__result != null) __result = null;
                    if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={exId} slotPtr={slotPtr.ToInt64()} exclusive-reject");
                    return;
                }
            }

            if (__result != null)
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={probeId} slotPtr={slotPtr.ToInt64()} native-marker (no override needed)");
                return;
            }
            if (item == null) return;

            if (!SlotFilterRegistry.TryGetWhitelist(slotPtr, out var whitelist))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={probeId} slotPtr={slotPtr.ToInt64()} NOT-registered");
                return;
            }

            string itemId;
            try { itemId = item.identifier; } catch { return; }
            if (string.IsNullOrEmpty(itemId) || !whitelist.Contains(itemId))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot item={itemId} slotPtr={slotPtr.ToInt64()} registered-but-not-whitelisted");
                return;
            }

            try
            {
                var existing = __instance.childItem;
                GameItem target = null;
                if (existing != null)
                {
                    string existingId;
                    try { existingId = existing.identifier; } catch { return; }
                    if (!string.Equals(existingId, itemId, StringComparison.OrdinalIgnoreCase))
                        return;
                    target = existing;
                }

                var marker = new SlotMarker();
                marker.inventory = __instance;
                marker.item = item;
                marker.index = 0;
                marker.itemGridShape = tempShape;
                marker.targetItem = target;
                marker.numTransfer = requestedNum;
                marker.priority = 0;
                __result = marker;
                // v0.5.14 诊断: 白名单命中即记录 (含 marker 有效性)
                bool valid = false;
                try { valid = marker.IsValid(); } catch { }
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryInventorySlot marker created item={itemId} slotPtr={slotPtr.ToInt64()} valid={valid} tempShape={(tempShape == null ? "null" : "set")} target={(target == null ? "-" : "same")}");
            }
            catch (Exception e) { SlotFilterRegistry.DbgWarn($"slot-filter TryInventorySlot postfix: {e.Message}"); }
        }
    }

    /// <summary>v0.6.4: 网格槽排他版 Patch 1 — GameGridInventory.MayHaveValidInventorySlot。
    /// 与 GameSlotInventory 版不同: 白名单命中不需要强制 true/造 marker, 原生网格实现自己找空位,
    /// 这里只负责"排他槽位拒非白名单"(零委托网格原生会乱放任何东西进来)。</summary>
    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.MayHaveValidInventorySlot))]
    internal static class GridMayHaveValidSlotPatch
    {
        private static void Postfix(GameGridInventory __instance, GameItem item, ref bool __result)
        {
            IntPtr slotPtr;
            try { slotPtr = __instance.Pointer; } catch { return; }

            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                if (__result) __result = false;
                return;
            }
            if (!SlotFilterRegistry.IsExclusive(slotPtr)) return;   // 非排他网格槽: 原生判定

            string itemId = null;
            try { if (item != null) itemId = item.identifier; } catch { }
            bool wlHit = !string.IsNullOrEmpty(itemId)
                && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var wl)
                && wl.Contains(itemId);
            if (!wlHit)
            {
                if (__result) __result = false;
                SlotFilterRegistry.Dbg($"slot-filter dbg: grid MayHaveValidSlot item={(itemId ?? "?")} slotPtr={slotPtr.ToInt64()} exclusive-reject");
            }
        }
    }

    /// <summary>v0.6.4: 网格槽排他版 Patch 2 — GameGridInventory.TryInventorySlot。排他槽位非白名单物品作废原生 marker。</summary>
    [HarmonyPatch(typeof(GameGridInventory), nameof(GameGridInventory.TryInventorySlot),
        new[] { typeof(GameItem), typeof(int), typeof(Vector2), typeof(GridShape), typeof(TagSystem) })]
    internal static class GridTryInventorySlotPatch
    {
        private static void Postfix(GameGridInventory __instance, GameItem item, ref SlotMarker __result)
        {
            IntPtr slotPtr;
            try { slotPtr = __instance.Pointer; } catch { return; }

            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                if (__result != null) __result = null;
                return;
            }
            if (!SlotFilterRegistry.IsExclusive(slotPtr)) return;
            if (__result == null) return;   // 原生没找到位置, 无需干预 (白名单物品的放置由原生网格逻辑完成)

            string itemId = null;
            try { if (item != null) itemId = item.identifier; } catch { }
            bool wlHit = !string.IsNullOrEmpty(itemId)
                && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var wl)
                && wl.Contains(itemId);
            if (!wlHit)
            {
                __result = null;
                SlotFilterRegistry.Dbg($"slot-filter dbg: grid TryInventorySlot item={(itemId ?? "?")} slotPtr={slotPtr.ToInt64()} exclusive-reject");
            }
        }
    }

    /// <summary>Patch 3: TryAcceptOnce — EndDrag 实际放置，绕过 mayAdd 调 AcceptUnchecked。</summary>
    [HarmonyPatch(typeof(SlotMarker), nameof(SlotMarker.TryAcceptOnce))]
    internal static class TryAcceptOncePatch
    {
        private static readonly HashSet<string> _probeIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scrap_metal", "common_ore", "newspaper", "bottled_water" };

        private static bool Prefix(SlotMarker __instance, int amount, ref int __result)
        {
            GameItem item0 = null;
            string probeId = null;
            try { item0 = __instance.item; } catch { }
            try { if (item0 != null && _probeIds.Contains(item0.identifier)) probeId = item0.identifier; } catch { }

            GameInventory inventory;
            try { inventory = __instance.inventory; } catch { return true; }
            if (inventory == null) return true;

            IntPtr slotPtr;
            try { slotPtr = inventory.Pointer; } catch { return true; }

            // v0.5.22: 加工中输入槽连放入也锁 — 无论白名单/原生放行都拒收 (原生路径也会被此前缀拦截)
            if (SlotFilterRegistry.IsInsertLocked(slotPtr))
            {
                __result = 0;
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={(probeId ?? "?")} slotPtr={slotPtr.ToInt64()} insert-locked (machine busy) -> reject");
                return false;
            }

            // v0.6.3: 排他槽位先判 — 非白名单物品直接拒, 不放给原生 (自装配槽位零委托, 原生会乱放)
            if (SlotFilterRegistry.IsExclusive(slotPtr))
            {
                string exId = null;
                try { if (item0 != null) exId = item0.identifier; } catch { }
                bool wlHit = !string.IsNullOrEmpty(exId)
                    && SlotFilterRegistry.TryGetWhitelist(slotPtr, out var exWl)
                    && exWl.Contains(exId);
                if (!wlHit)
                {
                    __result = 0;
                    SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={(exId ?? "?")} slotPtr={slotPtr.ToInt64()} exclusive-reject");
                    return false;
                }
            }

            if (!SlotFilterRegistry.TryGetWhitelist(slotPtr, out var whitelist))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={probeId} slotPtr={slotPtr.ToInt64()} NOT-registered -> native");
                return true;
            }

            if (item0 == null) return true;

            string itemId;
            try { itemId = item0.identifier; } catch { return true; }
            if (string.IsNullOrEmpty(itemId) || !whitelist.Contains(itemId))
            {
                if (probeId != null) SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce item={itemId} slotPtr={slotPtr.ToInt64()} registered-but-not-whitelisted -> native");
                return true;
            }

            try
            {
                // 防 double-prefix 二次执行: 物品已在本槽则直接报成功
                var slotInv = inventory.TryCast<GameSlotInventory>();
                if (slotInv != null)
                {
                    GameItem cur = null;
                    try { cur = slotInv.childItem; } catch { }
                    if (cur != null && cur.Pointer == item0.Pointer)
                    {
                        __result = 1;
                        SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce prefix item={itemId} slotPtr={slotPtr.ToInt64()} already-in-slot -> success");
                        return false;
                    }
                }

                int r = -1;
                try { r = __instance.AcceptUnchecked(); }
                catch (Exception e) { SlotFilterRegistry.DbgWarn($"slot-filter dbg: AcceptUnchecked err item={itemId}: {e.Message}"); }
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce prefix item={itemId} slotPtr={slotPtr.ToInt64()} acceptResult={r}");
                if (r > 0) { __result = r; return false; }

                // 回退: §9i 实证路径 — 槽级 UncheckedAccept 原生放行多格物品, SlotMarker.AcceptUnchecked 有额外检查
                var parent = FindParentInventory(item0);
                if (parent != null)
                {
                    try { if (parent.Pointer != inventory.Pointer) parent.Expel(item0); } catch { }
                }
                bool ok = false;
                try { ok = inventory.UncheckedAccept(item0); }
                catch (Exception e) { SlotFilterRegistry.DbgWarn($"slot-filter dbg: UncheckedAccept err item={itemId}: {e.Message}"); }
                SlotFilterRegistry.Dbg($"slot-filter dbg: TryAcceptOnce fallback item={itemId} slotPtr={slotPtr.ToInt64()} UncheckedAccept={ok}");
                if (ok) { __result = 1; return false; }
                return true; // 全失败 → 走原生 (回弹)
            }
            catch (Exception e)
            {
                SlotFilterRegistry.DbgWarn($"slot-filter dbg: TryAcceptOnce prefix err item={itemId}: {e.Message}");
                return true;
            }
        }

        private static GameInventory FindParentInventory(GameItem item)
        {
            try
            {
                var parents = item.parents;
                if (parents == null) return null;
                foreach (var p in parents)
                {
                    if (p == null) continue;
                    var inv = p.TryCast<GameInventory>();
                    if (inv != null) return inv;
                }
            }
            catch { }
            return null;
        }
    }
}