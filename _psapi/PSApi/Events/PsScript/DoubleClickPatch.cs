using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// v1.47.0 物品双击接管 (items.on_double_click 落点) — 仿 crime.set_filter (CrimeExemptPatch)。
    ///
    /// ItemMouseDoubleClickHandler.DoubleClickAction(GameItem, Vector2) 是原生双击唯一行为入口
    /// (容器开箱/机器开面板/背包 examine 都经它): Prefix 按物品 identifier 查注册表, 命中 →
    /// 调 fn(物品句柄) 并 return false 跳过原生; 未命中 → return true 放行原生。
    /// 脚本异常 = warn + 放行原生 (不炸游戏); 防重入闸门 (fn 里再触发双击直接放行)。
    ///
    /// 用途: 普通物品 (非容器非机器) 双击开包自定义 psui 独立窗 (gunworks 探索者日志)。
    /// 机器/容器物品不要走这里 — 原生双击已有语义, 且 contentWindow 物品会被 SlotFilterPatches 拦。
    /// </summary>
    internal static class DoubleClickRegistry
    {
        internal sealed class Entry
        {
            internal PsCallable Fn;
            internal Interpreter Itp;
        }

        /// <summary>identifier (裸 id 含包前缀) → 回调。items.on_double_click 注册; 不持久化, 包脚本顶层重注册。</summary>
        internal static readonly Dictionary<string, Entry> Handlers = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>防重入: 回调执行中再触发 DoubleClickAction (脚本里模拟双击?) 直接放行原生。</summary>
        internal static bool Running;
    }

    [HarmonyPatch(typeof(ItemMouseDoubleClickHandler), "DoubleClickAction")]
    internal static class DoubleClickPatch
    {
        private static bool Prefix(GameItem newItem, Vector2 mousePosition)
        {
            try
            {
                if (newItem == null || DoubleClickRegistry.Running) return true;
                // v1.48.0: 锁交互槽 (旅行物品箱大物件槽) — 槽内物品双击全吞:
                // 脚本 on_double_click 与原生容器/机器开窗都拦 (防套容器藏东西; 移动不受影响)
                try { if (InteractionLockRegistry.IsItemLocked(newItem)) return false; } catch { }
                if (DoubleClickRegistry.Handlers.Count == 0) return true;
                string id = null;
                try { id = newItem.identifier; } catch { }
                if (id == null || !DoubleClickRegistry.Handlers.TryGetValue(id, out var e) || e.Fn == null || e.Itp == null)
                    return true;
                try
                {
                    DoubleClickRegistry.Running = true;
                    e.Itp.BeginRun();
                    e.Itp.CallCallable(e.Fn, new List<object> { new PsItemHandle(newItem) }, 0);
                }
                catch (Exception ex)
                {
                    MelonLoader.MelonLogger.Warning($"[items] on_double_click 回调出错 ({id}, 放行原生): {ex.Message}");
                    return true;
                }
                finally { DoubleClickRegistry.Running = false; }
                return false;   // 命中 = 跳过原生双击行为
            }
            catch { }
            return true;
        }
    }
}
