namespace WorkspaceAgent.Sql;

// AST for the SELECT subset the governor accepts. Anything the parser cannot represent is rejected,
// so the emitted SQL can only contain constructs listed here.

public abstract record Expr;
public sealed record NumberLit(string Text) : Expr;
public sealed record StringLit(string Value) : Expr;
public sealed record NullLit : Expr;
public sealed record ColumnRef(string? Qualifier, string Name) : Expr;
public sealed record StarExpr(string? Qualifier) : Expr;
public sealed record FuncCall(string Name, bool Distinct, IReadOnlyList<Expr> Args, bool StarArg) : Expr;
public sealed record UnaryExpr(string Op, Expr Operand) : Expr;
public sealed record BinaryExpr(string Op, Expr Left, Expr Right) : Expr;
public sealed record InListExpr(Expr Value, bool Negated, IReadOnlyList<Expr> Items) : Expr;
public sealed record InSubqueryExpr(Expr Value, bool Negated, SelectStmt Query) : Expr;
public sealed record BetweenExpr(Expr Value, bool Negated, Expr Low, Expr High) : Expr;
public sealed record IsNullExpr(Expr Value, bool Negated) : Expr;
public sealed record LikeExpr(Expr Value, bool Negated, Expr Pattern) : Expr;
public sealed record ExistsExpr(SelectStmt Query) : Expr;
public sealed record ScalarSubquery(SelectStmt Query) : Expr;
public sealed record CaseExpr(Expr? Operand, IReadOnlyList<(Expr When, Expr Then)> Branches, Expr? Else) : Expr;
public sealed record CastExpr(Expr Value, string TypeName) : Expr;

/// <summary>Trusted SQL from the semantic layer (row filters). Never produced by the parser.</summary>
public sealed record TrustedSql(string Sql) : Expr;

public sealed record SelectItem(Expr Expr, string? Alias);

public abstract record TableSource;
public sealed record TableRef(string Name, string? Alias) : TableSource;
public sealed record DerivedTable(SelectStmt Query, string Alias) : TableSource;

public sealed record Join(string Kind, TableSource Source, Expr? On);

public sealed record OrderItem(Expr Expr, bool Descending);

public sealed record SelectStmt(
    bool Distinct,
    IReadOnlyList<SelectItem> Items,
    TableSource? From,
    IReadOnlyList<Join> Joins,
    Expr? Where,
    IReadOnlyList<Expr> GroupBy,
    Expr? Having,
    IReadOnlyList<OrderItem> OrderBy,
    long? Limit,
    long? Offset);

public sealed class SqlParseException(string message) : Exception(message);
