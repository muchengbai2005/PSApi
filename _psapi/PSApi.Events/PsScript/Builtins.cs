using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MelonLoader;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// E1 内置面: log.info/warn/err 命名空间(落 MelonLoader 日志, 前缀 [pss &lt;包id&gt;])
    /// + 标准库全局函数 len/str/int/float/bool/type/range/push/pop/keys/values/has/remove/
    /// join/split/format/rand/randf/floor/ceil/abs/min/max/clamp。
    /// 每包一份(闭包捕获包 id), 注册进包全局环境。
    /// </summary>
    internal static class PsBuiltins
    {
        private static readonly Random Rng = new Random();

        /// <summary>创建包全局环境: 标准库函数 + log 命名空间 + bus.emit(自定义事件, 直通 EventBus)。</summary>
        internal static PsEnv CreatePackEnv(MelonLogger.Instance logger, string packId, EventBus bus)
        {
            var env = new PsEnv(null);
            var log = new PsNamespace("log");
            log.Members["info"] = BF("log.info", (itp, args, line) =>
            {
                Need(args, 1, 1, "log.info(text)", line);
                PsApi.Log(logger, $"[pss {packId}] {PsValues.Fmt(args[0])}");
                return null;
            });
            log.Members["warn"] = BF("log.warn", (itp, args, line) =>
            {
                Need(args, 1, 1, "log.warn(text)", line);
                PsApi.Warn(logger, $"[pss {packId}] {PsValues.Fmt(args[0])}");
                return null;
            });
            log.Members["err"] = BF("log.err", (itp, args, line) =>
            {
                Need(args, 1, 1, "log.err(text)", line);
                PsApi.Err(logger, $"[pss {packId}] {PsValues.Fmt(args[0])}");
                return null;
            });
            env.Define("log", log, true, 0);

            // bus.emit: 自定义事件(跨包协作通道), payload 须为 dict(可省略 = 空 dict)
            var busNs = new PsNamespace("bus");
            busNs.Members["emit"] = BF("bus.emit", (itp, args, line) =>
            {
                Need(args, 1, 2, "bus.emit(事件名[, dict])", line);
                if (args[0] is not string name || name.Length == 0)
                    throw new PsRuntimeError("bus.emit() 事件名须为非空字符串(建议 '包id:事件名' 形式)", line);
                object payload;
                if (args.Count == 1 || args[1] == null)
                    payload = new Dictionary<string, object>(StringComparer.Ordinal);
                else if (args[1] is Dictionary<string, object> d)
                    payload = d;
                else
                    throw new PsRuntimeError("bus.emit() 的 payload 须为 dict", line);
                bus?.Publish(name, payload);
                return null;
            });
            env.Define("bus", busNs, true, 0);

            foreach (var fn in StdLib()) env.Define(fn.Name, fn, true, 0);
            return env;
        }

        internal static BuiltinFunc BF(string name, Func<Interpreter, List<object>, int, object> impl)
            => new BuiltinFunc(name, impl);

        internal static void Need(List<object> args, int min, int max, string sig, int line)
        {
            if (args.Count < min || args.Count > max)
                throw new PsRuntimeError($"参数个数错误(实传 {args.Count}), 用法: {sig}", line);
        }

        private static IEnumerable<BuiltinFunc> StdLib()
        {
            yield return BF("len", (itp, a, line) =>
            {
                Need(a, 1, 1, "len(x)", line);
                return a[0] switch
                {
                    string s => (long)s.Length,
                    List<object> l => (long)l.Count,
                    Dictionary<string, object> d => (long)d.Count,
                    _ => throw new PsRuntimeError($"len() 不支持类型 {PsValues.TypeName(a[0])}", line),
                };
            });
            yield return BF("str", (itp, a, line) =>
            {
                Need(a, 1, 1, "str(x)", line);
                return PsValues.Fmt(a[0]);
            });
            yield return BF("int", (itp, a, line) =>
            {
                Need(a, 1, 1, "int(x)", line);
                return a[0] switch
                {
                    long l => l,
                    double d => (long)d,
                    bool b => b ? 1L : 0L,
                    string s => ParseLong(s, line),
                    _ => throw new PsRuntimeError($"int() 不支持类型 {PsValues.TypeName(a[0])}", line),
                };
            });
            yield return BF("float", (itp, a, line) =>
            {
                Need(a, 1, 1, "float(x)", line);
                return a[0] switch
                {
                    long l => (double)l,
                    double d => d,
                    bool b => b ? 1.0 : 0.0,
                    string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        ? d
                        : throw new PsRuntimeError($"float() 无法解析 '{s}'", line),
                    _ => throw new PsRuntimeError($"float() 不支持类型 {PsValues.TypeName(a[0])}", line),
                };
            });
            yield return BF("bool", (itp, a, line) =>
            {
                Need(a, 1, 1, "bool(x)", line);
                return PsValues.Truthy(a[0]);
            });
            yield return BF("type", (itp, a, line) =>
            {
                Need(a, 1, 1, "type(x)", line);
                return PsValues.TypeName(a[0]);
            });
            yield return BF("range", (itp, a, line) =>
            {
                Need(a, 1, 3, "range(终点) / range(起点, 终点[, 步长])", line);
                long lo = 0, hi, step = 1;
                if (a.Count == 1) hi = AsLong(a[0], "range", line);
                else
                {
                    lo = AsLong(a[0], "range", line);
                    hi = AsLong(a[1], "range", line);
                    if (a.Count == 3) step = AsLong(a[2], "range", line);
                }
                if (step == 0) throw new PsRuntimeError("range() 步长不能为 0", line);
                var r = new List<object>();
                if (step > 0) for (long i = lo; i < hi; i += step) r.Add(i);
                else for (long i = lo; i > hi; i += step) r.Add(i);
                return r;
            });
            yield return BF("push", (itp, a, line) =>
            {
                Need(a, 2, 2, "push(array, value)", line);
                if (a[0] is not List<object> l) throw new PsRuntimeError("push() 第一个参数须为 array", line);
                l.Add(a[1]);
                return null;
            });
            yield return BF("pop", (itp, a, line) =>
            {
                Need(a, 1, 1, "pop(array)", line);
                if (a[0] is not List<object> l) throw new PsRuntimeError("pop() 参数须为 array", line);
                if (l.Count == 0) return null;
                var v = l[l.Count - 1];
                l.RemoveAt(l.Count - 1);
                return v;
            });
            yield return BF("keys", (itp, a, line) =>
            {
                Need(a, 1, 1, "keys(dict)", line);
                if (a[0] is not Dictionary<string, object> d) throw new PsRuntimeError("keys() 参数须为 dict", line);
                return new List<object>(d.Keys);
            });
            yield return BF("values", (itp, a, line) =>
            {
                Need(a, 1, 1, "values(dict)", line);
                if (a[0] is not Dictionary<string, object> d) throw new PsRuntimeError("values() 参数须为 dict", line);
                return new List<object>(d.Values);
            });
            yield return BF("has", (itp, a, line) =>
            {
                Need(a, 2, 2, "has(容器, 键/值/子串)", line);
                return a[0] switch
                {
                    Dictionary<string, object> d => a[1] is string k && d.ContainsKey(k),
                    List<object> l => ContainsValue(l, a[1]),
                    string s => a[1] is string sub && s.Contains(sub, StringComparison.Ordinal),
                    _ => throw new PsRuntimeError($"has() 不支持类型 {PsValues.TypeName(a[0])}", line),
                };
            });
            yield return BF("remove", (itp, a, line) =>
            {
                Need(a, 2, 2, "remove(dict, 键) / remove(array, 值)", line);
                if (a[0] is Dictionary<string, object> d)
                {
                    if (a[1] is not string k) throw new PsRuntimeError("remove(dict, 键) 的键须为字符串", line);
                    return d.Remove(k);
                }
                if (a[0] is List<object> l)
                {
                    for (int i = 0; i < l.Count; i++)
                        if (PsValues.Equal(l[i], a[1])) { l.RemoveAt(i); return true; }
                    return false;
                }
                throw new PsRuntimeError($"remove() 不支持类型 {PsValues.TypeName(a[0])}", line);
            });
            yield return BF("join", (itp, a, line) =>
            {
                Need(a, 1, 2, "join(array[, 分隔符])", line);
                if (a[0] is not List<object> l) throw new PsRuntimeError("join() 第一个参数须为 array", line);
                string sep = a.Count == 2 ? PsValues.Fmt(a[1]) : "";
                var parts = new string[l.Count];
                for (int i = 0; i < l.Count; i++) parts[i] = PsValues.Fmt(l[i]);
                return string.Join(sep, parts);
            });
            yield return BF("split", (itp, a, line) =>
            {
                Need(a, 2, 2, "split(string, 分隔符)", line);
                if (a[0] is not string s || a[1] is not string sep)
                    throw new PsRuntimeError("split() 两个参数都须为字符串", line);
                if (sep.Length == 0) throw new PsRuntimeError("split() 分隔符不能为空", line);
                var r = new List<object>();
                foreach (var part in s.Split(new[] { sep }, StringSplitOptions.None)) r.Add(part);
                return r;
            });
            yield return BF("format", (itp, a, line) =>
            {
                Need(a, 1, int.MaxValue, "format(模板, 参数...)", line);
                if (a[0] is not string fmt) throw new PsRuntimeError("format() 模板须为字符串", line);
                return Format(fmt, a, 1, line);
            });
            yield return BF("rand", (itp, a, line) =>
            {
                Need(a, 1, 2, "rand(上界) → 0..上界-1 / rand(下界, 上界) → 闭区间", line);
                if (a.Count == 1)
                {
                    long hi = AsLong(a[0], "rand", line);
                    if (hi <= 0) throw new PsRuntimeError("rand(上界) 上界须为正数", line);
                    return (long)Rng.Next((int)Math.Min(hi, int.MaxValue));
                }
                long lo = AsLong(a[0], "rand", line), hi2 = AsLong(a[1], "rand", line);
                if (hi2 < lo) throw new PsRuntimeError("rand(下界, 上界) 上界小于下界", line);
                return lo + (long)Math.Floor(Rng.NextDouble() * (hi2 - lo + 1));
            });
            yield return BF("randf", (itp, a, line) =>
            {
                Need(a, 0, 2, "randf() → [0,1) / randf(下界, 上界)", line);
                if (a.Count == 0) return Rng.NextDouble();
                double lo = PsValues.ToDouble(a[0]), hi = PsValues.ToDouble(a[1]);
                return lo + Rng.NextDouble() * (hi - lo);
            });
            yield return BF("floor", (itp, a, line) =>
            {
                Need(a, 1, 1, "floor(x)", line);
                return a[0] is long l ? l : (long)Math.Floor(PsValues.ToDouble(a[0]));
            });
            yield return BF("ceil", (itp, a, line) =>
            {
                Need(a, 1, 1, "ceil(x)", line);
                return a[0] is long l ? l : (long)Math.Ceiling(PsValues.ToDouble(a[0]));
            });
            yield return BF("abs", (itp, a, line) =>
            {
                Need(a, 1, 1, "abs(x)", line);
                return a[0] switch
                {
                    long l => (object)Math.Abs(l),
                    double d => Math.Abs(d),
                    _ => throw new PsRuntimeError("abs() 参数须为数字", line),
                };
            });
            yield return BF("min", (itp, a, line) =>
            {
                Need(a, 2, 2, "min(a, b)", line);
                return MinMax(a[0], a[1], line, true);
            });
            yield return BF("max", (itp, a, line) =>
            {
                Need(a, 2, 2, "max(a, b)", line);
                return MinMax(a[0], a[1], line, false);
            });
            yield return BF("clamp", (itp, a, line) =>
            {
                Need(a, 3, 3, "clamp(x, 下界, 上界)", line);
                return MinMax(MinMax(a[0], a[1], line, false), a[2], line, true);
            });
        }

        private static long ParseLong(string s, int line)
        {
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return (long)d;
            throw new PsRuntimeError($"int() 无法解析 '{s}'", line);
        }

        private static long AsLong(object v, string fn, int line)
        {
            if (v is long l) return l;
            throw new PsRuntimeError($"{fn}() 参数须为整数, 实为 {PsValues.TypeName(v)}", line);
        }

        private static bool ContainsValue(List<object> l, object v)
        {
            foreach (var item in l)
                if (PsValues.Equal(item, v)) return true;
            return false;
        }

        private static object MinMax(object a, object b, int line, bool min)
        {
            if (!PsValues.IsNumber(a) || !PsValues.IsNumber(b))
                throw new PsRuntimeError("min/max/clamp 参数须为数字", line);
            int cmp = PsValues.ToDouble(a).CompareTo(PsValues.ToDouble(b));
            return min ? (cmp <= 0 ? a : b) : (cmp >= 0 ? a : b);
        }

        /// <summary>format 模板: {} 顺序占位 / {0} 索引占位 / {{ }} 字面花括号, 与插值语义一致。</summary>
        private static string Format(string fmt, List<object> args, int argBase, int line)
        {
            var sb = new StringBuilder();
            int auto = 0;
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (c == '{')
                {
                    if (i + 1 < fmt.Length && fmt[i + 1] == '{') { sb.Append('{'); i++; continue; }
                    int close = fmt.IndexOf('}', i + 1);
                    if (close < 0) throw new PsRuntimeError("format() 模板缺少 }", line);
                    string spec = fmt.Substring(i + 1, close - i - 1);
                    int idx;
                    if (spec.Length == 0) idx = auto++;
                    else if (!int.TryParse(spec, out idx))
                        throw new PsRuntimeError($"format() 占位符 '{{{spec}}}' 应为 {{}} 或 {{索引}}", line);
                    if (idx < 0 || argBase + idx >= args.Count)
                        throw new PsRuntimeError($"format() 占位符索引 {idx} 超出参数个数({args.Count - argBase})", line);
                    sb.Append(PsValues.Fmt(args[argBase + idx]));
                    i = close;
                    continue;
                }
                if (c == '}' && i + 1 < fmt.Length && fmt[i + 1] == '}') { sb.Append('}'); i++; continue; }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
