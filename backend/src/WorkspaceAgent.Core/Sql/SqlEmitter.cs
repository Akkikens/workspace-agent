using System.Text;

namespace WorkspaceAgent.Sql;

/// <summary>
/// Renders the AST back to SQLite SQL. Identifiers are always double-quoted and every compound
/// expression is parenthesized, so the output never depends on the model's spelling or precedence.
/// </summary>
public static class SqlEmitter
{
    public static string Emit(SelectStmt s)
    {
        var sb = new StringBuilder();
        Select(sb, s);
        return sb.ToString();
    }

    public static string Quote(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    private static void Select(StringBuilder sb, SelectStmt s)
    {
        sb.Append("SELECT ");
        if (s.Distinct) sb.Append("DISTINCT ");
        for (int i = 0; i < s.Items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Expr(sb, s.Items[i].Expr);
            if (s.Items[i].Alias is { } a) sb.Append(" AS ").Append(Quote(a));
        }
        if (s.From is not null)
        {
            sb.Append(" FROM ");
            Source(sb, s.From);
            foreach (var j in s.Joins)
            {
                sb.Append(' ').Append(j.Kind).Append(' ');
                Source(sb, j.Source);
                if (j.On is not null) { sb.Append(" ON "); Expr(sb, j.On); }
            }
        }
        if (s.Where is not null) { sb.Append(" WHERE "); Expr(sb, s.Where); }
        if (s.GroupBy.Count > 0) { sb.Append(" GROUP BY "); List(sb, s.GroupBy); }
        if (s.Having is not null) { sb.Append(" HAVING "); Expr(sb, s.Having); }
        if (s.OrderBy.Count > 0)
        {
            sb.Append(" ORDER BY ");
            for (int i = 0; i < s.OrderBy.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Expr(sb, s.OrderBy[i].Expr);
                if (s.OrderBy[i].Descending) sb.Append(" DESC");
            }
        }
        if (s.Limit is { } l) sb.Append(" LIMIT ").Append(l);
        if (s.Offset is { } o) sb.Append(" OFFSET ").Append(o);
    }

    private static void Source(StringBuilder sb, TableSource src)
    {
        switch (src)
        {
            case TableRef t:
                sb.Append(Quote(t.Name));
                if (t.Alias is not null) sb.Append(" AS ").Append(Quote(t.Alias));
                break;
            case DerivedTable d:
                sb.Append('('); Select(sb, d.Query); sb.Append(") AS ").Append(Quote(d.Alias));
                break;
        }
    }

    private static void List(StringBuilder sb, IReadOnlyList<Expr> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            Expr(sb, items[i]);
        }
    }

    private static void Expr(StringBuilder sb, Expr e)
    {
        switch (e)
        {
            case NumberLit n: sb.Append(n.Text); break;
            case StringLit s: sb.Append('\'').Append(s.Value.Replace("'", "''")).Append('\''); break;
            case NullLit: sb.Append("NULL"); break;
            case ColumnRef c:
                if (c.Qualifier is not null) sb.Append(Quote(c.Qualifier)).Append('.');
                sb.Append(Quote(c.Name));
                break;
            case StarExpr st:
                if (st.Qualifier is not null) sb.Append(Quote(st.Qualifier)).Append('.');
                sb.Append('*');
                break;
            case FuncCall f:
                sb.Append(f.Name).Append('(');
                if (f.StarArg) sb.Append('*');
                else { if (f.Distinct) sb.Append("DISTINCT "); List(sb, f.Args); }
                sb.Append(')');
                break;
            case UnaryExpr u:
                sb.Append('(').Append(u.Op).Append(u.Op == "NOT" ? " " : "");
                Expr(sb, u.Operand); sb.Append(')');
                break;
            case BinaryExpr b:
                sb.Append('('); Expr(sb, b.Left); sb.Append(' ').Append(b.Op).Append(' '); Expr(sb, b.Right); sb.Append(')');
                break;
            case InListExpr il:
                sb.Append('('); Expr(sb, il.Value); sb.Append(il.Negated ? " NOT IN (" : " IN (");
                List(sb, il.Items); sb.Append("))");
                break;
            case InSubqueryExpr iq:
                sb.Append('('); Expr(sb, iq.Value); sb.Append(iq.Negated ? " NOT IN (" : " IN (");
                Select(sb, iq.Query); sb.Append("))");
                break;
            case BetweenExpr bt:
                sb.Append('('); Expr(sb, bt.Value); sb.Append(bt.Negated ? " NOT BETWEEN " : " BETWEEN ");
                Expr(sb, bt.Low); sb.Append(" AND "); Expr(sb, bt.High); sb.Append(')');
                break;
            case IsNullExpr isn:
                sb.Append('('); Expr(sb, isn.Value); sb.Append(isn.Negated ? " IS NOT NULL)" : " IS NULL)");
                break;
            case LikeExpr lk:
                sb.Append('('); Expr(sb, lk.Value); sb.Append(lk.Negated ? " NOT LIKE " : " LIKE ");
                Expr(sb, lk.Pattern); sb.Append(')');
                break;
            case ExistsExpr ex:
                sb.Append("EXISTS ("); Select(sb, ex.Query); sb.Append(')');
                break;
            case ScalarSubquery sq:
                sb.Append('('); Select(sb, sq.Query); sb.Append(')');
                break;
            case CaseExpr c:
                sb.Append("CASE");
                if (c.Operand is not null) { sb.Append(' '); Expr(sb, c.Operand); }
                foreach (var (w, t) in c.Branches)
                {
                    sb.Append(" WHEN "); Expr(sb, w); sb.Append(" THEN "); Expr(sb, t);
                }
                if (c.Else is not null) { sb.Append(" ELSE "); Expr(sb, c.Else); }
                sb.Append(" END");
                break;
            case CastExpr ce:
                sb.Append("CAST("); Expr(sb, ce.Value); sb.Append(" AS ").Append(ce.TypeName).Append(')');
                break;
            case TrustedSql ts:
                sb.Append('(').Append(ts.Sql).Append(')');
                break;
            default:
                throw new InvalidOperationException($"cannot emit {e.GetType().Name}");
        }
    }
}
