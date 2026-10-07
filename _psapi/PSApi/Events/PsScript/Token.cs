using System.Collections.Generic;

namespace PSApi.Events.PsScript
{
    /// <summary>Token 类别。缩进块在 Lexer 阶段转为 Indent/Dedent, 语句以 Newline 收尾(参照 Python 方案)。</summary>
    internal enum Tk
    {
        Eof, Newline, Indent, Dedent,
        Ident, Int, Float, Str,

        // 关键字
        Var, Const, Func, If, Elif, Else, For, While, In, Break, Continue, Return, Pass,
        And, Or, Not, True, False, Null, On,

        // 运算符/标点
        Plus, Minus, Star, Slash, Percent,
        EqEq, NotEq, Lt, LtEq, Gt, GtEq,
        Assign, PlusEq, MinusEq, StarEq, SlashEq,
        LParen, RParen, LBracket, RBracket, LBrace, RBrace,
        Comma, Colon, Dot,
    }

    /// <summary>插值字符串的一段: 字面文本(已解码转义) 或 表达式源码(交回 Lexer/Parser 子解析)。</summary>
    internal struct InterpPart
    {
        internal bool IsExpr;
        internal string Text;
    }

    internal sealed class Token
    {
        internal Tk Kind;
        internal string Text;            // Ident 名 / 普通字符串解码结果
        internal long IntVal;
        internal double FloatVal;
        internal int Line;
        internal int Col;
        internal List<InterpPart> Parts; // 插值字符串非 null(此时 Text 无效)

        internal Token(Tk kind, int line, int col) { Kind = kind; Line = line; Col = col; }

        public override string ToString() => $"{Kind}({Text ?? IntVal.ToString()})@{Line}:{Col}";
    }
}
