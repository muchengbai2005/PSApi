using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// 脚本值 ↔ JSON 文本(SaveStates 是 string→string KV, state 命名空间经此序列化)。
    /// 支持 null/bool/int/float/string/array/dict 递归; 不可序列化值(函数/命名空间)退化为打印文本。
    /// </summary>
    internal static class ScriptJson
    {
        internal static string Encode(object v) => ToNode(v)?.ToJsonString() ?? "null";

        /// <summary>解码失败(非 JSON 原文)返回 false, 调用方按原字符串处理。</summary>
        internal static bool TryDecode(string raw, out object value)
        {
            value = null;
            if (string.IsNullOrEmpty(raw)) return false;
            JsonNode node;
            try { node = JsonNode.Parse(raw); }
            catch { return false; }
            value = FromNode(node);
            return true;
        }

        private static JsonNode ToNode(object v)
        {
            switch (v)
            {
                case null: return null;
                case bool b: return JsonValue.Create(b);
                case long l: return JsonValue.Create(l);
                case double d: return JsonValue.Create(d);
                case string s: return JsonValue.Create(s);
                case List<object> list:
                {
                    var arr = new JsonArray();
                    foreach (var item in list) arr.Add(ToNode(item));
                    return arr;
                }
                case Dictionary<string, object> dict:
                {
                    var obj = new JsonObject();
                    foreach (var kv in dict) obj[kv.Key] = ToNode(kv.Value);
                    return obj;
                }
                default:
                    return JsonValue.Create(PsValues.Fmt(v)); // 函数/命名空间等: 存打印文本, 读回是字符串
            }
        }

        private static object FromNode(JsonNode node)
        {
            switch (node)
            {
                case null: return null;
                case JsonArray arr:
                {
                    var list = new List<object>(arr.Count);
                    foreach (var item in arr) list.Add(FromNode(item));
                    return list;
                }
                case JsonObject obj:
                {
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var kv in obj) dict[kv.Key] = FromNode(kv.Value);
                    return dict;
                }
                case JsonValue jv:
                {
                    if (jv.TryGetValue<bool>(out var b)) return b;
                    if (jv.TryGetValue<long>(out var l)) return l;
                    if (jv.TryGetValue<double>(out var d)) return d;
                    if (jv.TryGetValue<string>(out var s)) return s;
                    return jv.ToString();
                }
                default:
                    return node.ToString();
            }
        }
    }
}
