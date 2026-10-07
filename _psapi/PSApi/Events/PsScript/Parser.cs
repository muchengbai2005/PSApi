using System.Collections.Generic;

namespace PSApi.Events.PsScript
{
    /// <summary>
    /// 递归下降 Parser: Token 流 → AST。
    /// 语句: var/const/赋值/表达式/if/while/for/break/continue/return/pass/func/顶层 on 块;
    /// 表达式优先级: or → and → not(一元) → 比较(含 in, 非结合) → + - → * / % → 一元 - → 后缀 → 原子。
    /// 插值字符串在此展开为 "+" 拼接(表达式段递归子解析)。
    /// </summary>
    internal sealed class Parser
    {
        private readonly string _file;
        private readonly List<Token> _tokens;
        private int _pos;

        private Parser(string file, List<Token> tokens) { _file = file; _tokens = tokens; }

        internal static PsProgram Parse(string file, List<Token> tokens)
        {
            var p = new Parser(file, tokens);
            var prog = new PsProgram();
            p.SkipNewlines();
            while (!p.Check(Tk.Eof))
            {
                var stmt = p.ParseStatement(topLevel: true);
                if (stmt is OnStmt on) prog.Handlers.Add(on);
                else prog.TopLevel.Add(stmt);
                p.SkipNewlines();
            }
            return prog;
        }

        /// <summary>插值表达式子解析: 源码 → 单个表达式(独立 Lexer 趟, 无缩进)。</summary>
        private Expr ParseInterpExpr(string exprSrc, int line, int col)
        {
            List<Token> toks;
            try { toks = Lexer.LexExpression(_file, line, exprSrc); }
            catch (PsCompileError lexErr) { throw new PsCompileError(_file, line, col, "插值表达式词法错误: " + lexErr.Message); }
            var sub = new Parser(_file, toks);
            Expr e;
            try
            {
                e = sub.ParseExpr();
                if (!sub.Check(Tk.Eof))
                    throw sub.Here("插值表达式多余内容(应为单个表达式)");
            }
            catch (PsCompileError ce) { throw new PsCompileError(_file, line, col, "插值表达式语法错误: " + ce.Message); }
            return e;
        }

        private Token Cur => _tokens[_pos];
        private Token Prev => _tokens[_pos - 1];
        private bool Check(Tk k) => Cur.Kind == k;
        private PsCompileError Here(string msg) => new PsCompileError(_file, Cur.Line, Cur.Col, msg);

        private bool Accept(Tk k)
        {
            if (Cur.Kind != k) return false;
            _pos++;
            return true;
        }

        private Token Expect(Tk k, string what)
        {
            if (Cur.Kind != k) throw Here($"应为 {what}, 实为 {Describe(Cur)}");
            return _tokens[_pos++];
        }

        private static string Describe(Token t) => t.Kind switch
        {
            Tk.Eof => "文件末尾",
            Tk.Newline => "行尾",
            Tk.Ident => $"标识符 '{t.Text}'",
            Tk.Int or Tk.Float or Tk.Str => $"字面量 {t.Text}",
            _ => $"'{t.Text ?? t.Kind.ToString()}'",
        };

        private void SkipNewlines() { while (Check(Tk.Newline)) _pos++; }

        // ---- 语句 ----

        private Stmt ParseStatement(bool topLevel)
        {
            switch (Cur.Kind)
            {
                case Tk.Var: return ParseVarDecl(false);
                case Tk.Const: return ParseVarDecl(true);
                case Tk.If: return ParseIf();
                case Tk.While: return ParseWhile();
                case Tk.For: return ParseFor();
                case Tk.Func: return ParseFuncDef();
                case Tk.On:
                    if (!topLevel) throw Here("'on' 块只能出现在文件顶层");
                    return ParseOn();
                case Tk.Break:
                    _pos++;
                    ExpectStmtEnd();
                    return new BreakStmt { Line = Prev.Line };
                case Tk.Continue:
                    _pos++;
                    ExpectStmtEnd();
                    return new ContinueStmt { Line = Prev.Line };
                case Tk.Pass:
                    _pos++;
                    ExpectStmtEnd();
                    return new PassStmt { Line = Prev.Line };
                case Tk.Return:
                {
                    var kw = Expect(Tk.Return, "return");
                    Expr value = Check(Tk.Newline) || Check(Tk.Eof) || Check(Tk.Dedent) ? null : ParseExpr();
                    ExpectStmtEnd();
                    return new ReturnStmt { Line = kw.Line, Value = value };
                }
                default:
                    return ParseExprOrAssign();
            }
        }

