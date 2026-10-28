using System.Globalization;
using System.Text;

namespace WorkspaceAgent.Sql;

public enum TokKind { Ident, QuotedIdent, Number, String, Symbol, End }

public readonly record struct Token(TokKind Kind, string Text, int Pos)
{
    public bool IsKeyword(string kw) => Kind == TokKind.Ident && string.Equals(Text, kw, StringComparison.OrdinalIgnoreCase);
    public bool IsSymbol(string s) => Kind == TokKind.Symbol && Text == s;
    public override string ToString() => Kind == TokKind.End ? "end of input" : $"'{Text}'";
}

/// <summary>
/// Tokenizer. Rejects comments, parameters, backticks and brackets outright: none are needed to answer
/// a question, and each is a common way to smuggle text past a validator.
/// </summary>
public static class SqlLexer
{
    public static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') throw new SqlParseException("comments are not allowed");
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') throw new SqlParseException("comments are not allowed");
            if (c is '?' or ':' or '@' or '$') throw new SqlParseException($"parameters are not allowed ('{c}' at {i})");
            if (c is '`' or '[' or ']' or '#') throw new SqlParseException($"unsupported character '{c}' at {i}");

            int start = i;
            if (char.IsLetter(c) || c == '_')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                tokens.Add(new Token(TokKind.Ident, sql[start..i], start));
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1])))
            {
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.')) i++;
                var text = sql[start..i];
                if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
                    throw new SqlParseException($"bad number '{text}'");
                tokens.Add(new Token(TokKind.Number, text, start));
            }
            else if (c == '\'' || c == '"')
            {
                char q = c; i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= sql.Length) throw new SqlParseException("unterminated quoted text");
                    if (sql[i] == q)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == q) { sb.Append(q); i += 2; continue; }
                        i++; break;
                    }
                    sb.Append(sql[i++]);
                }
                tokens.Add(new Token(q == '\'' ? TokKind.String : TokKind.QuotedIdent, sb.ToString(), start));
            }
            else
            {
                string two = i + 1 < sql.Length ? sql.Substring(i, 2) : "";
                if (two is "<=" or ">=" or "<>" or "!=" or "||" or "==")
                {
                    tokens.Add(new Token(TokKind.Symbol, two == "==" ? "=" : two, start)); i += 2;
                }
                else if ("(),.*+-/%=<>;".Contains(c))
                {
                    tokens.Add(new Token(TokKind.Symbol, c.ToString(), start)); i++;
                }
                else throw new SqlParseException($"unexpected character '{c}' at {i}");
            }
        }
        tokens.Add(new Token(TokKind.End, "", sql.Length));
        return tokens;
    }
}

