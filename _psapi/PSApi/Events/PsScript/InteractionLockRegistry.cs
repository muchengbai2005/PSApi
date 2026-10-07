using System;
using System.Collections.Generic;
using Il2Cpp;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// v1.48.0 槽内物品交互锁 (psui slot/grid_slot lock_interactions: true) — gunworks v0.45.0
    /// 旅行物品箱大物件槽: 槽内物品锁死除「移动」外一切交互 (防套容器藏东西/双击开 UI/被当 use 目标)。
    ///
    /// 判定 = 「位于该槽」状态 (item.parents 链含注册槽指针), 不标记物品实例 → 不持久化,
    /// 面板重建时按 psui 声明重新注册槽指针, 读档/场景切换后天然恢复 (物品随原生存档留在槽里,
    /// 新面板新指针在 Build 时重挂)。与 SlotFilterRegistry 同寿命 (进程级, 指针注册表)。
    ///
    /// 消费点:
    ///   DoubleClickPatch        — 槽内物品双击全吞 (含原生容器/机器开窗, explorer_journal 类脚本双击同拦)
    ///   ItemTargetMay/CanPatch  — 成为 on_target 目标端 → 拒 (高亮不亮)
    ///   ItemTargetDoPatch       — 任一端在锁槽 → Target 跳过 (use 双方全堵)
    /// 移动 (拖入/拖出/快捷转移) 不经过以上路径, 天然放行。
    /// </summary>
    internal static class InteractionLockRegistry
    {
        private static readonly HashSet<IntPtr> _lockedSlots = new HashSet<IntPtr>();

        /// <summary>psui 构建期注册 (lock_interactions: true 的槽)。重复注册幂等。</summary>
        internal static void Register(IntPtr slotPtr)
        {
            if (slotPtr != IntPtr.Zero) _lockedSlots.Add(slotPtr);
        }

        internal static bool IsLockedSlot(IntPtr slotPtr)
            => slotPtr != IntPtr.Zero && _lockedSlots.Contains(slotPtr);

        /// <summary>物品当前是否位于锁交互槽 (直接父系含注册槽指针)。读取失败保守 false (不锁)。</summary>
        internal static bool IsItemLocked(GameItem item)
        {
            if (item == null || _lockedSlots.Count == 0) return false;
            try
            {
                var parents = item.parents;
                if (parents == null) return false;
                foreach (var p in parents)
                {
                    if (p == null) continue;
                    var inv = p.TryCast<GameInventory>();
                    if (inv != null && IsLockedSlot(inv.Pointer)) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>测试台用: 注册槽数。</summary>
        internal static int Count => _lockedSlots.Count;

        /// <summary>测试台用: 清空 (静态表跨段共享, 各段自清理防串扰)。</summary>
        internal static void Clear() => _lockedSlots.Clear();
    }
}