        private void ExpectStmtEnd()
        {
            if (!Check(Tk.Newline) && !Check(Tk.Eof) && !Check(Tk.Dedent))
                throw Here("语句后应为行尾(一行一条语句)");
            Accept(Tk.Newline);
        }

        private VarDeclStmt ParseVarDecl(bool isConst)
        {
            var kw = _tokens[_pos++];
            var name = Expect(Tk.Ident, "变量名");
            Expect(Tk.Assign, isConst ? "'='(const 必须有初始值)" : "'='");
            var init = ParseExpr();
            ExpectStmtEnd();
            return new VarDeclStmt { Line = kw.Line, Name = name.Text, Init = init, IsConst = isConst };
        }

        private Stmt ParseExprOrAssign()
        {
            var expr = ParseExpr();
            if (Cur.Kind is Tk.Assign or Tk.PlusEq or Tk.MinusEq or Tk.StarEq or Tk.SlashEq)
            {
                if (expr is not (IdentExpr or IndexExpr or MemberExpr))
                    throw new PsCompileError(_file, expr.Line, 0, "赋值目标必须是变量/索引/成员");
                var op = _tokens[_pos++];
                var value = ParseExpr();
                ExpectStmtEnd();
                return new AssignStmt { Line = op.Line, Target = expr, Op = op.Kind, Value = value };
            }
            ExpectStmtEnd();
            return new ExprStmt(expr) { Line = expr.Line };
        }

        private IfStmt ParseIf()
        {
            var kw = Expect(Tk.If, "if");
            var stmt = new IfStmt { Line = kw.Line };
            stmt.Branches.Add(new IfBranch { Cond = ParseExpr(), Body = ParseBlock() });
            while (Accept(Tk.Elif))
                stmt.Branches.Add(new IfBranch { Cond = ParseExpr(), Body = ParseBlock() });
            if (Accept(Tk.Else))
                stmt.Branches.Add(new IfBranch { Cond = null, Body = ParseBlock() });
            return stmt;
        }

        private WhileStmt ParseWhile()
        {
            var kw = Expect(Tk.While, "while");
            var cond = ParseExpr();
            return new WhileStmt { Line = kw.Line, Cond = cond, Body = ParseBlock() };
        }

        private ForStmt ParseFor()
        {
            var kw = Expect(Tk.For, "for");
            var name = Expect(Tk.Ident, "循环变量名");
            Expect(Tk.In, "'in'");
            var iterable = ParseExpr();
            return new ForStmt { Line = kw.Line, VarName = name.Text, Iterable = iterable, Body = ParseBlock() };
        }

        private FuncDefStmt ParseFuncDef()
        {
            var kw = Expect(Tk.Func, "func");
            var name = Expect(Tk.Ident, "函数名");
            var def = new FuncDefStmt { Line = kw.Line, Name = name.Text };
            Expect(Tk.LParen, "'('");
            if (!Check(Tk.RParen))
            {
                while (true)
                {
                    var p = Expect(Tk.Ident, "参数名");
                    var param = new FuncParam { Name = p.Text };
                    if (Accept(Tk.Assign)) param.Default = ParseExpr();
                    else if (def.Params.Count > 0 && def.Params[def.Params.Count - 1].Default != null)
                        throw new PsCompileError(_file, p.Line, p.Col, "带默认值参数之后的参数也必须有默认值");
                    def.Params.Add(param);
                    if (!Accept(Tk.Comma)) break;
                }
            }
            Expect(Tk.RParen, "')'");
            def.Body = ParseBlock();
            return def;
        }

