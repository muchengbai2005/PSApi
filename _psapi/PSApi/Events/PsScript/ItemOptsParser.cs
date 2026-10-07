using System;
using System.Collections.Generic;
using PSApi.Items;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// v1.15.0 物品实例定制 opts 共享解析 (items.give 第三参 dict / sell_items/sell_pool dict 条目 /
    /// inject.sell_shelf/doctor opts dict 同一套语义同一套校验)。
    /// 键全可选: name/desc/flavor(字符串, 其他类型 Fmt 转字符串) / value(0..99999999) /
    /// quality(品质 id) / uses(1..usesMax) / data(dict, 值仅 字符串/数字/bool/null; null=删键) /
    /// to(v1.16.0: 发放落点 inventory/counter_out/counter_in, 仅 items.give/give_counter 消费)。
    /// 未知键忽略 (同 inject.tune 惯例); 本类纯托管, 无头可测。
    /// </summary>
    internal static class PsItemOpts
    {
        internal static readonly string[] KnownKeys = { "name", "desc", "flavor", "value", "quality", "uses", "data", "to" };

        /// <summary>dict → ItemOpts; 无定制键 = 返回空 opts (HasAny=false 供调用方省一次游戏调用)。</summary>
        internal static ItemsFacade.ItemOpts Parse(Dictionary<string, object> d, string api, int line, int usesMax = 9999)
        {
            var o = new ItemsFacade.ItemOpts();
            if (d.TryGetValue("name", out var v) && v != null) o.Name = PsValues.Fmt(v);
            if (d.TryGetValue("desc", out v) && v != null) o.Desc = PsValues.Fmt(v);
            if (d.TryGetValue("flavor", out v) && v != null) o.Flavor = PsValues.Fmt(v);
            if (d.TryGetValue("value", out v) && v != null)
            {
                long val = AsNum(v, api + ".value", line);
                if (val < 0 || val > 99999999)
                    throw new PsRuntimeError($"{api} 的 value 须在 0..99999999, 实为 {val}", line);
                o.Value = val;
            }
            if (d.TryGetValue("quality", out v) && v != null)
            {
                if (v is not string q || string.IsNullOrWhiteSpace(q))
                    throw new PsRuntimeError($"{api} 的 quality 须为非空字符串, 实为 {PsValues.TypeName(v)}", line);
                o.Quality = q;
            }
            if (d.TryGetValue("uses", out v) && v != null)
            {
                long n = AsNum(v, api + ".uses", line);
                if (n < 1 || n > usesMax)
                    throw new PsRuntimeError($"{api} 的 uses 须在 1..{usesMax}, 实为 {n}", line);
                o.Uses = (int)n;
            }
            if (d.TryGetValue("to", out v) && v != null)
            {
                // v1.16.0: 发放落点路由 (仅 items.give/give_counter 消费; 售卖条目里写了不生效)
                if (v is not string toRaw || string.IsNullOrWhiteSpace(toRaw))
                    throw new PsRuntimeError($"{api} 的 to 须为非空字符串(inventory/counter_out/counter_in), 实为 {PsValues.TypeName(v)}", line);
                string to = toRaw.Trim().ToLowerInvariant();
                if (to != "inventory" && to != "counter_out" && to != "counter_in")
                    throw new PsRuntimeError($"{api} 的 to 仅支持 inventory/counter_out/counter_in, 实为 '{toRaw}'", line);
                o.To = to;
            }
            if (d.TryGetValue("data", out v) && v != null)
            {
                if (v is not Dictionary<string, object> dm)
                    throw new PsRuntimeError($"{api} 的 data 须为 dict, 实为 {PsValues.TypeName(v)}", line);
                o.Data = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var kv in dm)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        throw new PsRuntimeError($"{api} 的 data 含空键", line);
                    switch (kv.Value)
                    {
                        case null: o.Data[kv.Key] = null; break; // null = 删除该键
                        case string: case long: case double: case bool:
                            o.Data[kv.Key] = kv.Value; break;
                        // v1.16.2: 嵌套 dict/list 允许 (SetData 走 j:JSON 编码持久化)
                        case Dictionary<string, object>: case List<object>:
                            o.Data[kv.Key] = kv.Value; break;
                        default:
                            throw new PsRuntimeError($"{api} 的 data['{kv.Key}'] 仅支持 字符串/数字/bool/dict/list/null, 实为 {PsValues.TypeName(kv.Value)}", line);
                    }
                }
            }
            return o;
        }

        /// <summary>opts 是否含任何定制 (null/空 = 摆货点可不调用)。</summary>
        internal static bool HasAny(ItemsFacade.ItemOpts o)
            => o != null && (o.Name != null || o.Desc != null || o.Flavor != null || o.Value.HasValue
                || !string.IsNullOrEmpty(o.Quality) || o.Uses > 0 || (o.Data != null && o.Data.Count > 0));

        private static long AsNum(object v, string api, int line)
        {
            if (v is long l) return l;
            if (v is double d) return (long)d;
            throw new PsRuntimeError($"{api} 的数值参数须为数字, 实为 {PsValues.TypeName(v)}", line);
        }
    }
}
