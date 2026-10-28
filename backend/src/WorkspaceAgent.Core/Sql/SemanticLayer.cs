using System.Text;
using WorkspaceAgent.Identity;

namespace WorkspaceAgent.Sql;

/// <param name="Roles">Roles granted this column. Empty = known to the governor but never exposed.</param>
public sealed record ColumnDef(string Name, string Type, string Description, IReadOnlySet<UserRole> Roles);

/// <param name="BusinessUnitFilter">
/// Trusted SQL predicate (written by the data owner, never by the model) applied to every read of the
/// table for users scoped to a business unit. It may reference the bound parameter <c>@bu</c>.
/// </param>
/// <param name="FilterReads">Extra (table, column) reads the filter needs, granted to the SQLite authorizer.</param>
public sealed record TableDef(
    string Name,
    string Description,
    IReadOnlyList<ColumnDef> Columns,
    IReadOnlySet<UserRole> Roles,
    string? BusinessUnitFilter,
    IReadOnlyList<(string Table, string Column)> FilterReads)
{
    public ColumnDef? Column(string name) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ColumnDef> GrantedColumns(UserRole role) => Columns.Where(c => c.Roles.Contains(role));
}

/// <summary>
/// What the model may query, per role: tables, columns, and row-level filters. This is the single source
/// for the prompt the model sees, the validator, the rewrite and the SQLite authorizer allow-list.
/// </summary>
public sealed class SemanticLayer(IReadOnlyList<TableDef> tables, IReadOnlySet<string> functions, int maxRows)
{
    public IReadOnlyList<TableDef> Tables { get; } = tables;
    public IReadOnlySet<string> Functions { get; } = functions;
    public int MaxRows { get; } = maxRows;

    public TableDef? Table(string name) =>
        Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool IsRowScoped(UserContext user) => user.BusinessUnit is not null;

    /// <summary>(table, column) pairs the SQLite authorizer lets this user's statements read.</summary>
    public HashSet<(string, string)> ReadGrants(UserContext user)
    {
        var grants = new HashSet<(string, string)>();
        foreach (var t in Tables.Where(t => t.Roles.Contains(user.Role)))
        {
            foreach (var c in t.GrantedColumns(user.Role)) grants.Add((t.Name.ToLowerInvariant(), c.Name.ToLowerInvariant()));
            if (IsRowScoped(user))
                foreach (var (ft, fc) in t.FilterReads) grants.Add((ft.ToLowerInvariant(), fc.ToLowerInvariant()));
        }
        return grants;
    }

    /// <summary>Schema description for the model, limited to what this user's role can see.</summary>
    public string DescribeFor(UserContext user)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SQLite database of engineering programs. Tables and columns you may use:");
        foreach (var t in Tables.Where(t => t.Roles.Contains(user.Role)))
        {
            sb.AppendLine($"- {t.Name}: {t.Description}");
            foreach (var c in t.GrantedColumns(user.Role))
                sb.AppendLine($"    {c.Name} {c.Type} -- {c.Description}");
        }
        sb.AppendLine("Rules: write one SQLite SELECT statement. No comments, no CTEs (WITH), no UNION, no parameters.");
        sb.AppendLine($"Results are capped at {MaxRows} rows.");
        if (IsRowScoped(user))
            sb.AppendLine($"Row-level security restricts every table to the {user.BusinessUnit} business unit automatically; do not add that filter yourself.");
        return sb.ToString();
    }

    private static readonly IReadOnlySet<UserRole> AllRoles = new HashSet<UserRole> { UserRole.Engineer, UserRole.ProgramManager, UserRole.Director };
    private static readonly IReadOnlySet<UserRole> PmAndDirector = new HashSet<UserRole> { UserRole.ProgramManager, UserRole.Director };
    private static readonly IReadOnlySet<UserRole> DirectorOnly = new HashSet<UserRole> { UserRole.Director };
    private static readonly IReadOnlySet<UserRole> Nobody = new HashSet<UserRole>();

    private const string ProgramScoped = "program_id IN (SELECT program_id FROM programs WHERE business_unit = @bu)";
    private static readonly (string, string)[] ProgramScopedReads = [("programs", "program_id"), ("programs", "business_unit")];

    /// <summary>The demo warehouse's semantic layer (see <see cref="Warehouse"/> for the data).</summary>
    public static SemanticLayer Default { get; } = new(
    [
        new TableDef("programs", "one row per engineering program",
        [
            new("program_id", "INTEGER", "primary key", AllRoles),
            new("name", "TEXT", "program name", AllRoles),
            new("business_unit", "TEXT", "Modem, Camera, Automotive or Compute", AllRoles),
            new("status", "TEXT", "active, at_risk or completed", AllRoles),
            new("start_date", "TEXT", "ISO date", AllRoles),
            new("target_date", "TEXT", "ISO date of planned launch", AllRoles),
            new("budget_musd", "REAL", "approved budget in millions of USD", DirectorOnly),
        ], AllRoles, "business_unit = @bu", []),

        new TableDef("milestones", "program milestones",
        [
            new("milestone_id", "INTEGER", "primary key", AllRoles),
            new("program_id", "INTEGER", "references programs.program_id", AllRoles),
            new("name", "TEXT", "milestone name", AllRoles),
            new("due_date", "TEXT", "ISO date", AllRoles),
            new("completed_date", "TEXT", "ISO date, NULL if not completed", AllRoles),
            new("status", "TEXT", "done, open or late", AllRoles),
        ], AllRoles, ProgramScoped, ProgramScopedReads),

        new TableDef("defects", "defects filed against programs",
        [
            new("defect_id", "INTEGER", "primary key", AllRoles),
            new("program_id", "INTEGER", "references programs.program_id", AllRoles),
            new("severity", "TEXT", "S1 (critical) to S4 (minor)", AllRoles),
            new("status", "TEXT", "open, in_progress or closed", AllRoles),
            new("component", "TEXT", "component name", AllRoles),
            new("opened_at", "TEXT", "ISO date", AllRoles),
            new("closed_at", "TEXT", "ISO date, NULL unless closed", AllRoles),
        ], AllRoles, ProgramScoped, ProgramScopedReads),

        new TableDef("builds", "CI builds per program",
        [
            new("build_id", "INTEGER", "primary key", AllRoles),
            new("program_id", "INTEGER", "references programs.program_id", AllRoles),
            new("branch", "TEXT", "main or release", AllRoles),
            new("status", "TEXT", "passed or failed", AllRoles),
            new("duration_min", "INTEGER", "build duration in minutes", AllRoles),
            new("started_at", "TEXT", "ISO date", AllRoles),
        ], AllRoles, ProgramScoped, ProgramScopedReads),

        new TableDef("employees", "engineering staff",
        [
            new("employee_id", "INTEGER", "primary key", PmAndDirector),
            new("name", "TEXT", "full name", PmAndDirector),
            new("business_unit", "TEXT", "business unit", PmAndDirector),
            new("title", "TEXT", "job title", PmAndDirector),
            new("salary_usd", "INTEGER", "compensation (restricted, never exposed)", Nobody),
        ], PmAndDirector, "business_unit = @bu", []),
    ],
    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "count", "sum", "avg", "min", "max", "total", "round", "abs", "lower", "upper", "length", "substr", "trim",
        "coalesce", "ifnull", "nullif", "date", "strftime", "julianday", "cast",
    },
    maxRows: 200);
}
