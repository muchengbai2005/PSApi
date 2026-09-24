using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 槽位过滤器包装 (v0.5.11): 放宽原生槽位过滤，让玩家能手动拖拽非原生物品进槽。
    ///
    /// 原理: 读取槽位的原生 mayInventoryAddItemFunc 委托，包装为新委托:
    ///   新 mayAdd(item, inv) = native(item, inv) || whitelist.Contains(item.identifier)
    /// 原委托行为完全保留 (作为内层调用)，只是多一个 OR 分支。
    /// 其他配套委托 (onSlotAdd/onSlotRemove/maxSlotAmount) 不变 → 拖拽协议不破坏。
    ///
    /// 与 v0.5.6 事故的区别: 那次把 mayAdd 整体替换为恒真谓词，丢弃原委托 → 拖拽失灵。
    /// 这里是包装而非替换，原委托的所有行为保留。
    ///
    /// 用途: desequencer 输入槽原生只收 ACCESS_CARD，包装后可放行白名单中的物品 (如 scrap_metal)。
    /// </summary>
    internal static class SlotFilterService
    {
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) => _log = log;

        /// <summary>
        /// 包装指定槽位的 mayAdd 委托，放行 whitelist 中的物品 id。
        /// 幂等: 重复调用只是覆盖包装，无累积副作用 (每次读当前 mayAdd 再包装)。
        /// </summary>
        internal static bool WrapSlotFilter(GameInventory slot, HashSet<string> whitelist, string label, List<object> pinned)
        {
            if (slot == null || whitelist == null || whitelist.Count == 0) return false;
            if (pinned == null) pinned = new List<object>();

            Il2CppSystem.Func<GameItem, GameInventory, bool> nativeMayAdd = null;
            try { nativeMayAdd = slot.mayInventoryAddItemFunc; }
            catch (Exception e) { PsApi.Warn(_log, $"WrapSlotFilter: read native mayAdd failed ({label}): {e.Message}"); return false; }

            var capturedWhitelist = whitelist;
            var capturedNative = nativeMayAdd;
            var capturedLabel = label;

            Func<GameItem, GameInventory, bool> wrapped = (item, inv) =>
            {
                try
                {
                    if (capturedNative != null)
                    {
                        try { if (capturedNative.Invoke(item, inv)) return true; }
                        catch { }
                    }
                    if (item != null)
                    {
                        string id = null;
                        try { id = item.identifier; } catch { }
                        if (!string.IsNullOrEmpty(id) && capturedWhitelist.Contains(id)) return true;
                    }
                }
                catch { }
                return false;
            };

            var del = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<GameItem, GameInventory, bool>>(wrapped);
            pinned.Add(wrapped);
            pinned.Add(del);
            if (capturedNative != null) pinned.Add(capturedNative);

            try { slot.mayInventoryAddItemFunc = del; }
            catch (Exception e) { PsApi.Warn(_log, $"WrapSlotFilter: set wrapped mayAdd failed ({label}): {e.Message}"); return false; }

            PsApi.Log(_log, $"slot filter wrapped: {label} whitelist=[{string.Join(",", capturedWhitelist)}] native={(capturedNative != null ? "kept" : "null")}");
            return true;
        }

        /// <summary>构建白名单 HashSet (忽略大小写)。</summary>
        internal static HashSet<string> BuildWhitelist(List<string> ids)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ids != null)
                foreach (var id in ids)
                    if (!string.IsNullOrWhiteSpace(id)) set.Add(id.Trim());
            return set;
        }
    }
}