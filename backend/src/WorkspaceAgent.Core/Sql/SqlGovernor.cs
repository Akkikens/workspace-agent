using WorkspaceAgent.Identity;

namespace WorkspaceAgent.Sql;

public sealed record GovernedQuery(
    string ProposedSql,
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> AppliedFilters,
    long Limit,
    bool LimitClamped,
    IReadOnlySet<(string Table, string Column)> ReadGrants);

public sealed record GovernanceDecision(GovernedQuery? Query, IReadOnlyList<string> Reasons)
{
    public bool Allowed => Query is not null;
}

/// <summary>
/// Turns model-proposed SQL into SQL that is safe to run for one user:
/// 1. parse (single SELECT only; the parser has no representation for anything else),
/// 2. validate every table, column and function against the semantic layer for the user's role,
/// 3. rewrite every base-table reference into a subquery that projects only granted columns and
///    applies the role's row filter with a bound parameter,
/// 4. enforce the row cap on the outer query.
/// Execution then adds a SQLite authorizer as a second, independent check (see <see cref="Warehouse"/>).
/// </summary>
public sealed class SqlGovernor(SemanticLayer layer)
{
    public SemanticLayer Layer => layer;

    public GovernanceDecision Govern(string proposedSql, UserContext user)
    {
        SelectStmt ast;
        try { ast = SqlParser.Parse(proposedSql); }
        catch (SqlParseException e) { return new(null, [e.Message]); }

        var ctx = new Ctx(user);
        var (rewritten, _) = RewriteSelect(ast, null, ctx);
        if (ctx.Reasons.Count > 0) return new(null, ctx.Reasons.Distinct().ToList());

        long max = layer.MaxRows;
        bool clamped = rewritten.Limit is { } l && l > max;
        long limit = rewritten.Limit is { } l2 && l2 <= max ? l2 : max;
        rewritten = rewritten with { Limit = limit };

        var parameters = new Dictionary<string, object?>();
        if (layer.IsRowScoped(user)) parameters["@bu"] = user.BusinessUnit;

        return new(new GovernedQuery(
            proposedSql, SqlEmitter.Emit(rewritten), parameters, ctx.AppliedFilters.Distinct().ToList(),
            limit, clamped, layer.ReadGrants(user)), []);
    }

    private sealed class Ctx(UserContext user)
    {
        public UserContext User { get; } = user;
        public List<string> Reasons { get; } = [];
        public List<string> AppliedFilters { get; } = [];
    }

    /// <summary>A FROM-clause entry visible to column references: a base table or a derived table.</summary>
    private sealed record ScopeTable(TableDef? Base, HashSet<string>? DerivedColumns);

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;
        public Dictionary<string, ScopeTable> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SelectAliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private (SelectStmt, HashSet<string>?) RewriteSelect(SelectStmt s, Scope? parent, Ctx ctx)
    {
        var scope = new Scope(parent);
        var from = s.From is null ? null : RewriteSource(s.From, scope, ctx);
        var joins = s.Joins.Select(j => j with { Source = RewriteSource(j.Source, scope, ctx) }).ToList();

        foreach (var item in s.Items) if (item.Alias is not null) scope.SelectAliases.Add(item.Alias);

        var items = s.Items.Select(i => i with { Expr = RewriteExpr(i.Expr, scope, ctx, selectList: true) }).ToList();
        joins = joins.Select(j => j.On is null ? j : j with { On = RewriteExpr(j.On, scope, ctx) }).ToList();
        var where = s.Where is null ? null : RewriteExpr(s.Where, scope, ctx);
        var groupBy = s.GroupBy.Select(g => RewriteExpr(g, scope, ctx)).ToList();
        var having = s.Having is null ? null : RewriteExpr(s.Having, scope, ctx);
        var orderBy = s.OrderBy.Select(o => o with { Expr = RewriteExpr(o.Expr, scope, ctx) }).ToList();

        // Output column names, so an outer query over this one can be checked. Unknown => permissive.
        HashSet<string>? outputs = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in s.Items)
        {
            if (item.Alias is not null) outputs.Add(item.Alias);
            else if (item.Expr is ColumnRef c) outputs.Add(c.Name);
            else { outputs = null; break; }
        }

