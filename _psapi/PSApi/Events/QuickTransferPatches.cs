using System;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace PSApi.Events
{
    /// <summary>
    /// raid 内 ctrl+左键快捷转移重定向 (v1.25.1 实装 / v1.25.2 二修 / v1.33.0 波 3 简化)。
    /// 原版路径调研 (UserData/cpp2il_isil/IsilDump/Assembly-CSharp/ItemQuickTransferHandler.txt,
    /// OnEventRelease ~500 行 ISIL): release 时鼠标下重取物品 → 与 currentItem 同物校验 →
    /// MayRemove → 目标选择链, 末段按夜间标志 TryAcceptOnce(EmporiumEntry.afterhourWindow
    /// [字段偏移 0x140], item)。场景复用夜间上下文 → ctrl+左键把物品送进被 Hide 的原版
    /// afterhour 外出栏 = 玩家看不见的实质遗失。
    /// 方案 (首选, 重定向): 三条件门控 (RaidCore.QuickTransferShouldIntercept 纯函数) —
    /// 在 raid + isQuickTransfer + <b>点击键 (__instance.key) release</b> 全真才接管,
    /// 转交 ScriptGridService.TryQuickTransferRedirect (v1.25.2: 直插路径, 不再依赖原版
    /// CanInsertToActiveContainer/InsertToActiveContainer — 对自建地面窗恒 false,
    /// v1.25.1 用户实测全部落兜底拦截的根因)。
    /// v1.33.0 (波 3): builtin_raid 退役, 门控只剩 script 一路 — ScriptGridService.RedirectNeeded
    /// (script 场景经 grid.hide_afterhour 藏了外出栏, 原版目标=隐藏窗=实质遗失, 同款接管)。
    /// v1.25.2 刷屏根因: OnEventRelease 对监听的每个键 release 都会进一次, 纯 ctrl 松开时
    /// isQuickTransfer 仍为 true (flag 复位在原版方法体内, 在 prefix 之后) — v1.25.1 没查
    /// 点击键, 按一下 ctrl 就触发一次重定向+日志。现在纯 ctrl 松开直接放行, 零日志。
    /// 非 raid / 非快捷转移 (普通点击拖拽 release) 一律放行原版。
    /// Alt/Alt1 = 同构备用键位处理器, 同一 helper 一并拦截。
    /// 补丁点字节数: OnEventRelease 数百字节完整流程 — 安全 (Prefix 整体跳过, 不改写中段)。
    /// </summary>
    internal static class QuickTransferPatches
    {
        /// <summary>true = 放行原版; false = 拦截 (场景已接管 / 场景内异常防丢)。
        /// 只在真正接管时经 ScriptGridService 写信息栏; 纯按键经过 (ctrl 松开/普通拖拽) 静默放行。</summary>
        private static bool Redirect(bool isQuickTransfer, bool clickRelease, GameItem currentItem)
        {
            // script 场景经 grid.hide_afterhour 藏了外出栏 → 接管 (原版目标=隐藏窗=实质遗失)
            Scenes.ScriptGridService sg = null;
            bool sgNeeded = false;
            try { sg = EventsPlugin.Instance?.ScriptGridSvc; } catch { }
            try { sgNeeded = sg != null && sg.RedirectNeeded; } catch { }
            if (!Raid.RaidCore.QuickTransferShouldIntercept(sgNeeded, isQuickTransfer, clickRelease))
                return true;
            try
            {
                return !sg.TryQuickTransferRedirect(currentItem);
            }
            catch (Exception e)
            {
                // 场景内异常一律拦: 原版 ctrl+click 在 raid 必进隐藏窗, 宁拦不放 (物品原地不动)
                EventsPlugin.LogWarn("[raid] 快捷转移重定向异常, 已拦截防丢: " + e.Message);
                return false;
            }
        }

        /// <summary>点击键 (mouse) 是否在 release 集合里 — 原版 OnEventRelease 转移分支的同款首查。
        /// (Il2Cpp interop 的 ISet 包装类不含 Contains 声明, 与原版 ISIL 一致走 ICollection 接口)。</summary>
        private static bool IsClickRelease(KeyCode key, Il2CppSystem.Collections.Generic.ISet<KeyCode> keysPressed)
        {
            try
            {
                var coll = keysPressed == null ? null : keysPressed.TryCast<Il2CppSystem.Collections.Generic.ICollection<KeyCode>>();
                return coll != null && coll.Contains(key);
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(ItemQuickTransferHandler), "OnEventRelease")]
        private static class PatchMain
        {
            private static bool Prefix(ItemQuickTransferHandler __instance,
                Il2CppSystem.Collections.Generic.ISet<KeyCode> keysPressed)
            {
                if (__instance is null) return true;
                return Redirect(__instance.isQuickTransfer,
                    IsClickRelease(__instance.key, keysPressed), __instance.currentItem);
            }
        }

        [HarmonyPatch(typeof(ItemQuickTransferAltHandler), "OnEventRelease")]
        private static class PatchAlt
        {
            private static bool Prefix(ItemQuickTransferAltHandler __instance,
                Il2CppSystem.Collections.Generic.ISet<KeyCode> keysPressed)
            {
                if (__instance is null) return true;
                return Redirect(__instance.isQuickTransfer,
                    IsClickRelease(__instance.key, keysPressed), __instance.currentItem);
            }
        }

        [HarmonyPatch(typeof(ItemQuickTransferAltHandler1), "OnEventRelease")]
        private static class PatchAlt1
        {
            private static bool Prefix(ItemQuickTransferAltHandler1 __instance,
                Il2CppSystem.Collections.Generic.ISet<KeyCode> keysPressed)
            {
                if (__instance is null) return true;
                return Redirect(__instance.isQuickTransfer,
                    IsClickRelease(__instance.key, keysPressed), __instance.currentItem);
            }
        }
    }
}
