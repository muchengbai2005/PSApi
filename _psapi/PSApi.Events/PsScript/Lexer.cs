using System.Collections.Generic;
using System.Text;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// 词法分析: .pss 文本 → Token 流。
    /// 缩进块转 Indent/Dedent(参照 Python 方案): 4 空格或 1 tab 一级, 同一文件禁止混用;
    /// 括号 ([{ 内换行不产生 Newline(隐式续行); # 注释; 字符串支持 {expr} 插值({{/}} 转义花括号)。
    /// </summary>
    internal sealed class Lexer
    {
        private static readonly Dictionary<string, Tk> Keywords = new Dictionary<string, Tk>
        {
            ["var"] = Tk.Var, ["const"] = Tk.Const, ["func"] = Tk.Func,
            ["if"] = Tk.If, ["elif"] = Tk.Elif, ["else"] = Tk.Else,
            ["for"] = Tk.For, ["while"] = Tk.While, ["in"] = Tk.In,
            ["break"] = Tk.Break, ["continue"] = Tk.Continue,
            ["return"] = Tk.Return, ["pass"] = Tk.Pass,
            ["and"] = Tk.And, ["or"] = Tk.Or, ["not"] = Tk.Not,
            ["true"] = Tk.True, ["false"] = Tk.False, ["null"] = Tk.Null,
            ["on"] = Tk.On,
        };

        private readonly string _file;
        private readonly string _src;
        private readonly bool _exprMode;          // 插值表达式子解析: 无缩进逻辑, 到尾即 Eof
        private int _pos;
        private int _line = 1;
        private int _lineStart;                   // 当前行首在 _src 中的偏移(列计算用)
        private readonly List<Token> _tokens = new List<Token>();
        private readonly List<int> _indents = new List<int> { 0 };
        private int _indentMode;                  // 0=未定 1=空格 2=tab
        private int _bracketDepth;

        private Lexer(string file, string src, bool exprMode)
        {
            _file = file;
            _src = src;
            _exprMode = exprMode;
        }

        internal static List<Token> Lex(string file, string src) => new Lexer(file, src, false).Run();

        /// <summary>插值/子表达式词法: 不处理缩进与换行, 末尾直接 Eof。</summary>
        internal static List<Token> LexExpression(string file, int line, string exprSrc)
        {
            var lx = new Lexer(file, exprSrc, true) { _line = line };
            return lx.Run();
        }

        private int Col => _pos - _lineStart + 1;
        private bool AtEnd => _pos >= _src.Length;
        private char Peek => AtEnd ? '\0' : _src[_pos];
        private char PeekNext => _pos + 1 >= _src.Length ? '\0' : _src[_pos + 1];

        private PsCompileError Err(string msg) => new PsCompileError(_file, _line, Col, msg);

        private List<Token> Run()
        {
            bool lineStart = true;
            while (!AtEnd)
            {
                if (lineStart && !_exprMode)
                {
                    bool codeLine = HandleLineStart();
                    lineStart = false;
                    if (!codeLine) continue; // 空行/纯注释行: 跳过, 由行尾 \n 分支重置 lineStart
                }

                char c = Peek;
                switch (c)
                {
                    case '\r':
                    case ' ':
                    case '\t':
                        _pos++;
                        break;
                    case '\n':
                        _pos++;
                        _line++;
                        _lineStart = _pos;
                        if (_bracketDepth == 0)
                        {
                            // 括号外换行 = 语句结束; 行首无 token 的空行已被 HandleLineStart 跳过
                            if (_tokens.Count > 0 && _tokens[_tokens.Count - 1].Kind != Tk.Newline)
                                _tokens.Add(new Token(Tk.Newline, _line - 1, 1));
                            lineStart = true;
                        }
                        break;
                    case '#':
                        while (!AtEnd && Peek != '\n') _pos++;
                        break;
                    case '"' or '\'':
                        LexString(c);
                        break;
                    case >= '0' and <= '9':
                        LexNumber();
                        break;
                    default:
                        if (c == '_' || char.IsLetter(c)) LexIdent();
                        else LexOperator();
                        break;
                }
            }

            if (!_exprMode)
            {
                if (_tokens.Count > 0 && _tokens[_tokens.Count - 1].Kind != Tk.Newline)
                    _tokens.Add(new Token(Tk.Newline, _line, Col));
                while (_indents.Count > 1)
                {
                    _indents.RemoveAt(_indents.Count - 1);
                    _tokens.Add(new Token(Tk.Dedent, _line, Col));
                }
            }
            _tokens.Add(new Token(Tk.Eof, _line, Col));
            return _tokens;
        }

        /// <summary>行首缩进处理; 返回 true 表示该行是有效代码行(继续扫描 token), false 表示空行/纯注释行(已跳过)。</summary>
        private bool HandleLineStart()
        {
            int spaces = 0, tabs = 0;
            while (Peek == ' ' || Peek == '\t')
            {
                if (Peek == ' ') spaces++; else tabs++;
                _pos++;
            }

            // 空行/纯注释行: 跳过整行, 不参与缩进计算
            if (Peek == '\n' || Peek == '\r' || Peek == '#' || AtEnd)
            {
                while (!AtEnd && Peek != '\n') _pos++;
                return false;
            }

            if (spaces > 0 && tabs > 0)
                throw Err("缩进混用空格与制表符(须全文统一 4 空格或 1 tab)");

            int units;
            if (tabs > 0)
            {
                if (_indentMode == 1) throw Err("缩进混用: 本文件已使用 4 空格缩进, 此处出现 tab");
                _indentMode = 2;
                units = tabs;
            }
            else if (spaces > 0)
            {
                if (_indentMode == 2) throw Err("缩进混用: 本文件已使用 tab 缩进, 此处出现空格");
                _indentMode = 1;
                if (spaces % 4 != 0) throw Err($"空格缩进须为 4 的倍数(当前 {spaces} 个)");
                units = spaces / 4;
            }
            else
            {
                units = 0; // 顶格行不改变缩进模式
            }

            int top = _indents[_indents.Count - 1];
            if (units > top)
            {
                _indents.Add(units);
                _tokens.Add(new Token(Tk.Indent, _line, Col));
            }
            else if (units < top)
            {
                while (_indents.Count > 1 && _indents[_indents.Count - 1] > units)
                {
                    _indents.RemoveAt(_indents.Count - 1);
                    _tokens.Add(new Token(Tk.Dedent, _line, Col));
                }
                if (_indents[_indents.Count - 1] != units)
                    throw Err("缩进层级未与任何外层块对齐");
            }
            return true;
        }

        private void LexNumber()
        {
            int start = _pos;
            while (char.IsDigit(Peek)) _pos++;
            bool isFloat = false;
            if (Peek == '.' && char.IsDigit(PeekNext))
            {
                isFloat = true;
                _pos++;
                while (char.IsDigit(Peek)) _pos++;
            }
            string text = _src.Substring(start, _pos - start);
            if (isFloat)
            {
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d))
                    throw Err($"无效数字 '{text}'");
                _tokens.Add(new Token(Tk.Float, _line, start - _lineStart + 1) { FloatVal = d, Text = text });
            }
            else
            {
                if (!long.TryParse(text, out long l))
                    throw Err($"整数过大 '{text}'");
                _tokens.Add(new Token(Tk.Int, _line, start - _lineStart + 1) { IntVal = l, Text = text });
            }
        }

        private void LexIdent()
        {
            int start = _pos;
            while (Peek == '_' || char.IsLetterOrDigit(Peek)) _pos++;
            string text = _src.Substring(start, _pos - start);
            var kind = Keywords.TryGetValue(text, out var kw) ? kw : Tk.Ident;
            _tokens.Add(new Token(kind, _line, start - _lineStart + 1) { Text = text });
        }

        /// <summary>字符串字面量: 支持 \n \t \r \\ \" \' \{ \} 转义与 {expr} 插值({{ / }} 为字面花括号)。</summary>
        private void LexString(char quote)
        {
            int tokLine = _line, tokCol = Col;
            _pos++; // 开引号
            var literal = new StringBuilder();
            List<InterpPart> parts = null;

            void FlushLiteral()
            {
                if (literal.Length == 0 && parts == null) return;
                parts ??= new List<InterpPart>();
                parts.Add(new InterpPart { IsExpr = false, Text = literal.ToString() });
                literal.Clear();
            }

            while (true)
            {
                if (AtEnd || Peek == '\n') throw new PsCompileError(_file, tokLine, tokCol, "字符串未闭合(不支持跨行)");
                char c = Peek;
                if (c == quote)
                {
                    _pos++;
                    break;
                }
                if (c == '\\')
                {
                    _pos++;
                    if (AtEnd) throw Err("转义序列不完整");
                    literal.Append(Peek switch
                    {
                        'n' => '\n', 't' => '\t', 'r' => '\r',
                        '\\' => '\\', '"' => '"', '\'' => '\'',
                        '{' => '{', '}' => '}',
                        _ => Peek, // 未知转义原样保留
                    });
                    _pos++;
                    continue;
                }
                if (c == '{')
                {
                    if (PeekNext == '{') { literal.Append('{'); _pos += 2; continue; }
                    FlushLiteral();
                    parts ??= new List<InterpPart>();
                    parts.Add(new InterpPart { IsExpr = true, Text = ScanInterpExpr(quote) });
                    continue;
                }
                if (c == '}' && PeekNext == '}') { literal.Append('}'); _pos += 2; continue; }
                literal.Append(c);
                _pos++;
            }

            if (parts == null)
                _tokens.Add(new Token(Tk.Str, tokLine, tokCol) { Text = literal.ToString() });
            else
            {
                FlushLiteral();
                _tokens.Add(new Token(Tk.Str, tokLine, tokCol) { Parts = parts });
            }
        }

        /// <summary>扫描插值表达式源码(允许嵌套花括号与异类引号字符串), 返回表达式文本并把 _pos 移到闭花括号之后。</summary>
        private string ScanInterpExpr(char outerQuote)
        {
            _pos++; // '{'
            int start = _pos;
            int depth = 1;
            while (!AtEnd)
            {
                char c = Peek;
                if (c == '\n') throw Err("插值表达式未闭合(缺少 })");
                if (c == '"' || c == '\'')
                {
                    // 插值内引号须与外层字符串不同, 否则提前终止外层字符串(此处宽容跳过)
                    if (c == outerQuote) throw Err("插值表达式内的字符串引号须与外层不同(如外层 \" 内层 ')");
                    char q = c;
                    _pos++;
                    while (!AtEnd && Peek != q)
                    {
                        if (Peek == '\\') _pos++;
                        if (AtEnd || Peek == '\n') throw Err("插值表达式内字符串未闭合");
                        _pos++;
                    }
                    if (AtEnd) throw Err("插值表达式未闭合(缺少 })");
                    _pos++;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        string expr = _src.Substring(start, _pos - start).Trim();
                        _pos++;
                        if (expr.Length == 0) throw Err("插值表达式不能为空 {}");
                        return expr;
                    }
                }
                _pos++;
            }
            throw Err("插值表达式未闭合(缺少 })");
        }

        private void LexOperator()
        {
            int line = _line, col = Col;
            char c = _src[_pos];
            char n = PeekNext;
            Tk? two = (c, n) switch
            {
                ('=', '=') => Tk.EqEq, ('!', '=') => Tk.NotEq,
                ('<', '=') => Tk.LtEq, ('>', '=') => Tk.GtEq,
                ('+', '=') => Tk.PlusEq, ('-', '=') => Tk.MinusEq,
                ('*', '=') => Tk.StarEq, ('/', '=') => Tk.SlashEq,
                _ => null,
            };
            if (two.HasValue)
            {
                _pos += 2;
                _tokens.Add(new Token(two.Value, line, col));
                return;
            }
            Tk? one = c switch
            {
                '+' => Tk.Plus, '-' => Tk.Minus, '*' => Tk.Star, '/' => Tk.Slash, '%' => Tk.Percent,
                '<' => Tk.Lt, '>' => Tk.Gt, '=' => Tk.Assign,
                '(' => Tk.LParen, ')' => Tk.RParen,
                '[' => Tk.LBracket, ']' => Tk.RBracket,
                '{' => Tk.LBrace, '}' => Tk.RBrace,
                ',' => Tk.Comma, ':' => Tk.Colon, '.' => Tk.Dot,
                _ => null,
            };
            if (!one.HasValue) throw Err($"无法识别的字符 '{c}'");
            _pos++;
            if (c is '(' or '[' or '{') _bracketDepth++;
            if (c is ')' or ']' or '}')
            {
                if (_bracketDepth == 0) throw Err($"多余的 '{c}'(无匹配的开括号)");
                _bracketDepth--;
            }
            _tokens.Add(new Token(one.Value, line, col));
        }
    }
}
