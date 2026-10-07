using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// 脚本值工具: 值用 C# 原生类型表示 — null / bool / long / double / string /
    /// List&lt;object&gt;(数组) / Dictionary&lt;string,object&gt;(字典) / PsCallable(函数) / PsNamespace(命名空间)。
    /// </summary>
    internal static class PsValues
    {
        internal static bool IsNumber(object v) => v is long or double;

        /// <summary>truthy: null/false/0/""/空数组/空字典 = false, 其余 true。</summary>
        internal static bool Truthy(object v) => v switch
        {
            null => false,
            bool b => b,
            long l => l != 0,
            double d => d != 0.0,
            string s => s.Length > 0,
            List<object> list => list.Count > 0,
            Dictionary<string, object> dict => dict.Count > 0,
            _ => true,
        };

        /// <summary>== 按值比较; 数组/字典/函数为引用相等(v1 不递归深比); int/float 跨类型按数值比。</summary>
        internal static bool Equal(object a, object b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            if (IsNumber(a) && IsNumber(b)) return ToDouble(a) == ToDouble(b);
            if (a.GetType() != b.GetType()) return false;
            return a.Equals(b);
        }

        internal static double ToDouble(object v) => v switch
        {
            long l => l,
            double d => d,
            _ => throw new PsRuntimeError($"类型 {TypeName(v)} 不能当作数字"),
        };

        internal static string TypeName(object v) => v switch
        {
            null => "null",
            bool => "bool",
            long => "int",
            double => "float",
            string => "string",
            List<object> => "array",
            Dictionary<string, object> => "dict",
            PsCallable => "function",
            PsNamespace => "namespace",
            PsHandle => "handle",
            _ => v.GetType().Name,
        };

        /// <summary>打印格式: 与 str() / 插值 / log 输出一致; 容器递归展开(限深防环)。</summary>
        internal static string Fmt(object v) => Fmt(v, 0);

        private static string Fmt(object v, int depth)
        {
            switch (v)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d: return d.ToString("G", CultureInfo.InvariantCulture);
                case string s: return s;
                case PsCallable fn: return $"<func {fn.Name}>";
                case PsNamespace ns: return $"<{ns.Name}>";
                case PsHandle h: return $"<{h.Kind} handle>";
                case List<object> list:
                {
                    if (depth >= 4) return "[...]";
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(Fmt(list[i], depth + 1));
                    }
                    return sb.Append(']').ToString();
                }
                case Dictionary<string, object> dict:
                {
                    if (depth >= 4) return "{...}";
                    var sb = new StringBuilder("{");
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(", ");
                        first = false;
                        sb.Append(kv.Key).Append(": ").Append(Fmt(kv.Value, depth + 1));
                    }
                    return sb.Append('}').ToString();
                }
                default: return v.ToString();
            }
        }
    }

    /// <summary>
    /// 环境链: 函数局部 → 包全局。var/const 在当前环境声明;
    /// 赋值沿链查找已存在的变量(事件 handler 里改包全局是常态), 找不到则在当前环境新建。
    /// </summary>
    internal sealed class PsEnv
    {
        private readonly Dictionary<string, object> _vars = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly HashSet<string> _consts = new HashSet<string>(StringComparer.Ordinal);
        private readonly PsEnv _parent;

        internal PsEnv(PsEnv parent) { _parent = parent; }

        internal void Define(string name, object value, bool isConst, int line)
        {
            _vars[name] = value;
            if (isConst) _consts.Add(name);
            else _consts.Remove(name);
        }

        internal bool TryGet(string name, out object value)
        {
            for (var e = this; e != null; e = e._parent)
                if (e._vars.TryGetValue(name, out value))
                    return true;
            value = null;
            return false;
        }

        internal object Get(string name, int line)
        {
            if (TryGet(name, out var v)) return v;
            throw new PsRuntimeError($"未定义变量 '{name}'", line);
        }

        /// <summary>赋值: 沿链更新已存在的变量; 不存在则在当前环境新建(顶层即包全局)。</summary>
        internal void Set(string name, object value, int line)
        {
            for (var e = this; e != null; e = e._parent)
            {
                if (!e._vars.ContainsKey(name)) continue;
                if (e._consts.Contains(name))
                    throw new PsRuntimeError($"不能给常量 '{name}' 赋值", line);
                e._vars[name] = value;
                return;
            }
            _vars[name] = value;
        }

        /// <summary>本环境直接写(引擎注入 event 等用), 可覆盖常量标记之外的一切。</summary>
        internal void SetLocal(string name, object value)
        {
            _vars[name] = value;
            _consts.Remove(name);
        }
    }

    /// <summary>可调用值: 内置函数(C# 委托) 或 脚本函数(AST 闭包)。</summary>
    internal abstract class PsCallable
    {
        internal string Name;
        internal abstract object Call(Interpreter itp, List<object> args, int line);
    }

    internal sealed class BuiltinFunc : PsCallable
    {
        private readonly Func<Interpreter, List<object>, int, object> _impl;
        internal BuiltinFunc(string name, Func<Interpreter, List<object>, int, object> impl)
        {
            Name = name;
            _impl = impl;
        }
        internal override object Call(Interpreter itp, List<object> args, int line) => _impl(itp, args, line);
    }

    internal sealed class ScriptFunc : PsCallable
    {
        internal readonly List<FuncParam> Params;
        internal readonly List<Stmt> Body;
        internal readonly PsEnv Closure;

        internal ScriptFunc(string name, List<FuncParam> ps, List<Stmt> body, PsEnv closure)
        {
            Name = name;
            Params = ps;
            Body = body;
            Closure = closure;
        }

        internal override object Call(Interpreter itp, List<object> args, int line)
        {
            if (args.Count > Params.Count)
                throw new PsRuntimeError($"函数 {Name}() 最多 {Params.Count} 个参数, 实传 {args.Count}", line);
            var local = new PsEnv(Closure);
            for (int i = 0; i < Params.Count; i++)
            {
                object v = i < args.Count
                    ? args[i]
                    : Params[i].Default != null
                        ? itp.Eval(Params[i].Default, Closure)
                        : throw new PsRuntimeError($"函数 {Name}() 缺少必填参数 '{Params[i].Name}'", line);
                local.Define(Params[i].Name, v, false, line);
            }
            try
            {
                itp.ExecBlock(Body, local);
            }
            catch (PsReturn r)
            {
                return r.Value;
            }
            return null;
        }
    }

    /// <summary>内置命名空间(log 等): 只读成员表, 成员 = 方法/常量。</summary>
    internal sealed class PsNamespace
    {
        internal readonly string Name;
        internal readonly Dictionary<string, object> Members = new Dictionary<string, object>(StringComparer.Ordinal);
        internal PsNamespace(string name) { Name = name; }
    }

    /// <summary>
    /// 不透明游戏对象句柄(E4 客户等): 脚本侧 dict 风格属性访问 → GetMember 分派。
    /// 属性成员直接返回值; 方法成员返回 BuiltinFunc(复用既有 CallExpr 路径)。
    /// 未知成员抛 PsRuntimeError(友好列出可用成员由实现决定); 句柄整体只读(成员赋值由解释器拒绝)。
    /// </summary>
    internal abstract class PsHandle
    {
        /// <summary>句柄种类名(打印/type() 用, 如 "client")。</summary>
        internal abstract string Kind { get; }

        /// <summary>取成员: 属性值或方法; 未知成员抛 PsRuntimeError。</summary>
        internal abstract object GetMember(string name, int line);
    }
}
