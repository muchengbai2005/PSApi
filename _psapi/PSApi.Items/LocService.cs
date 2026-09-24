using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace PSApi.Items
{
    /// <summary>
    /// 物品文本本地化服务: 合并所有包的物品文本到一张表(key 规则与原版一致:
    /// item_&lt;id&gt;_name / _desc / _flavor), 用 LocHelper.GetLocalizedItem 前缀补丁注入。
    /// 移植自 ExtraItems.ItemLocPatch。同 key 后注册者胜 + 警告(与注册表语义一致)。
    /// </summary>
    internal static class LocService
    {
        private static readonly Dictionary<string, string> _loc = new Dictionary<string, string>(StringComparer.Ordinal);
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) { _log = log; }

        internal static void Clear() => _loc.Clear();

        /// <summary>登记一个物品的三条文本(空文本跳过)。返回登记条数。</summary>
        internal static int AddItemTexts(string id, string name, string desc, string flavor, string owner)
        {
            int n = 0;
            n += Put($"item_{id}_name", name, owner) ? 1 : 0;
            n += Put($"item_{id}_desc", desc, owner) ? 1 : 0;
            n += Put($"item_{id}_flavor", flavor, owner) ? 1 : 0;
            return n;
        }

        internal static bool Put(string key, string text, string owner)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return false;
            if (_loc.ContainsKey(key))
                PsApi.Warn(_log, $"loc '{key}' overwritten by {owner}");
            _loc[key] = text;
            return true;
        }

        internal static bool TryGet(string key, out string text) => _loc.TryGetValue(key, out text);

        internal static int Count => _loc.Count;
    }

    /// <summary>本地化注入补丁: 命中包内文本表直接返回, 否则走原版管线。</summary>
    [HarmonyPatch(typeof(LocHelper), "GetLocalizedItem", new Type[] { typeof(string), typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>) })]
    internal static class ItemLocPatch
    {
        private static bool Prefix(string key, ref string __result)
        {
            if (key != null && LocService.TryGet(key, out var text))
            {
                __result = text;
                return false;
            }
            return true;
        }
    }
}