        return (s with { From = from, Joins = joins, Items = items, Where = where, GroupBy = groupBy, Having = having, OrderBy = orderBy }, outputs);
    }

    private TableSource RewriteSource(TableSource src, Scope scope, Ctx ctx)
    {
        switch (src)
        {
            case DerivedTable d:
            {
                var (q, outputs) = RewriteSelect(d.Query, scope.Parent, ctx);
                Register(scope, d.Alias, new ScopeTable(null, outputs), ctx);
                return d with { Query = q };
            }
            case TableRef t:
            {
                var alias = t.Alias ?? t.Name;
                var def = layer.Table(t.Name);
                if (def is null)
                {
                    ctx.Reasons.Add($"table '{t.Name}' is not in the semantic layer");
                    return t;
                }
                if (!def.Roles.Contains(ctx.User.Role))
                {
                    ctx.Reasons.Add($"table '{def.Name}' is not available to role {ctx.User.RoleName}");
                    return t;
                }
                Register(scope, alias, new ScopeTable(def, null), ctx);

                Expr? filter = null;
                if (layer.IsRowScoped(ctx.User) && def.BusinessUnitFilter is { } f)
                {
                    filter = new TrustedSql(f);
                    ctx.AppliedFilters.Add($"{def.Name}: {f.Replace("@bu", $"'{ctx.User.BusinessUnit}'")}");
                }
                var projection = def.GrantedColumns(ctx.User.Role)
                    .Select(c => new SelectItem(new ColumnRef(null, c.Name), null)).ToList();
                var inner = new SelectStmt(false, projection, new TableRef(def.Name, null), [], filter, [], null, [], null, null);
                return new DerivedTable(inner, alias);
            }
            default:
                throw new InvalidOperationException();
        }
    }

    private static void Register(Scope scope, string alias, ScopeTable table, Ctx ctx)
    {
        if (!scope.Tables.TryAdd(alias, table))
            ctx.Reasons.Add($"duplicate table alias '{alias}'");
    }

    private Expr RewriteExpr(Expr e, Scope scope, Ctx ctx, bool selectList = false)
    {
        switch (e)
        {
            case NumberLit or StringLit or NullLit:
                return e;
            case ColumnRef c:
                CheckColumn(c, scope, ctx);
                return e;
            case StarExpr st:
                if (!selectList) ctx.Reasons.Add("'*' is only allowed in the select list");
                else if (st.Qualifier is not null && Lookup(scope, st.Qualifier) is null)
                    ctx.Reasons.Add($"unknown table or alias '{st.Qualifier}'");
                return e;
            case FuncCall f:
                if (!layer.Functions.Contains(f.Name)) ctx.Reasons.Add($"function '{f.Name}' is not allowed");
                return f with { Args = f.Args.Select(a => RewriteExpr(a, scope, ctx)).ToList() };
            case UnaryExpr u:
                return u with { Operand = RewriteExpr(u.Operand, scope, ctx) };
            case BinaryExpr b:
                return b with { Left = RewriteExpr(b.Left, scope, ctx), Right = RewriteExpr(b.Right, scope, ctx) };
            case InListExpr il:
                return il with { Value = RewriteExpr(il.Value, scope, ctx), Items = il.Items.Select(i => RewriteExpr(i, scope, ctx)).ToList() };
            case InSubqueryExpr iq:
                return iq with { Value = RewriteExpr(iq.Value, scope, ctx), Query = RewriteSelect(iq.Query, scope, ctx).Item1 };
            case BetweenExpr bt:
                return bt with { Value = RewriteExpr(bt.Value, scope, ctx), Low = RewriteExpr(bt.Low, scope, ctx), High = RewriteExpr(bt.High, scope, ctx) };
            case IsNullExpr isn:
                return isn with { Value = RewriteExpr(isn.Value, scope, ctx) };
            case LikeExpr lk:
                return lk with { Value = RewriteExpr(lk.Value, scope, ctx), Pattern = RewriteExpr(lk.Pattern, scope, ctx) };
            case ExistsExpr ex:
                return ex with { Query = RewriteSelect(ex.Query, scope, ctx).Item1 };
            case ScalarSubquery sq:
                return sq with { Query = RewriteSelect(sq.Query, scope, ctx).Item1 };
            case CaseExpr ce:
                return ce with
                {
                    Operand = ce.Operand is null ? null : RewriteExpr(ce.Operand, scope, ctx),
                    Branches = ce.Branches.Select(b => (RewriteExpr(b.When, scope, ctx), RewriteExpr(b.Then, scope, ctx))).ToList(),
                    Else = ce.Else is null ? null : RewriteExpr(ce.Else, scope, ctx),
                };
            case CastExpr cast:
                return cast with { Value = RewriteExpr(cast.Value, scope, ctx) };
            default:
                ctx.Reasons.Add($"unsupported expression {e.GetType().Name}");
                return e;
        }
    }

    private static ScopeTable? Lookup(Scope? scope, string alias)
    {
        for (var s = scope; s is not null; s = s.Parent)
            if (s.Tables.TryGetValue(alias, out var t)) return t;
        return null;
    }

    private void CheckColumn(ColumnRef c, Scope scope, Ctx ctx)
    {
        var role = ctx.User.Role;
        if (c.Qualifier is not null)
        {
            var t = Lookup(scope, c.Qualifier);
            if (t is null) { ctx.Reasons.Add($"unknown table or alias '{c.Qualifier}'"); return; }
            if (t.Base is { } def)
            {
                var col = def.Column(c.Name);
                if (col is null) ctx.Reasons.Add($"unknown column '{def.Name}.{c.Name}'");
                else if (!col.Roles.Contains(role)) ctx.Reasons.Add($"column '{def.Name}.{col.Name}' is not available to role {ctx.User.RoleName}");
            }
            else if (t.DerivedColumns is { } cols && !cols.Contains(c.Name))
                ctx.Reasons.Add($"unknown column '{c.Qualifier}.{c.Name}'");
            return;
        }

        bool restricted = false, permissive = false;
        string? restrictedName = null;
        for (var s = scope; s is not null; s = s.Parent)
        {
            foreach (var t in s.Tables.Values)
            {
                if (t.Base is { } def)
                {
                    var col = def.Column(c.Name);
                    if (col is null) continue;
                    if (col.Roles.Contains(role)) return;
                    restricted = true; restrictedName = $"{def.Name}.{col.Name}";
                }
                else if (t.DerivedColumns is null || t.DerivedColumns.Contains(c.Name)) permissive = true;
            }
            if (s.SelectAliases.Contains(c.Name)) return;
        }
        if (permissive) return;
        ctx.Reasons.Add(restricted
            ? $"column '{restrictedName}' is not available to role {ctx.User.RoleName}"
            : $"unknown column '{c.Name}'");
    }
}