/// <summary>Recursive-descent parser for a single SELECT statement (no CTEs, no set operations).</summary>
public sealed class SqlParser
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT","FROM","WHERE","GROUP","BY","HAVING","ORDER","LIMIT","OFFSET","JOIN","LEFT","RIGHT","FULL","INNER","OUTER",
        "CROSS","ON","AS","AND","OR","NOT","IN","IS","NULL","LIKE","BETWEEN","CASE","WHEN","THEN","ELSE","END","DISTINCT",
        "UNION","INTERSECT","EXCEPT","ASC","DESC","EXISTS","CAST","WITH","INSERT","UPDATE","DELETE","DROP","ALTER","CREATE",
        "ATTACH","DETACH","PRAGMA","REPLACE","VACUUM","GLOB","REGEXP","MATCH","ESCAPE","COLLATE","WINDOW","OVER","NATURAL","USING",
    };

    private readonly List<Token> _t;
    private int _p;

    private SqlParser(List<Token> tokens) => _t = tokens;

    public static SelectStmt Parse(string sql)
    {
        var tokens = SqlLexer.Tokenize(sql);
        var parser = new SqlParser(tokens);
        var first = tokens[0];
        if (first.Kind == TokKind.End) throw new SqlParseException("empty query");
        if (first.IsKeyword("WITH")) throw new SqlParseException("WITH (common table expressions) is not supported; use a subquery");
        if (!first.IsKeyword("SELECT")) throw new SqlParseException($"only SELECT statements are allowed (got {first.Text.ToUpperInvariant()})");

        var stmt = parser.ParseSelect();
        while (parser.Peek.IsSymbol(";")) parser._p++;
        if (parser.Peek.Kind != TokKind.End)
        {
            var t = parser.Peek;
            if (t.IsKeyword("UNION") || t.IsKeyword("INTERSECT") || t.IsKeyword("EXCEPT"))
                throw new SqlParseException($"{t.Text.ToUpperInvariant()} is not supported");
            throw new SqlParseException(parser._t[parser._p - 1].IsSymbol(";")
                ? "multiple statements are not allowed"
                : $"unexpected {t} at {t.Pos}");
        }
        return stmt;
    }

    private Token Peek => _t[_p];
    private Token PeekAt(int k) => _t[Math.Min(_p + k, _t.Count - 1)];
    private Token Next() => _t[_p++];

    private bool AcceptKw(string kw) { if (Peek.IsKeyword(kw)) { _p++; return true; } return false; }
    private bool AcceptSym(string s) { if (Peek.IsSymbol(s)) { _p++; return true; } return false; }
    private void ExpectKw(string kw) { if (!AcceptKw(kw)) throw Error($"expected {kw}"); }
    private void ExpectSym(string s) { if (!AcceptSym(s)) throw Error($"expected '{s}'"); }
    private SqlParseException Error(string msg) => new($"{msg} but found {Peek} at {Peek.Pos}");

    private string Identifier()
    {
        var t = Peek;
        if (t.Kind == TokKind.QuotedIdent || (t.Kind == TokKind.Ident && !Reserved.Contains(t.Text))) { _p++; return t.Text; }
        throw Error("expected identifier");
    }

    private bool PeekIsIdentifier => Peek.Kind == TokKind.QuotedIdent || (Peek.Kind == TokKind.Ident && !Reserved.Contains(Peek.Text));

    private SelectStmt ParseSelect()
    {
        ExpectKw("SELECT");
        bool distinct = AcceptKw("DISTINCT");
        if (!distinct) AcceptKw("ALL");

        var items = new List<SelectItem>();
        do items.Add(ParseSelectItem()); while (AcceptSym(","));

        TableSource? from = null;
        var joins = new List<Join>();
        if (AcceptKw("FROM"))
        {
            from = ParseTableSource();
            while (true)
            {
                if (AcceptSym(",")) { joins.Add(new Join("CROSS JOIN", ParseTableSource(), null)); continue; }
                string? kind = null;
                if (AcceptKw("JOIN")) kind = "JOIN";
                else if (Peek.IsKeyword("INNER") && PeekAt(1).IsKeyword("JOIN")) { _p += 2; kind = "INNER JOIN"; }
                else if (Peek.IsKeyword("LEFT"))
                {
                    _p++; AcceptKw("OUTER"); ExpectKw("JOIN"); kind = "LEFT JOIN";
                }
                else if (Peek.IsKeyword("CROSS") && PeekAt(1).IsKeyword("JOIN")) { _p += 2; joins.Add(new Join("CROSS JOIN", ParseTableSource(), null)); continue; }
                else if (Peek.IsKeyword("RIGHT") || Peek.IsKeyword("FULL") || Peek.IsKeyword("NATURAL"))
                    throw new SqlParseException($"{Peek.Text.ToUpperInvariant()} joins are not supported");
                if (kind is null) break;
                var src = ParseTableSource();
                if (Peek.IsKeyword("USING")) throw new SqlParseException("JOIN ... USING is not supported; use ON");
                ExpectKw("ON");
                joins.Add(new Join(kind, src, ParseExpr()));
            }
        }

        Expr? where = AcceptKw("WHERE") ? ParseExpr() : null;

        var groupBy = new List<Expr>();
        if (AcceptKw("GROUP")) { ExpectKw("BY"); do groupBy.Add(ParseExpr()); while (AcceptSym(",")); }
        Expr? having = AcceptKw("HAVING") ? ParseExpr() : null;

        var orderBy = new List<OrderItem>();
        if (AcceptKw("ORDER"))
        {
            ExpectKw("BY");
            do
            {
                var e = ParseExpr();
                bool desc = AcceptKw("DESC");
                if (!desc) AcceptKw("ASC");
                if (AcceptKw("NULLS")) { if (!AcceptKw("FIRST")) ExpectKw("LAST"); }
                orderBy.Add(new OrderItem(e, desc));
            } while (AcceptSym(","));
        }

        long? limit = null, offset = null;
        if (AcceptKw("LIMIT"))
        {
            limit = IntegerLiteral("LIMIT");
            if (AcceptKw("OFFSET")) offset = IntegerLiteral("OFFSET");
            else if (AcceptSym(",")) { offset = limit; limit = IntegerLiteral("LIMIT"); }
        }
        return new SelectStmt(distinct, items, from, joins, where, groupBy, having, orderBy, limit, offset);
    }

    private long IntegerLiteral(string clause)
    {
        var t = Peek;
        if (t.Kind == TokKind.Number && long.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) { _p++; return v; }
        throw Error($"{clause} must be a non-negative integer literal");
    }

    private SelectItem ParseSelectItem()
    {
        if (AcceptSym("*")) return new SelectItem(new StarExpr(null), null);
        if (PeekIsIdentifier && PeekAt(1).IsSymbol(".") && PeekAt(2).IsSymbol("*"))
        {
            var q = Identifier(); _p += 2;
            return new SelectItem(new StarExpr(q), null);
        }
        var e = ParseExpr();
        string? alias = null;
        if (AcceptKw("AS")) alias = Peek.Kind == TokKind.String ? Next().Text : Identifier();
        else if (PeekIsIdentifier) alias = Identifier();
        return new SelectItem(e, alias);
    }

    private TableSource ParseTableSource()
    {
        if (AcceptSym("("))
        {
            if (!Peek.IsKeyword("SELECT")) throw Error("expected a subquery");
            var q = ParseSelect();
            ExpectSym(")");
            AcceptKw("AS");
            if (!PeekIsIdentifier) throw Error("a subquery in FROM needs an alias");
            return new DerivedTable(q, Identifier());
        }
        var name = Identifier();
        if (Peek.IsSymbol(".")) throw new SqlParseException($"schema-qualified names are not allowed ('{name}.')");
        if (Peek.IsSymbol("(")) throw new SqlParseException($"table-valued functions are not allowed ('{name}(')");
        string? alias = null;
        if (AcceptKw("AS")) alias = Identifier();
        else if (PeekIsIdentifier) alias = Identifier();
        if (Peek.IsKeyword("INDEXED") || Peek.IsKeyword("NOT") && PeekAt(1).IsKeyword("INDEXED"))
            throw new SqlParseException("INDEXED BY is not allowed");
        return new TableRef(name, alias);
    }

    // Precedence: OR < AND < NOT < comparison/IS/IN/LIKE/BETWEEN < || + - < * / % < unary
    private Expr ParseExpr() => ParseOr();

    private Expr ParseOr()
    {
        var l = ParseAnd();
        while (AcceptKw("OR")) l = new BinaryExpr("OR", l, ParseAnd());
        return l;
    }

    private Expr ParseAnd()
    {
        var l = ParseNot();
        while (AcceptKw("AND")) l = new BinaryExpr("AND", l, ParseNot());
        return l;
    }

    private Expr ParseNot() => AcceptKw("NOT") ? new UnaryExpr("NOT", ParseNot()) : ParseComparison();

    private Expr ParseComparison()
    {
        var l = ParseAdditive();
        while (true)
        {
            var t = Peek;
            if (t.Kind == TokKind.Symbol && t.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
            {
                _p++; l = new BinaryExpr(t.Text == "!=" ? "<>" : t.Text, l, ParseAdditive()); continue;
            }
            if (t.IsKeyword("IS"))
            {
                _p++; bool neg = AcceptKw("NOT");
                if (AcceptKw("NULL")) { l = new IsNullExpr(l, neg); continue; }
                throw Error("only IS [NOT] NULL is supported");
            }
            bool negated = false;
            if (t.IsKeyword("NOT") && (PeekAt(1).IsKeyword("IN") || PeekAt(1).IsKeyword("LIKE") || PeekAt(1).IsKeyword("BETWEEN")))
            {
                _p++; negated = true; t = Peek;
            }
            if (t.IsKeyword("IN"))
            {
                _p++; ExpectSym("(");
                if (Peek.IsKeyword("SELECT"))
                {
                    var q = ParseSelect(); ExpectSym(")");
                    l = new InSubqueryExpr(l, negated, q); continue;
                }
                var items = new List<Expr>();
                if (!Peek.IsSymbol(")")) do items.Add(ParseExpr()); while (AcceptSym(","));
                ExpectSym(")");
                l = new InListExpr(l, negated, items); continue;
            }
            if (t.IsKeyword("LIKE"))
            {
                _p++; var pat = ParseAdditive();
                if (Peek.IsKeyword("ESCAPE")) throw new SqlParseException("LIKE ... ESCAPE is not supported");
                l = new LikeExpr(l, negated, pat); continue;
            }
            if (t.IsKeyword("BETWEEN"))
            {
                _p++; var lo = ParseAdditive(); ExpectKw("AND"); var hi = ParseAdditive();
                l = new BetweenExpr(l, negated, lo, hi); continue;
            }
            if (t.IsKeyword("GLOB") || t.IsKeyword("REGEXP") || t.IsKeyword("MATCH") || t.IsKeyword("COLLATE"))
                throw new SqlParseException($"{t.Text.ToUpperInvariant()} is not supported");
            if (negated) throw Error("expected IN, LIKE or BETWEEN after NOT");
            return l;
        }
    }

    private Expr ParseAdditive()
    {
        var l = ParseMultiplicative();
        while (Peek.Kind == TokKind.Symbol && Peek.Text is "+" or "-" or "||")
            l = new BinaryExpr(Next().Text, l, ParseMultiplicative());
        return l;
    }

    private Expr ParseMultiplicative()
    {
        var l = ParseUnary();
        while (Peek.Kind == TokKind.Symbol && Peek.Text is "*" or "/" or "%")
            l = new BinaryExpr(Next().Text, l, ParseUnary());
        return l;
    }

    private Expr ParseUnary()
    {
        if (AcceptSym("-")) return new UnaryExpr("-", ParseUnary());
        if (AcceptSym("+")) return ParseUnary();
        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        var t = Peek;
        switch (t.Kind)
        {
            case TokKind.Number: _p++; return new NumberLit(t.Text);
            case TokKind.String: _p++; return new StringLit(t.Text);
            case TokKind.Symbol when t.Text == "(":
                _p++;
                if (Peek.IsKeyword("SELECT")) { var q = ParseSelect(); ExpectSym(")"); return new ScalarSubquery(q); }
                var inner = ParseExpr(); ExpectSym(")");
                return inner;
        }
        if (AcceptKw("NULL")) return new NullLit();
        if (t.IsKeyword("TRUE") || t.IsKeyword("FALSE")) { _p++; return new NumberLit(t.IsKeyword("TRUE") ? "1" : "0"); }
        if (AcceptKw("EXISTS")) { ExpectSym("("); var q = ParseSelect(); ExpectSym(")"); return new ExistsExpr(q); }
        if (t.IsKeyword("NOT") && PeekAt(1).IsKeyword("EXISTS")) { _p++; return new UnaryExpr("NOT", ParsePrimary()); }
        if (AcceptKw("CASE")) return ParseCase();
        if (AcceptKw("CAST"))
        {
            ExpectSym("("); var v = ParseExpr(); ExpectKw("AS");
            var type = Identifier().ToUpperInvariant();
            if (type is not ("INTEGER" or "INT" or "REAL" or "TEXT" or "NUMERIC" or "FLOAT"))
                throw new SqlParseException($"CAST to {type} is not supported");
            ExpectSym(")");
            return new CastExpr(v, type);
        }
        if (t.Kind == TokKind.Ident && Reserved.Contains(t.Text) && !PeekAt(1).IsSymbol("("))
            throw Error("unexpected keyword");

        if (t.Kind is TokKind.Ident or TokKind.QuotedIdent)
        {
            if (t.Kind == TokKind.Ident && PeekAt(1).IsSymbol("("))
            {
                _p += 2;
                var name = t.Text.ToLowerInvariant();
                if (AcceptSym("*")) { ExpectSym(")"); return new FuncCall(name, false, [], true); }
                bool distinct = AcceptKw("DISTINCT");
                var args = new List<Expr>();
                if (!Peek.IsSymbol(")")) do args.Add(ParseExpr()); while (AcceptSym(","));
                ExpectSym(")");
                if (Peek.IsKeyword("OVER") || Peek.IsKeyword("FILTER")) throw new SqlParseException("window functions and FILTER are not supported");
                return new FuncCall(name, distinct, args, false);
            }
            var first = Identifier();
            if (AcceptSym("."))
            {
                if (Peek.IsSymbol("*")) throw Error("qualified * is only allowed in the select list");
                var col = Identifier();
                if (Peek.IsSymbol(".")) throw new SqlParseException("schema-qualified names are not allowed");
                return new ColumnRef(first, col);
            }
            return new ColumnRef(null, first);
        }
        throw Error("expected an expression");
    }

    private Expr ParseCase()
    {
        Expr? operand = Peek.IsKeyword("WHEN") ? null : ParseExpr();
        var branches = new List<(Expr, Expr)>();
        while (AcceptKw("WHEN"))
        {
            var w = ParseExpr(); ExpectKw("THEN");
            branches.Add((w, ParseExpr()));
        }
        if (branches.Count == 0) throw Error("CASE needs at least one WHEN");
        Expr? els = AcceptKw("ELSE") ? ParseExpr() : null;
        ExpectKw("END");
        return new CaseExpr(operand, branches, els);
    }
}
