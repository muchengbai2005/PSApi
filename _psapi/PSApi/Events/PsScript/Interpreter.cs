using System;
using System.Collections.Generic;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// 树遍历解释器。每包一个实例(持有包全局环境)。
    /// 熔断: 每段执行(顶层/handler)10 万步预算, 每 128 条语句检查一次; 调用深度上限 64。
    /// 异常语义: PsRuntimeError 由引擎记录; PsBreak/PsContinue/PsReturn 是控制流信号。
    /// </summary>
    internal sealed class Interpreter
    {
        internal const int StepBudget = 100_000;
        private const int CheckEvery = 128;
        internal const int MaxCallDepth = 64;

        internal readonly string PackId;
        internal PsEnv Global;

        private int _steps;
        private int _depth;

        internal Interpreter(string packId)
        {
            PackId = packId;
        }

        /// <summary>开启一段独立执行(顶层语句或一次 handler 触发): 重置预算与深度。</summary>
        internal void BeginRun()
        {
            _steps = 0;
            _depth = 0;
        }

        private void Step(int line)
        {
            // 每 CheckEvery 条语句才比较预算, 控制开销
            if (++_steps % CheckEvery == 0 && _steps > StepBudget)
                throw new PsRuntimeError($"执行步数超过预算({StepBudget}), 疑似死循环", line);
        }

        // ---- 语句执行 ----

        /// <summary>执行一组语句(块不产生新作用域, 与 GDScript 一致: 作用域到函数为止)。</summary>
        internal void ExecBlock(List<Stmt> stmts, PsEnv env)
        {
            foreach (var s in stmts) Exec(s, env);
        }

        internal void Exec(Stmt s, PsEnv env)
        {
            Step(s.Line);
            switch (s)
            {
                case VarDeclStmt v:
                    env.Define(v.Name, Eval(v.Init, env), v.IsConst, v.Line);
                    break;
                case AssignStmt a:
                    ExecAssign(a, env);
                    break;
                case ExprStmt e:
                    Eval(e.Value, env);
                    break;
                case IfStmt iff:
                    foreach (var br in iff.Branches)
                    {
                        if (br.Cond == null || PsValues.Truthy(Eval(br.Cond, env)))
                        {
                            ExecBlock(br.Body, env);
                            break;
                        }
                    }
                    break;
                case WhileStmt w:
                    try
                    {
                        while (PsValues.Truthy(Eval(w.Cond, env)))
                        {
                            Step(w.Line);
                            try { ExecBlock(w.Body, env); }
                            catch (PsContinue) { }
                        }
                    }
                    catch (PsBreak) { }
                    break;
                case ForStmt f:
                    ExecFor(f, env);
                    break;
                case BreakStmt:
                    throw new PsBreak();
                case ContinueStmt:
                    throw new PsContinue();
                case PassStmt:
                    break;
                case ReturnStmt r:
                    throw new PsReturn(r.Value == null ? null : Eval(r.Value, env));
                case FuncDefStmt fn:
                    env.Define(fn.Name, new ScriptFunc(fn.Name, fn.Params, fn.Body, env), false, fn.Line);
                    break;
                case OnStmt:
                    break; // 引擎在装载期已登记, 顶层执行跳过
                default:
                    throw new PsRuntimeError("未知语句节点 " + s.GetType().Name, s.Line);
            }
        }

        private void ExecFor(ForStmt f, PsEnv env)
        {
            var iterable = Eval(f.Iterable, env);
            IEnumerable<object> seq = iterable switch
            {
                List<object> list => new List<object>(list), // 快照, 允许循环体内改原数组
                Dictionary<string, object> dict => new List<object>(dict.Keys),
                string s => CharsOf(s),
                _ => throw new PsRuntimeError($"类型 {PsValues.TypeName(iterable)} 不可迭代(for 需要 array/dict/string)", f.Line),
            };
            try
            {
                foreach (var item in seq)
                {
                    Step(f.Line);
                    env.Set(f.VarName, item, f.Line);
                    try { ExecBlock(f.Body, env); }
                    catch (PsContinue) { }
                }
            }
            catch (PsBreak) { }
        }

        private static IEnumerable<object> CharsOf(string s)
        {
            foreach (var c in s) yield return c.ToString();
        }

        private void ExecAssign(AssignStmt a, PsEnv env)
        {
            if (a.Op == Tk.Assign)
            {
                AssignTo(a.Target, Eval(a.Value, env), env, a.Line);
                return;
            }
            // 复合赋值: 读出-运算-写回(与二元运算同一套语义, 含字符串拼接/数组拼接)
            var cur = Eval(a.Target, env);
            var rhs = Eval(a.Value, env);
            var op = a.Op switch
            {
                Tk.PlusEq => Tk.Plus,
                Tk.MinusEq => Tk.Minus,
                Tk.StarEq => Tk.Star,
                Tk.SlashEq => Tk.Slash,
                _ => throw new PsRuntimeError("不支持的复合赋值", a.Line),
            };
            AssignTo(a.Target, ApplyOp(op, cur, rhs, a.Line), env, a.Line);
        }

        private void AssignTo(Expr target, object value, PsEnv env, int line)
        {
            switch (target)
            {
                case IdentExpr id:
                    env.Set(id.Name, value, line);
                    break;
                case IndexExpr ix:
                {
                    var obj = Eval(ix.Obj, env);
                    var idx = Eval(ix.Index, env);
                    if (obj is List<object> list)
                    {
                        int i = NormIndex(idx, list.Count, line);
                        list[i] = value;
                    }
                    else if (obj is Dictionary<string, object> dict)
                    {
                        if (idx is not string k) throw new PsRuntimeError("字典索引须为字符串", line);
                        dict[k] = value;
                    }
                    else throw new PsRuntimeError($"类型 {PsValues.TypeName(obj)} 不支持索引赋值", line);
                    break;
                }
                case MemberExpr m:
                {
                    var obj = Eval(m.Obj, env);
                    if (obj is Dictionary<string, object> dict) dict[m.Name] = value;
                    else if (obj is PsNamespace) throw new PsRuntimeError("命名空间成员只读, 不能赋值", line);
                    else if (obj is PsHandle h) throw new PsRuntimeError($"句柄({h.Kind})成员只读, 不能赋值(用其方法, 如 say/set_cash)", line);
                    else throw new PsRuntimeError($"类型 {PsValues.TypeName(obj)} 不支持成员赋值", line);
                    break;
                }
                default:
                    throw new PsRuntimeError("赋值目标必须是变量/索引/成员", line);
            }
        }

        // ---- 表达式求值 ----

        internal object Eval(Expr e, PsEnv env)
        {
            switch (e)
            {
                case LiteralExpr lit: return lit.Value;
                case IdentExpr id: return env.Get(id.Name, e.Line);
                case UnaryExpr u:
                {
                    var v = Eval(u.Operand, env);
                    if (u.Op == Tk.Not) return !PsValues.Truthy(v);
                    // 一元取负
                    if (v is long l) return -l;
                    if (v is double d) return -d;
                    throw new PsRuntimeError($"类型 {PsValues.TypeName(v)} 不能取负", e.Line);
                }
                case BinaryExpr b: return EvalBinary(b, env);
                case ArrayExpr arr:
                {
                    var list = new List<object>(arr.Items.Count);
                    foreach (var item in arr.Items) list.Add(Eval(item, env));
                    return list;
                }
                case DictExpr de:
                {
                    var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (int i = 0; i < de.Keys.Count; i++)
                        dict[de.Keys[i]] = Eval(de.Values[i], env);
                    return dict;
                }
                case IndexExpr ix:
                {
                    var obj = Eval(ix.Obj, env);
                    var idx = Eval(ix.Index, env);
                    return IndexOf(obj, idx, e.Line);
                }
                case MemberExpr m:
                {
                    var obj = Eval(m.Obj, env);
                    if (obj is Dictionary<string, object> dict)
                        return dict.TryGetValue(m.Name, out var v) ? v : null; // 缺键宽容返回 null(事件 dict 常态)
                    if (obj is PsNamespace ns)
                    {
                        if (ns.Members.TryGetValue(m.Name, out var v)) return v;
                        throw new PsRuntimeError($"命名空间 {ns.Name} 无成员 '{m.Name}'", e.Line);
                    }
                    if (obj is PsHandle h)
                        return h.GetMember(m.Name, e.Line);
                    throw new PsRuntimeError($"类型 {PsValues.TypeName(obj)} 不支持成员访问 '.'", e.Line);
                }
                case CallExpr call:
                {
                    var callee = Eval(call.Callee, env);
                    if (callee is not PsCallable fn)
                        throw new PsRuntimeError($"类型 {PsValues.TypeName(callee)} 不可调用", e.Line);
                    var args = new List<object>(call.Args.Count);
                    foreach (var a in call.Args) args.Add(Eval(a, env));
                    return CallCallable(fn, args, e.Line);
                }
                default:
                    throw new PsRuntimeError("未知表达式节点 " + e.GetType().Name, e.Line);
            }
        }

        internal object CallCallable(PsCallable fn, List<object> args, int line)
        {
            if (++_depth > MaxCallDepth)
            {
                _depth--;
                throw new PsRuntimeError($"调用深度超过上限({MaxCallDepth}), 疑似无限递归", line);
            }
            try { return fn.Call(this, args, line); }
            finally { _depth--; }
        }

        private object EvalBinary(BinaryExpr b, PsEnv env)
        {
            // and/or 短路
            if (b.Op == Tk.And)
            {
                var l = Eval(b.Left, env);
                return !PsValues.Truthy(l) ? l : Eval(b.Right, env);
            }
            if (b.Op == Tk.Or)
            {
                var l = Eval(b.Left, env);
                return PsValues.Truthy(l) ? l : Eval(b.Right, env);
            }
            var left = Eval(b.Left, env);
            var right = Eval(b.Right, env);
            return ApplyOp(b.Op, left, right, b.Line);
        }

        /// <summary>二元运算统一入口(表达式与复合赋值共用)。</summary>
        internal static object ApplyOp(Tk op, object left, object right, int line)
        {
            switch (op)
            {
                case Tk.Plus:
                {
                    if (left is string || right is string)
                        return PsValues.Fmt(left) + PsValues.Fmt(right);
                    if (left is List<object> la && right is List<object> lb)
                    {
                        var r = new List<object>(la.Count + lb.Count);
                        r.AddRange(la);
                        r.AddRange(lb);
                        return r;
                    }
                    return Arith(Tk.Plus, left, right, line);
                }
                case Tk.Minus: return Arith(Tk.Minus, left, right, line);
                case Tk.Star: return Arith(Tk.Star, left, right, line);
                case Tk.Slash: return Arith(Tk.Slash, left, right, line);
                case Tk.Percent: return Arith(Tk.Percent, left, right, line);
                case Tk.EqEq: return PsValues.Equal(left, right);
                case Tk.NotEq: return !PsValues.Equal(left, right);
                case Tk.Lt: return Compare(left, right, line) < 0;
                case Tk.LtEq: return Compare(left, right, line) <= 0;
                case Tk.Gt: return Compare(left, right, line) > 0;
                case Tk.GtEq: return Compare(left, right, line) >= 0;
                case Tk.In:
                {
                    if (right is List<object> list)
                    {
                        foreach (var item in list)
                            if (PsValues.Equal(left, item)) return true;
                        return false;
                    }
                    if (right is Dictionary<string, object> dict)
                    {
                        if (left is not string k) throw new PsRuntimeError("dict 的 in 左操作数须为字符串(键)", line);
                        return dict.ContainsKey(k);
                    }
                    if (right is string s)
                    {
                        if (left is not string sub) throw new PsRuntimeError("string 的 in 左操作数须为字符串(子串)", line);
                        return s.Contains(sub, StringComparison.Ordinal);
                    }
                    throw new PsRuntimeError($"类型 {PsValues.TypeName(right)} 不支持 in 运算", line);
                }
                default:
                    throw new PsRuntimeError("未知运算符 " + op, line);
            }
        }

        /// <summary>数值运算: int+float=float; 双 int 的 / % 按整数截断(GDScript 直觉)。</summary>
        private static object Arith(Tk op, object a, object b, int line)
        {
            if (a is long la && b is long lb)
            {
                switch (op)
                {
                    case Tk.Plus: return la + lb;
                    case Tk.Minus: return la - lb;
                    case Tk.Star: return la * lb;
                    case Tk.Slash:
                        if (lb == 0) throw new PsRuntimeError("除以零", line);
                        return la / lb;
                    case Tk.Percent:
                        if (lb == 0) throw new PsRuntimeError("对零取模", line);
                        return la % lb;
                }
            }
            if (PsValues.IsNumber(a) && PsValues.IsNumber(b))
            {
                double da = PsValues.ToDouble(a), db = PsValues.ToDouble(b);
                return op switch
                {
                    Tk.Plus => da + db,
                    Tk.Minus => da - db,
                    Tk.Star => da * db,
                    Tk.Slash => da / db,
                    Tk.Percent => da % db,
                    _ => null,
                };
            }
            throw new PsRuntimeError($"类型 {PsValues.TypeName(a)} 与 {PsValues.TypeName(b)} 不支持算术运算", line);
        }

        /// <summary>大小比较: 数字跨 int/float 按数值; 字符串按序号; 其余报错。</summary>
        private static int Compare(object a, object b, int line)
        {
            if (PsValues.IsNumber(a) && PsValues.IsNumber(b))
                return PsValues.ToDouble(a).CompareTo(PsValues.ToDouble(b));
            if (a is string sa && b is string sb)
                return string.CompareOrdinal(sa, sb);
            throw new PsRuntimeError($"类型 {PsValues.TypeName(a)} 与 {PsValues.TypeName(b)} 不能比较大小", line);
        }

        private object IndexOf(object obj, object idx, int line)
        {
            if (obj is List<object> list) return list[NormIndex(idx, list.Count, line)];
            if (obj is Dictionary<string, object> dict)
            {
                if (idx is not string k) throw new PsRuntimeError("字典索引须为字符串", line);
                return dict.TryGetValue(k, out var v) ? v : null;
            }
            if (obj is string s)
            {
                int i = NormIndex(idx, s.Length, line);
                return s[i].ToString();
            }
            throw new PsRuntimeError($"类型 {PsValues.TypeName(obj)} 不支持索引", line);
        }

        /// <summary>索引规范化: 支持负数(从尾部计), 越界报错。</summary>
        private static int NormIndex(object idx, int count, int line)
        {
            if (idx is not long li) throw new PsRuntimeError("索引须为整数", line);
            long i = li < 0 ? count + li : li;
            if (i < 0 || i >= count) throw new PsRuntimeError($"索引越界: {li}(长度 {count})", line);
            return (int)i;
        }
    }
}
