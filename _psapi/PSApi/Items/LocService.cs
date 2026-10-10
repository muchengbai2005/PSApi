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
    /// v2.0.7: internal → public (测试台段 135 钉 Put/TryGet/IsTranslationError 行为需跨程序集访问; 行为不变)。
    /// </summary>
    public static class LocService
    {
        private static readonly Dictionary<string, string> _loc = new Dictionary<string, string>(StringComparer.Ordinal);
        private static MelonLogger.Instance _log;

        internal static void Init(MelonLogger.Instance log) { _log = log; }

        public static void Clear() => _loc.Clear();

        /// <summary>登记一个物品的三条文本(空文本跳过)。返回登记条数。</summary>
        internal static int AddItemTexts(string id, string name, string desc, string flavor, string owner)
        {
            int n = 0;
            n += Put($"item_{id}_name", name, owner) ? 1 : 0;
            n += Put($"item_{id}_desc", desc, owner) ? 1 : 0;
            n += Put($"item_{id}_flavor", flavor, owner) ? 1 : 0;
            return n;
        }

        public static bool Put(string key, string text, string owner)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return false;
            if (_loc.ContainsKey(key))
                PsApi.Warn(_log, $"loc '{key}' overwritten by {owner}");
            _loc[key] = text;
            return true;
        }

        public static bool TryGet(string key, out string text) => _loc.TryGetValue(key, out text);

        /// <summary>v2.0.7: 原版本地化管线对未注册键**返回错误字符串** "Translation Error '&lt;key&gt;' in Item"
        /// 而非抛异常 (try/catch 拦不住, 实测: 没写 flavor 的模组物品信息栏底部显示该错误串)。
        /// 凡无条件调 LocHelper.GetLocalizedItem 的路径必须用本判定剔除错误串。</summary>
        public static bool IsTranslationError(string s)
            => s != null && s.StartsWith("Translation Error", System.StringComparison.Ordinal);

        // ==================== v2.0.8: 未注册模组物品键兜底 + 存档污染清洗 ====================
        // v2.0.7 只修了建物期 (ItemStore.CreateItem 改写字段), 但错误串早已随存档序列化固化
        // (SaveItemNode.flavorText 是序列化字段) — 读档恢复的模组物品 flavorText 仍是错误串。
        // 双保险: ① ItemLocPatch 对模组物品的未注册 item_ 键兜底回退 def 文本 (任何路径实时查键
        // 都不放行原版) ② SaveLoadPatches decode 前缀清洗存档节点里已固化的错误串。

        /// <summary>模组物品定义查询注入 (ItemStore.Init 提供), 供 TryFallbackItemLoc 用。</summary>
        private static System.Func<string, (string Name, string Desc, string Flavor)?> _modItemLookup;

        /// <summary>v2.0.8: 注入模组物品文本查询 (id → def 三文本)。public 仅供测试台换桩; 运行期由 ItemStore.Init 注入。</summary>
        public static void SetModItemLookup(System.Func<string, (string Name, string Desc, string Flavor)?> lookup)
            => _modItemLookup = lookup;

        /// <summary>v2.0.8: 解析 "item_&lt;id&gt;_name/_desc/_flavor" 键, 拆出物品 id 与字段名。
        /// id 可含 ':' 与 '_' (按首尾字面值剥离, 中间全归 id)。非物品 loc 键/空 id = false。</summary>
        public static bool TryParseItemLocKey(string key, out string itemId, out string field)
        {
            itemId = null; field = null;
            const string P = "item_";
            if (string.IsNullOrEmpty(key) || !key.StartsWith(P, System.StringComparison.Ordinal)) return false;
            int suffixLen;
            if (key.EndsWith("_name", System.StringComparison.Ordinal)) { field = "name"; suffixLen = 5; }
            else if (key.EndsWith("_desc", System.StringComparison.Ordinal)) { field = "desc"; suffixLen = 5; }
            else if (key.EndsWith("_flavor", System.StringComparison.Ordinal)) { field = "flavor"; suffixLen = 7; }
            else return false;
            int idLen = key.Length - P.Length - suffixLen;
            if (idLen <= 0) return false;
            itemId = key.Substring(P.Length, idLen);
            return true;
        }

        /// <summary>v2.0.8: 模组物品的未注册 item_ 键兜底 — 命中模组 def 时给出回退文本
        /// (name→def.Name??id, desc→def.Desc??"", flavor→def.Flavor??""), **绝不放行原版**
        /// (原版管线对未注册键返回错误串)。非物品键/未注入 lookup/非模组 id = false 放行原版。</summary>
        public static bool TryFallbackItemLoc(string key, out string text)
        {
            text = null;
            if (!TryParseItemLocKey(key, out var id, out var field)) return false;
            var look = _modItemLookup;
            if (look == null) return false;
            (string Name, string Desc, string Flavor)? t;
            try { t = look(id); } catch { return false; }
            if (t == null) return false;   // 原版物品 id 不在模组注册表 → 放行原版
            text = field == "name" ? (t.Value.Name ?? id)
                 : field == "desc" ? (t.Value.Desc ?? "")
                 : (t.Value.Flavor ?? "");
            return true;
        }

        /// <summary>v2.0.8: 存档文本字段清洗 — 已固化的错误串换成回退文本, 其余 (含 null/空/正常文本) 原样返回。</summary>
        public static string ScrubTranslationError(string saved, string fallback)
            => IsTranslationError(saved) ? (fallback ?? "") : saved;

        internal static int Count => _loc.Count;
    }

    /// <summary>本地化注入补丁: 命中包内文本表直接返回; 模组物品的未注册 item_ 键兜底回退 def 文本
    /// (v2.0.8 — 原版管线对未注册键返回 "Translation Error" 错误串, 模组键绝不放行); 其余走原版管线。</summary>
    [HarmonyPatch(typeof(LocHelper), "GetLocalizedItem", new Type[] { typeof(string), typeof(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>) })]
    internal static class ItemLocPatch
    {
        private static bool Prefix(string key, ref string __result)
        {
            if (key == null) return true;
            if (LocService.TryGet(key, out var text))
            {
                __result = text;
                return false;
            }
            if (LocService.TryFallbackItemLoc(key, out var fallback))
            {
                __result = fallback;
                return false;
            }
            return true;
        }
    }
}
