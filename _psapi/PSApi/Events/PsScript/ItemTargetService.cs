using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using PSApi.Items;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// v1.39.0 物品对物品 use 注册服务 (items.on_target) — 通用「拖拽物品 A 到物品 B 上触发脚本」。
    ///
    /// 背景: 原版 module_bay_expansion_kit 拖到机器上走 GameItem.MayTarget/CanTarget/Target,
    /// 原生判定内部走 MachineHelper.CanExpand, 对自定义机器必然 false — 必须 Harmony 接管。
    /// 本服务与具体物品无关, 任何包可注册任意 (source_id, target_id) 对。
    ///
    /// 补丁三件套 (照 CrimeExemptPatch/SlotFilterPatches 写法, Plugin PatchAll 自动应用):
    ///   MayTarget postfix / CanTarget postfix: 源/目标 id 对命中注册表 → __result = true。
    ///     **设计决定: May/Can 只按 id 对命中放行, 不调 pss** — 拖拽高亮每帧调用, 避免每帧脚本开销。
    ///   Target prefix: 命中 → PsItemHandle 包装 source/target 调 fn(h_source, h_target);
    ///     fn 返回 false → 放行原生 (return true); 其他 → 跳过原生 (return false)。
    ///     异常隔离: try/catch 记日志 + 放行原生 — 一次脚本异常不炸全游戏拖拽; 防重入闸门。
    ///
    /// id 规范化: 注册与查找都过 ItemsFacade.NormalizeId ("game:" 前缀剥除) —
    /// 包侧写 "game:module_bay_expansion_kit", item.identifier 是裸 id, 两侧同尺度比对。
    /// 重复注册同键 = 覆盖 + 警告 (可调对象不持久化, 包脚本顶层每次加载重新注册)。
    /// </summary>
    internal static class ItemTargetService
    {
        internal sealed class Entry
        {
            internal PsCallable Fn;
            internal Interpreter Itp;
            internal string PackId;
            internal int Line;
        }

        /// <summary>(规范化 source_id, 规范化 target_id) → 回调。</summary>
        private static readonly Dictionary<(string src, string dst), Entry> _map = new Dictionary<(string, string), Entry>();

        /// <summary>防重入: fn 执行中再触发 Target (脚本里又拖拽?) 直接放行原生。</summary>
        private static bool _reentry;

        internal static void Register(string sourceId, string targetId, PsCallable fn, Interpreter itp, int line)
        {
            var key = (ItemsFacade.NormalizeId(sourceId), ItemsFacade.NormalizeId(targetId));
            if (_map.ContainsKey(key))
                MelonLoader.MelonLogger.Warning($"[psapi] items.on_target 重复注册 {sourceId} → {targetId}, 已覆盖 (后注册生效)");
            _map[key] = new Entry { Fn = fn, Itp = itp, PackId = itp != null ? itp.PackId : "?", Line = line };
        }

        /// <summary>纯函数 (无头可测): 规范化 id 对是否命中注册表。</summary>
        internal static bool ShouldAllowIds(string sourceId, string targetId)
        {
            if (sourceId == null || targetId == null || _map.Count == 0) return false;
            return _map.ContainsKey((ItemsFacade.NormalizeId(sourceId), ItemsFacade.NormalizeId(targetId)));
        }

        /// <summary>May/Can 用: 只按 id 对命中放行, 不调 pss (每帧调用, 见类注释设计决定)。</summary>
        internal static bool ShouldAllow(GameItem source, GameItem target)
            => ShouldAllowIds(IdOf(source), IdOf(target));

        /// <summary>Target prefix 分发: fn(h_source, h_target); 明确 false → 放行原生 (true);
        /// 其他返回值 → 跳过原生 (false); 异常 → 记日志 + 放行原生 (true)。</summary>
        internal static bool TryHandle(GameItem source, GameItem target)
            => DispatchIds(IdOf(source), IdOf(target), new PsItemHandle(source), new PsItemHandle(target));

        /// <summary>分发核心 (无头可测: 句柄可传假句柄)。命中且无异常按 fn 返回值决定放行/跳过。</summary>
        internal static bool DispatchIds(string sourceId, string targetId, object hSource, object hTarget)
        {
            if (sourceId == null || targetId == null || _map.Count == 0) return true;
            if (!_map.TryGetValue((ItemsFacade.NormalizeId(sourceId), ItemsFacade.NormalizeId(targetId)), out var e)) return true;
            if (e.Fn == null || e.Itp == null) return true;
            if (_reentry) return true;
            try
            {
                _reentry = true;
                e.Itp.BeginRun();
                var ret = e.Itp.CallCallable(e.Fn, new List<object> { hSource, hTarget }, 0);
                if (ret is bool b && !b) return true;   // 明确 false = 放行原生
                return false;                            // 其他 = 跳过原生
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"[psapi] items.on_target 回调出错 ({e.PackId} 注册于行 {e.Line}), 本笔放行原生: {ex.Message}");
                return true;
            }
            finally { _reentry = false; }
        }

        private static string IdOf(GameItem item)
        {
            if (item == null) return null;
            try { return item.identifier; } catch { return null; }
        }

        /// <summary>测试台用: 清空注册表 (静态表跨段共享, 各段自清理防串扰)。</summary>
        internal static void ClearAll() => _map.Clear();

        /// <summary>测试台用: 注册表条数。</summary>
        internal static int Count => _map.Count;
    }

    [HarmonyPatch(typeof(GameItem), "MayTarget")]
    internal static class ItemTargetMayPatch
    {
        private static void Postfix(GameItem __instance, GameItem targetItem, ref bool __result)
        {
            // v1.48.0: 锁交互槽物品不能成为 use 目标端 (原生 true 也压住; 移动不经此路径)
            try { if (__result && InteractionLockRegistry.IsItemLocked(targetItem)) { __result = false; return; } } catch { }
            if (__result) return;
            try { if (ItemTargetService.ShouldAllow(__instance, targetItem)) __result = true; } catch { }
        }
    }

    [HarmonyPatch(typeof(GameItem), "CanTarget")]
    internal static class ItemTargetCanPatch
    {
        private static void Postfix(GameItem __instance, GameItem targetItem, ref bool __result)
        {
            // v1.48.0: 锁交互槽物品不能成为 use 目标端 (原生 true 也压住)
            try { if (__result && InteractionLockRegistry.IsItemLocked(targetItem)) { __result = false; return; } } catch { }
            if (__result) return;
            try { if (ItemTargetService.ShouldAllow(__instance, targetItem)) __result = true; } catch { }
        }
    }

    [HarmonyPatch(typeof(GameItem), "Target")]
    internal static class ItemTargetDoPatch
    {
        private static bool Prefix(GameItem __instance, GameItem targetItem)
        {
            // v1.48.0: 任一端位于锁交互槽 → use 全堵 (移动不经 Target 路径, 不受影响)
            try { if (InteractionLockRegistry.IsItemLocked(__instance) || InteractionLockRegistry.IsItemLocked(targetItem)) return false; } catch { }
            try { return ItemTargetService.TryHandle(__instance, targetItem); }
            catch { return true; }
        }
    }
}
