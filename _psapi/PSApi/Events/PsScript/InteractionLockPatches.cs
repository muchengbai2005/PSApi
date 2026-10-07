using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// v1.48.2 锁交互槽补充补丁 — g_big 放开容器/机器过滤的前提: 把双击以外的原生开启路径一并堵死。
    /// v1.48.3 实证修复: v1.48.2 用 Traverse.Field("currentItem"/"buttonOpen"/"canOpen") 读成员 —
    /// Il2Cpp interop 程序集里这些是【属性】不是字段 (运行时对象没有同名字段),
    /// Traverse.Field 静默失败 → CurrentItem 恒 null → 三条补丁全部空转 (用户实测右键「打开」照样开窗)。
    /// 改为直接属性访问 (编译期对真 Assembly-CSharp.dll 验证, 编译过=成员存在);
    /// 按钮比较用 .Pointer (Il2Cpp 包装对象 ReferenceEquals 不可靠)。
    /// 另加路径无关兜底闸门: 右键「打开」/默认激活热键最终都汇到
    /// ItemMouseDoubleClickHandler.OpenContentAction / OpenExamineAction (反编译方法表实证),
    /// 在这两个出口各加 Prefix — 即使上游某条 UI 路径没拦到 (内联/新路径), 开箱动作本身也过不去。
    ///
    /// 原生物品交互路径 (cpp2il 反编译实证):
    ///   1. ItemMouseDoubleClickHandler.DoubleClickAction — 双击, v1.48.0 DoubleClickPatch 拦截
    ///      (参数直传 GameItem, 不经字段反射, 不受 v1.48.2 缺陷影响)。
    ///   2. ItemContextHandler — 右键上下文菜单: UpdateConditions Postfix 灰掉 canOpen/canUse/
    ///      canActivate/canToggle (UX) + TriggerButton Prefix 硬拦 Open/Use/Activate/Toggle/Unload;
    ///      buttonEquip 放行 (装备=移动), 排序按钮放行 (整理=移动)。
    ///   3. ItemDefaultActivateHandler — 悬停热键「默认激活」(容器=开箱): OnEventPress Prefix 硬拦。
    ///   4. OpenContentAction / OpenExamineAction — 开箱/查看动作的公共出口, v1.48.3 兜底硬拦。
    ///   5. ItemQuickTransferHandler (ctrl 快捷转移) — 只搬动=移动, 用户拍板放行, 不拦。
    ///   6. v1.48.4 拖放存放 (第 4 条开启路径, 用户实锤): 拖 A 到锁槽容器/机器物品 B 上提示
    ///      「将 A 存放入 B」且松手真存入 — RichTextGenerator.InsertItemInOtherItem 生成提示文本,
    ///      GeneralHelper.MayPlayerInsertInto(GameItem) 是谓词闸门 (ItemMouseDragHandler 拖悬高亮
    ///      highlightedMayNodes 与松手执行都经它), Postfix 压 false = 提示不显示+存放不执行;
    ///      GraphUtils.CanInsertToActiveContainer/InsertToActiveContainer 为窗体侧执行兜底
    ///      (目标窗 parentItems 含锁槽物品 = 窗主在锁槽 → 拒; 覆盖"先开窗后入锁槽"边角)。
    /// 判定统一走 InteractionLockRegistry.IsItemLocked (位于该槽), 异常保守放行 (不炸原生菜单)。
    /// </summary>
    internal static class InteractionLockNativeGuard
    {
        internal static bool Locked(GameItem item)
        {
            try { return InteractionLockRegistry.IsItemLocked(item); }
            catch { return false; }
        }

        internal static GameItem CurrentItem(ItemContextHandler h)
        {
            try { return h == null ? null : h.currentItem; }   // interop 公开属性, 直读原生字段
            catch { return null; }
        }

        internal static GameItem CurrentItem(ItemDefaultActivateHandler h)
        {
            try { return h == null ? null : h.currentItem; }
            catch { return null; }
        }

        /// <summary>Il2Cpp 包装对象同一性按原生指针比 (包装实例不保证唯一)。</summary>
        internal static bool Same(RichTextElement a, RichTextElement b)
        {
            try { return a != null && b != null && a.Pointer == b.Pointer; }
            catch { return false; }
        }
    }

    /// <summary>右键菜单条件位: 锁槽物品灰掉 Open/Use/Activate/Toggle (UX 层; 硬拦在 TriggerButton/OpenContentAction)。</summary>
    [HarmonyPatch(typeof(ItemContextHandler), "UpdateConditions")]
    internal static class InteractionLockContextConditionsPatch
    {
        private static void Postfix(ItemContextHandler __instance)
        {
            try
            {
                if (!InteractionLockNativeGuard.Locked(InteractionLockNativeGuard.CurrentItem(__instance))) return;
                __instance.canOpen = false;
                __instance.canUse = false;
                __instance.canActivate = false;
                __instance.canToggle = false;
            }
            catch { }
        }
    }

    /// <summary>右键菜单执行闸门: 锁槽物品的 Open/Use/Activate/Toggle/Unload 按钮一律不触发;
    /// Equip (装备=移动) 与排序按钮放行。</summary>
    [HarmonyPatch(typeof(ItemContextHandler), "TriggerButton")]
    internal static class InteractionLockContextTriggerPatch
    {
        private static bool Prefix(ItemContextHandler __instance, RichTextElement button)
        {
            try
            {
                if (button == null) return true;
                if (!InteractionLockNativeGuard.Locked(InteractionLockNativeGuard.CurrentItem(__instance))) return true;
                string hit = null;
                if (InteractionLockNativeGuard.Same(__instance.buttonOpen, button)) hit = "Open";
                else if (InteractionLockNativeGuard.Same(__instance.buttonUse, button)) hit = "Use";
                else if (InteractionLockNativeGuard.Same(__instance.buttonActivate, button)) hit = "Activate";
                else if (InteractionLockNativeGuard.Same(__instance.buttonToggle, button)) hit = "Toggle";
                else if (InteractionLockNativeGuard.Same(__instance.buttonUnload, button)) hit = "Unload";
                if (hit != null)
                {
                    MelonLoader.MelonLogger.Warning($"[psapi] 锁交互槽: 右键菜单 {hit} 已拦截");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>悬停热键「默认激活」(容器=开箱): 锁槽物品硬拦。</summary>
    [HarmonyPatch(typeof(ItemDefaultActivateHandler), "OnEventPress")]
    internal static class InteractionLockDefaultActivatePatch
    {
        private static bool Prefix(ItemDefaultActivateHandler __instance)
        {
            try
            {
                if (InteractionLockNativeGuard.Locked(InteractionLockNativeGuard.CurrentItem(__instance)))
                {
                    MelonLoader.MelonLogger.Warning("[psapi] 锁交互槽: 默认激活热键已拦截");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>v1.48.3 路径无关兜底: 开箱动作公共出口 (双击/右键「打开」/默认激活最终都汇到这里)。
    /// 锁槽物品的容器/机器窗一律不开 — 上游任何 UI 路径漏拦 (内联/未来新路径) 都过不去。</summary>
    [HarmonyPatch(typeof(ItemMouseDoubleClickHandler), "OpenContentAction")]
    internal static class InteractionLockOpenContentPatch
    {
        private static bool Prefix(GameItem newItem)
        {
            try
            {
                if (InteractionLockNativeGuard.Locked(newItem))
                {
                    MelonLoader.MelonLogger.Warning("[psapi] 锁交互槽: 开箱动作 (OpenContentAction) 已拦截");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>v1.48.3 路径无关兜底: 查看 (examine) 窗出口 — 与双击全吞语义一致, 锁槽物品不弹查看窗。</summary>
    [HarmonyPatch(typeof(ItemMouseDoubleClickHandler), "OpenExamineAction")]
    internal static class InteractionLockOpenExaminePatch
    {
        private static bool Prefix(GameItem newItem)
        {
            try
            {
                if (InteractionLockNativeGuard.Locked(newItem))
                {
                    MelonLoader.MelonLogger.Warning("[psapi] 锁交互槽: 查看动作 (OpenExamineAction) 已拦截");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>v1.48.4 拖放存放谓词闸门 (第 4 条开启路径): 拖 A 悬到容器/机器物品 B 上时原生用它算
    /// 「将 A 存放入 B」提示 (RichTextGenerator.InsertItemInOtherItem 的 canInsert) 与松手执行资格 —
    /// B 在锁槽 → 压 false: 提示不显示, 松手存不进。热路径 (拖拽逐帧), 不打日志; 执行兜底才打。
    /// 物品由参数直传 (静态谓词), 无字段反射。重载按参数类型指定, 只拦 GameItem 版
    /// (GameInventory 版面向已开窗的容器格 — 锁槽容器开不了窗, 无靶)。</summary>
    [HarmonyPatch(typeof(GeneralHelper), "MayPlayerInsertInto", new Type[] { typeof(GameItem) })]
    internal static class InteractionLockInsertIntoItemPatch
    {
        private static void Postfix(GameItem containerItem, ref bool __result)
        {
            try
            {
                if (__result && InteractionLockNativeGuard.Locked(containerItem)) __result = false;
            }
            catch { }
        }
    }

    /// <summary>v1.48.4 拖放存放窗体侧兜底: 目标内容窗的 parentItems 含锁槽物品 (窗主容器/机器在锁槽,
    /// 典型边角 = 先打开背包窗再把背包拖进锁槽) → 不可存。直接属性访问 (interop 公开属性)。</summary>
    internal static class InteractionLockInsertWindowGuard
    {
        internal static bool WindowOwnerLocked(PixelWindow w)
        {
            try
            {
                if (w == null) return false;
                var items = w.parentItems;
                if (items == null) return false;
                foreach (var it in items)
                    if (InteractionLockNativeGuard.Locked(it)) return true;
            }
            catch { }
            return false;
        }
    }

    /// <summary>v1.48.4 执行层闸门: 可存判定压 false (提示/高亮侧)。</summary>
    [HarmonyPatch(typeof(GraphUtils), "CanInsertToActiveContainer")]
    internal static class InteractionLockCanInsertContainerPatch
    {
        private static void Postfix(PixelWindow parentWindow, ref bool __result)
        {
            try
            {
                if (__result && InteractionLockInsertWindowGuard.WindowOwnerLocked(parentWindow)) __result = false;
            }
            catch { }
        }
    }

    /// <summary>v1.48.4 执行层兜底: 存放动作硬拦 (一次动作, 打日志供实测核对)。</summary>
    [HarmonyPatch(typeof(GraphUtils), "InsertToActiveContainer")]
    internal static class InteractionLockInsertContainerPatch
    {
        private static bool Prefix(PixelWindow targetWindow)
        {
            try
            {
                if (InteractionLockInsertWindowGuard.WindowOwnerLocked(targetWindow))
                {
                    MelonLoader.MelonLogger.Warning("[psapi] 锁交互槽: 拖放存放 (InsertToActiveContainer) 已拦截");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>v1.48.5 快捷选择/装备出口兜底 (QuickChoose 快捷轮盘老账):
    /// 反编译实证 (IsilDump): QuickChooseHandler 本体只是 Esc/确认键分发器
    /// (cancelables/confirmables 委托列表, Confirmable 全游戏无人注册=死路径), 不直接动物品;
    /// 物品选择/装备的公共出口是 ItemSelectHandler.TrySelectItem / TryEquipItem —
    ///   · TrySelectItem 调用方: 双击 Use (ItemMouseDoubleClickHandler) / 右键 Use (ItemContextHandler)
    ///     — 上游 v1.48.0/v1.48.2 已拦, 此处双保险;
    ///   · TryEquipItem 调用方: 右键 Equip + ToolboxHelper.HandleHotkeys ×2 (工具箱热键快捷装备=
    ///     「快捷轮盘」语义, 此前未拦的唯一路径)。
    /// 两入口物品均为参数直传 (ISIL: MaySelectSlotItem/CanSelectSlotItem 检查 → SelectItem),
    /// 无字段反射; 锁槽物品在此处 return false = 选中/装备(手持可使用)双路全堵,
    /// 覆盖热键与一切未来路径。背包内正常物品 IsItemLocked=false 不受影响;
    /// 右键菜单 UX 层 Equip 按钮放行拍板 (v1.48.2) 不变 — 按钮还在, 锁槽物品点了无效果。</summary>
    [HarmonyPatch(typeof(ItemSelectHandler), "TrySelectItem")]
    internal static class InteractionLockTrySelectPatch
    {
        private static bool Prefix(GameItem newItem)
        {
            try
            {
                if (InteractionLockNativeGuard.Locked(newItem))
                {
                    MelonLoader.MelonLogger.Warning("[psapi] 锁交互槽: 快捷选择 (TrySelectItem) 已拦截");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }

    /// <summary>v1.48.5 快捷装备出口兜底 — 工具箱热键 (快捷轮盘) 唯一未拦路径的修复, 详见 TrySelectItem 注释。</summary>
    [HarmonyPatch(typeof(ItemSelectHandler), "TryEquipItem")]
    internal static class InteractionLockTryEquipPatch
    {
        private static bool Prefix(GameItem newItem)
        {
            try
            {
                if (InteractionLockNativeGuard.Locked(newItem))
                {
                    MelonLoader.MelonLogger.Warning("[psapi] 锁交互槽: 快捷装备 (TryEquipItem) 已拦截");
                    return false;
                }
            }
            catch { }
            return true;
        }
    }
}
