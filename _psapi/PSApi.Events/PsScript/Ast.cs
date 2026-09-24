using System.Collections.Generic;

namespace PSApi.Events.PsScript
{
    // ---- AST 节点定义(不可变数据, 执行语义见 Interpreter) ----

    internal abstract class Expr
    {
        internal int Line;
    }

    /// <summary>字面量: null / bool / long / double / string(普通字符串, 插值已在 Parser 展开为拼接)。</summary>
    internal sealed class LiteralExpr : Expr
    {
        internal readonly object Value;
        internal LiteralExpr(object value) { Value = value; }
    }

    internal sealed class IdentExpr : Expr
    {
        internal readonly string Name;
        internal IdentExpr(string name) { Name = name; }
    }

    internal sealed class BinaryExpr : Expr
    {
        internal readonly Tk Op;
        internal readonly Expr Left;
        internal readonly Expr Right;
        internal BinaryExpr(Tk op, Expr l, Expr r) { Op = op; Left = l; Right = r; }
    }

    /// <summary>一元: Tk.Minus 取负 / Tk.Not 逻辑非。</summary>
    internal sealed class UnaryExpr : Expr
    {
        internal readonly Tk Op;
        internal readonly Expr Operand;
        internal UnaryExpr(Tk op, Expr e) { Op = op; Operand = e; }
    }

    internal sealed class CallExpr : Expr
    {
        internal readonly Expr Callee;
        internal readonly List<Expr> Args;
        internal CallExpr(Expr callee, List<Expr> args) { Callee = callee; Args = args; }
    }

    internal sealed class IndexExpr : Expr
    {
        internal readonly Expr Obj;
        internal readonly Expr Index;
        internal IndexExpr(Expr obj, Expr idx) { Obj = obj; Index = idx; }
    }

    internal sealed class MemberExpr : Expr
    {
        internal readonly Expr Obj;
        internal readonly string Name;
        internal MemberExpr(Expr obj, string name) { Obj = obj; Name = name; }
    }

    internal sealed class ArrayExpr : Expr
    {
        internal readonly List<Expr> Items;
        internal ArrayExpr(List<Expr> items) { Items = items; }
    }

    /// <summary>字典字面量: 键一律为字符串(标识符键按键名文本处理)。</summary>
    internal sealed class DictExpr : Expr
    {
        internal readonly List<string> Keys = new List<string>();
        internal readonly List<Expr> Values = new List<Expr>();
    }

    internal abstract class Stmt
    {
        internal int Line;
    }

    internal sealed class VarDeclStmt : Stmt
    {
        internal string Name;
        internal Expr Init;
        internal bool IsConst;
    }

    /// <summary>赋值/复合赋值: Target 限 Ident/Index/Member; Op = Assign 或 PlusEq..SlashEq。</summary>
    internal sealed class AssignStmt : Stmt
    {
        internal Expr Target;
        internal Tk Op;
        internal Expr Value;
    }

    internal sealed class ExprStmt : Stmt
    {
        internal Expr Value;
        internal ExprStmt(Expr e) { Value = e; }
    }

    internal sealed class IfBranch
    {
        internal Expr Cond;      // else 分支为 null
        internal List<Stmt> Body;
    }

    internal sealed class IfStmt : Stmt
    {
        internal readonly List<IfBranch> Branches = new List<IfBranch>();
    }

    internal sealed class WhileStmt : Stmt
    {
        internal Expr Cond;
        internal List<Stmt> Body;
    }

    internal sealed class ForStmt : Stmt
    {
        internal string VarName;
        internal Expr Iterable;
        internal List<Stmt> Body;
    }

    internal sealed class BreakStmt : Stmt { }

    internal sealed class ContinueStmt : Stmt { }

    internal sealed class PassStmt : Stmt { }

    internal sealed class ReturnStmt : Stmt
    {
        internal Expr Value; // 可空 = return null
    }

    internal sealed class FuncParam
    {
        internal string Name;
        internal Expr Default; // 可空 = 必填参数
    }

    internal sealed class FuncDefStmt : Stmt
    {
        internal string Name;
        internal readonly List<FuncParam> Params = new List<FuncParam>();
        internal List<Stmt> Body;
    }

    /// <summary>顶层 on 块: 事件名 + handler 体; 引擎登记到 EventBus, 顶层执行时跳过。
    /// Verbatim=true 表示字符串形式(on "pack:event":) — 原样订阅, 不过别名表/不加前缀。</summary>
    internal sealed class OnStmt : Stmt
    {
        internal string EventName;
        internal bool Verbatim;
        internal List<Stmt> Body;
    }

    /// <summary>一个 .pss 文件的编译产物。</summary>
    internal sealed class PsProgram
    {
        internal readonly List<Stmt> TopLevel = new List<Stmt>();     // 不含 on 块
        internal readonly List<OnStmt> Handlers = new List<OnStmt>(); // 顶层 on 块(按出现序)
    }
}