        private OnStmt ParseOn()
        {
            var kw = Expect(Tk.On, "on");
            string name;
            bool verbatim;
            if (Check(Tk.Str))
            {
                // 字符串形式(自定义事件, 如 on "gunworks:deal_done":) — 原样订阅, 不过别名表
                if (Cur.Parts != null) throw Here("事件名不支持插值");
                name = Cur.Text;
                verbatim = true;
                if (name.Length == 0) throw Here("事件名不能为空字符串");
                _pos++;
            }
            else
            {
                name = Expect(Tk.Ident, "事件名(下划线写法如 scene_loaded, 或字符串形式如 \"pack:event\")").Text;
                verbatim = false;
            }
            if (verbatim)
            {
                // 字符串形式括号可选: on "pack:event": 或 on "pack:event"():
                if (Accept(Tk.LParen)) Expect(Tk.RParen, "')'(on 块不带参数, 事件数据经 event 变量注入)");
            }
            else
            {
                Expect(Tk.LParen, "'('");
                Expect(Tk.RParen, "')'(on 块不带参数, 事件数据经 event 变量注入)");
            }
            return new OnStmt { Line = kw.Line, EventName = name, Verbatim = verbatim, Body = ParseBlock() };
        }

        /// <summary>语句块: 冒号 + 缩进块(常规) 或冒号后同行单条简单语句(便捷写法)。</summary>
        private List<Stmt> ParseBlock()
        {
            Expect(Tk.Colon, "':'");
            if (Accept(Tk.Newline))
            {
                Expect(Tk.Indent, "缩进的语句块(冒号后下一行须增加缩进)");
                var stmts = new List<Stmt>();
                SkipNewlines();
                while (!Check(Tk.Dedent) && !Check(Tk.Eof))
                {
                    stmts.Add(ParseStatement(topLevel: false));
                    SkipNewlines();
                }
                Expect(Tk.Dedent, "语句块结束(缩进回退)");
                if (stmts.Count == 0) throw Here("空语句块(至少一条语句, 或用 pass 占位)");
                return stmts;
            }
            // 同行单行块: if x: return 1
            if (Cur.Kind is Tk.If or Tk.While or Tk.For or Tk.Func or Tk.On)
                throw Here("复合语句不能写在冒号后同一行(请换行缩进)");
            return new List<Stmt> { ParseStatement(topLevel: false) };
        }

        // ---- 表达式(优先级从低到高) ----

        private Expr ParseExpr() => ParseOr();

        private Expr ParseOr()
        {
            var e = ParseAnd();
            while (Check(Tk.Or))
            {
                var op = _tokens[_pos++];
                e = new BinaryExpr(Tk.Or, e, ParseAnd()) { Line = op.Line };
            }
            return e;
        }

        private Expr ParseAnd()
        {
            var e = ParseNot();
            while (Check(Tk.And))
            {
                var op = _tokens[_pos++];
                e = new BinaryExpr(Tk.And, e, ParseNot()) { Line = op.Line };
            }
            return e;
        }

        private Expr ParseNot()
        {
            if (Check(Tk.Not))
            {
                var op = _tokens[_pos++];
                return new UnaryExpr(Tk.Not, ParseNot()) { Line = op.Line };
            }
            return ParseComparison();
        }

        private static readonly HashSet<Tk> CompareOps = new HashSet<Tk>
            { Tk.EqEq, Tk.NotEq, Tk.Lt, Tk.LtEq, Tk.Gt, Tk.GtEq, Tk.In };

        private Expr ParseComparison()
        {
            var e = ParseArith();
            if (CompareOps.Contains(Cur.Kind)) // 非结合: 最多一个比较运算符
            {
                var op = _tokens[_pos++];
                e = new BinaryExpr(op.Kind, e, ParseArith()) { Line = op.Line };
            }
            return e;
        }

        private Expr ParseArith()
        {
            var e = ParseTerm();
            while (Cur.Kind is Tk.Plus or Tk.Minus)
            {
                var op = _tokens[_pos++];
                e = new BinaryExpr(op.Kind, e, ParseTerm()) { Line = op.Line };
            }
            return e;
        }

        private Expr ParseTerm()
        {
            var e = ParseUnary();
            while (Cur.Kind is Tk.Star or Tk.Slash or Tk.Percent)
            {
                var op = _tokens[_pos++];
                e = new BinaryExpr(op.Kind, e, ParseUnary()) { Line = op.Line };
            }
            return e;
        }

        private Expr ParseUnary()
        {
            if (Check(Tk.Minus))
            {
                var op = _tokens[_pos++];
                return new UnaryExpr(Tk.Minus, ParseUnary()) { Line = op.Line };
            }
            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            var e = ParseAtom();
            while (true)
            {
                if (Accept(Tk.LParen))
                {
                    var args = new List<Expr>();
                    if (!Check(Tk.RParen))
                    {
                        while (true)
                        {
                            args.Add(ParseExpr());
                            if (!Accept(Tk.Comma)) break;
                        }
                    }
                    var rp = Expect(Tk.RParen, "')'");
                    e = new CallExpr(e, args) { Line = rp.Line };
                }
                else if (Accept(Tk.LBracket))
                {
                    var idx = ParseExpr();
                    var rb = Expect(Tk.RBracket, "']'");
                    e = new IndexExpr(e, idx) { Line = rb.Line };
                }
                else if (Accept(Tk.Dot))
                {
                    var name = Expect(Tk.Ident, "成员名");
                    e = new MemberExpr(e, name.Text) { Line = name.Line };
                }
                else break;
            }
            return e;
        }

        private Expr ParseAtom()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case Tk.Int: _pos++; return new LiteralExpr(t.IntVal) { Line = t.Line };
                case Tk.Float: _pos++; return new LiteralExpr(t.FloatVal) { Line = t.Line };
                case Tk.True: _pos++; return new LiteralExpr(true) { Line = t.Line };
                case Tk.False: _pos++; return new LiteralExpr(false) { Line = t.Line };
                case Tk.Null: _pos++; return new LiteralExpr(null) { Line = t.Line };
                case Tk.Ident: _pos++; return new IdentExpr(t.Text) { Line = t.Line };
                case Tk.Str:
                    _pos++;
                    return BuildString(t);
                case Tk.LParen:
                {
                    _pos++;
                    var e = ParseExpr();
                    Expect(Tk.RParen, "')'");
                    return e;
                }
                case Tk.LBracket:
                {
                    _pos++;
                    var arr = new ArrayExpr(new List<Expr>()) { Line = t.Line };
                    if (!Check(Tk.RBracket))
                    {
                        while (true)
                        {
                            arr.Items.Add(ParseExpr());
                            if (!Accept(Tk.Comma)) break;
                            if (Check(Tk.RBracket)) break; // 允许尾逗号
                        }
                    }
                    Expect(Tk.RBracket, "']'");
                    return arr;
                }
                case Tk.LBrace:
                {
                    _pos++;
                    var dict = new DictExpr { Line = t.Line };
                    if (!Check(Tk.RBrace))
                    {
                        while (true)
                        {
                            string key;
                            if (Check(Tk.Str) && Cur.Parts == null) { key = Cur.Text; _pos++; }
                            else if (Check(Tk.Ident)) { key = Cur.Text; _pos++; }
                            else throw Here("字典键须为字符串字面量或标识符");
                            if (!Accept(Tk.Colon) && !Accept(Tk.Assign))
                                throw Here("字典键后应为 ':' 或 '='");
                            dict.Keys.Add(key);
                            dict.Values.Add(ParseExpr());
                            if (!Accept(Tk.Comma)) break;
                            if (Check(Tk.RBrace)) break; // 允许尾逗号
                        }
                    }
                    Expect(Tk.RBrace, "'}'");
                    return dict;
                }
                default:
                    throw Here($"表达式须以字面量/标识符/'('/'['/'{{' 开头, 实为 {Describe(t)}");
            }
        }

        /// <summary>字符串 token → 表达式: 无插值直接字面量; 有插值展开为 "段" + (expr) + "段" 拼接。</summary>
        private Expr BuildString(Token t)
        {
            if (t.Parts == null)
                return new LiteralExpr(t.Text) { Line = t.Line };
            Expr result = null;
            foreach (var part in t.Parts)
            {
                Expr seg = part.IsExpr
                    ? ParseInterpExpr(part.Text, t.Line, t.Col)
                    : new LiteralExpr(part.Text) { Line = t.Line };
                result = result == null ? seg : new BinaryExpr(Tk.Plus, result, seg) { Line = t.Line };
            }
            return result ?? new LiteralExpr("") { Line = t.Line };
        }
    }
}
