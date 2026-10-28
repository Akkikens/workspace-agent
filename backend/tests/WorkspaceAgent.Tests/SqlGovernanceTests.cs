using WorkspaceAgent.Identity;
using WorkspaceAgent.Sql;

namespace WorkspaceAgent.Tests;

public sealed class WarehouseFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wa-test-{Guid.NewGuid():n}.db");
    public Warehouse Warehouse { get; }
    public SqlGovernor Governor { get; } = new(SemanticLayer.Default);

    public WarehouseFixture() => Warehouse = Warehouse.CreateSeeded(Path);

    public QueryResult Run(string sql, UserContext user)
    {
        var d = Governor.Govern(sql, user);
        Assert.True(d.Allowed, $"expected allowed, got: {string.Join("; ", d.Reasons)}");
        return Warehouse.ExecuteGoverned(d.Query!, Governor.Layer.Functions);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(Path); } catch (IOException) { }
    }
}

/// <summary>The validator must refuse anything that is not a governed, read-only SELECT.</summary>
public sealed class SqlValidatorNegativeTests(WarehouseFixture fx) : IClassFixture<WarehouseFixture>
{
    private IReadOnlyList<string> Rejected(string sql, UserContext? user = null)
    {
        var d = fx.Governor.Govern(sql, user ?? DemoUsers.Carol);
        Assert.False(d.Allowed, $"expected rejection for: {sql}");
        return d.Reasons;
    }

    [Theory]
    [InlineData("DELETE FROM defects", "only SELECT")]
    [InlineData("UPDATE programs SET status = 'completed'", "only SELECT")]
    [InlineData("INSERT INTO programs (name) VALUES ('x')", "only SELECT")]
    [InlineData("DROP TABLE builds", "only SELECT")]
    [InlineData("PRAGMA table_info(employees)", "only SELECT")]
    [InlineData("ATTACH DATABASE '/tmp/x.db' AS x", "only SELECT")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", "WITH")]
    [InlineData("SELECT name FROM programs; DROP TABLE builds", "multiple statements")]
    [InlineData("SELECT name FROM programs -- AND business_unit = 'Modem'", "comments")]
    [InlineData("SELECT name FROM programs /* hidden */", "comments")]
    [InlineData("SELECT name FROM programs WHERE business_unit = ?", "parameters")]
    [InlineData("SELECT name FROM programs WHERE business_unit = :bu", "parameters")]
    [InlineData("SELECT name FROM programs UNION SELECT name FROM employees", "UNION")]
    [InlineData("SELECT name FROM main.programs", "schema-qualified")]
    [InlineData("SELECT name FROM pragma_table_info('employees')", "table-valued")]
    [InlineData("SELECT sql FROM sqlite_master", "not in the semantic layer")]
    [InlineData("SELECT * FROM audit_log", "not in the semantic layer")]
    [InlineData("SELECT load_extension('/tmp/x.so')", "function 'load_extension'")]
    [InlineData("SELECT randomblob(10) FROM programs", "function 'randomblob'")]
    [InlineData("SELECT name FROM programs WHERE name GLOB 'A*'", "GLOB")]
    [InlineData("SELECT `name` FROM programs", "unsupported character")]
    [InlineData("SELECT name FROM programs LIMIT (SELECT 5)", "LIMIT must be")]
    [InlineData("SELECT nope FROM programs", "unknown column 'nope'")]
    [InlineData("SELECT p.nope FROM programs p", "unknown column 'programs.nope'")]
    [InlineData("SELECT x.name FROM programs p", "unknown table or alias 'x'")]
    [InlineData("", "empty query")]
    public void Rejects(string sql, string reasonFragment)
    {
        var reasons = Rejected(sql);
        Assert.Contains(reasons, r => r.Contains(reasonFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("SELECT name, salary_usd FROM employees")]
    [InlineData("SELECT e.salary_usd FROM employees e")]
    [InlineData("SELECT \"salary_usd\" FROM employees")]
    [InlineData("SELECT name FROM employees ORDER BY salary_usd DESC")]
    [InlineData("SELECT AVG(salary_usd) FROM employees")]
    [InlineData("SELECT name FROM programs WHERE program_id IN (SELECT employee_id FROM employees WHERE salary_usd > 100)")]
    public void SalaryIsNeverExposedEvenToDirectors(string sql) =>
        Assert.Contains(Rejected(sql, DemoUsers.Carol), r => r.Contains("employees.salary_usd") && r.Contains("not available"));

    [Fact]
    public void BudgetIsDirectorOnly()
    {
        Assert.Contains(Rejected("SELECT name, budget_musd FROM programs", DemoUsers.Bob), r => r.Contains("budget_musd"));
        Assert.Contains(Rejected("SELECT SUM(budget_musd) FROM programs", DemoUsers.Alice), r => r.Contains("budget_musd"));
        Assert.True(fx.Governor.Govern("SELECT name, budget_musd FROM programs", DemoUsers.Carol).Allowed);
    }

    [Fact]
    public void EngineersCannotSeeEmployees() =>
        Assert.Contains(Rejected("SELECT COUNT(*) FROM employees", DemoUsers.Alice), r => r.Contains("'employees' is not available to role engineer"));
}

public sealed class SqlRewriteTests(WarehouseFixture fx) : IClassFixture<WarehouseFixture>
{
    [Fact]
    public void AddsTheRowCapWhenMissing()
    {
        var q = fx.Governor.Govern("SELECT name FROM programs", DemoUsers.Carol).Query!;
        Assert.EndsWith("LIMIT 200", q.Sql);
        Assert.False(q.LimitClamped);
    }

    [Fact]
    public void ClampsALimitAboveTheCapAndKeepsASmallerOne()
    {
        var big = fx.Governor.Govern("SELECT name FROM programs LIMIT 100000", DemoUsers.Carol).Query!;
        Assert.Equal(200, big.Limit);
        Assert.True(big.LimitClamped);
        Assert.EndsWith("LIMIT 5", fx.Governor.Govern("SELECT name FROM programs LIMIT 5", DemoUsers.Carol).Query!.Sql);
    }

    [Fact]
    public void RowFilterIsABoundParameterNotTextFromTheUser()
    {
        var q = fx.Governor.Govern("SELECT name FROM programs", DemoUsers.Alice).Query!;
        Assert.Contains("business_unit = @bu", q.Sql);
        Assert.DoesNotContain("Modem", q.Sql);
        Assert.Equal("Modem", q.Parameters["@bu"]);
        Assert.Contains("programs: business_unit = 'Modem'", q.AppliedFilters);
    }

    [Fact]
    public void DirectorsGetNoRowFilter()
    {
        var q = fx.Governor.Govern("SELECT name FROM programs", DemoUsers.Carol).Query!;
        Assert.Empty(q.Parameters);
        Assert.Empty(q.AppliedFilters);
    }

    [Fact]
    public void SelectStarProjectsOnlyGrantedColumns()
    {
        var r = fx.Run("SELECT * FROM programs", DemoUsers.Alice);
        Assert.DoesNotContain("budget_musd", r.Columns);
        Assert.Contains("business_unit", r.Columns);
        Assert.Contains("budget_musd", fx.Run("SELECT * FROM programs", DemoUsers.Carol).Columns);
    }

    [Fact]
    public void QuotesAndPrecedenceSurviveTheRoundTrip()
    {
        var sql = SqlEmitter.Emit(SqlParser.Parse("SELECT name FROM programs WHERE name = 'O''Brien' OR status = 'a' AND business_unit = 'b'"));
        Assert.Contains("'O''Brien'", sql);
        Assert.Contains("((\"name\" = 'O''Brien') OR ((\"status\" = 'a') AND (\"business_unit\" = 'b')))", sql);
    }
}

/// <summary>Row-level security: whatever the model writes, a scoped user only reads their business unit.</summary>
public sealed class RowLevelSecurityTests(WarehouseFixture fx) : IClassFixture<WarehouseFixture>
{
    private static long Count(QueryResult r) => Convert.ToInt64(r.Rows[0][0]);

    [Fact]
    public void EngineerSeesOnlyTheirBusinessUnit()
    {
        var r = fx.Run("SELECT business_unit FROM programs", DemoUsers.Alice);
        Assert.Equal(3, r.Rows.Count);
        Assert.All(r.Rows, row => Assert.Equal("Modem", row[0]));
    }

    [Fact]
    public void DirectorSeesEveryProgram() => Assert.Equal(12, fx.Run("SELECT name FROM programs", DemoUsers.Carol).Rows.Count);

    [Theory]
    [InlineData("SELECT COUNT(*) FROM programs WHERE business_unit = 'Camera'")]
    [InlineData("SELECT COUNT(*) FROM defects WHERE program_id IN (SELECT program_id FROM programs WHERE business_unit = 'Camera')")]
    [InlineData("SELECT COUNT(*) FROM defects d WHERE EXISTS (SELECT 1 FROM programs p WHERE p.program_id = d.program_id AND p.business_unit = 'Camera')")]
    [InlineData("SELECT COUNT(*) FROM (SELECT * FROM programs) x WHERE x.business_unit = 'Camera'")]
    [InlineData("SELECT COUNT(*) FROM builds b JOIN programs p ON p.program_id = b.program_id WHERE p.business_unit <> 'Modem'")]
    public void FilterAttemptsAgainstOtherUnitsReturnNothing(string sql) => Assert.Equal(0, Count(fx.Run(sql, DemoUsers.Alice)));

    [Fact]
    public void TautologiesDoNotWidenTheScope()
    {
        Assert.Equal(3, fx.Run("SELECT name FROM programs WHERE business_unit = 'Modem' OR 1 = 1", DemoUsers.Alice).Rows.Count);
        Assert.Equal(3, Count(fx.Run("SELECT (SELECT COUNT(*) FROM programs) FROM programs LIMIT 1", DemoUsers.Alice)));
    }

    [Fact]
    public void ChildTablesAreScopedThroughTheirProgram()
    {
        var all = fx.Warehouse.ExecuteRaw("SELECT COUNT(*) FROM defects d JOIN programs p ON p.program_id = d.program_id WHERE p.business_unit = 'Modem'");
        Assert.Equal(Count(all), Count(fx.Run("SELECT COUNT(*) FROM defects", DemoUsers.Alice)));
        Assert.True(Count(fx.Run("SELECT COUNT(*) FROM defects", DemoUsers.Carol)) > Count(all));
    }

    [Fact]
    public void ProgramManagerSeesOnlyTheirUnitsEmployees()
    {
        var r = fx.Run("SELECT DISTINCT business_unit FROM employees", DemoUsers.Bob);
        Assert.Single(r.Rows);
        Assert.Equal("Camera", r.Rows[0][0]);
    }
}

/// <summary>The SQLite authorizer is a second check that does not trust the governor's output.</summary>
public sealed class AuthorizerTests(WarehouseFixture fx) : IClassFixture<WarehouseFixture>
{
    private GovernedQuery Forged(string sql, UserContext user) =>
        new(sql, sql, new Dictionary<string, object?>(), [], 200, false, SemanticLayer.Default.ReadGrants(user));

    [Fact]
    public void DeniesReadingAColumnOutsideTheGrants()
    {
        var e = Assert.Throws<AuthorizerDeniedException>(() =>
            fx.Warehouse.ExecuteGoverned(Forged("SELECT salary_usd FROM employees", DemoUsers.Carol), SemanticLayer.Default.Functions));
        Assert.Contains(e.Denied, d => d.Contains("salary_usd"));
    }

    [Fact]
    public void DeniesAFunctionOutsideTheAllowList() =>
        Assert.Throws<AuthorizerDeniedException>(() =>
            fx.Warehouse.ExecuteGoverned(Forged("SELECT hex(name) FROM programs", DemoUsers.Carol), SemanticLayer.Default.Functions));

    [Fact]
    public void DeniesWritesEvenIfTheyReachTheExecutor() =>
        Assert.ThrowsAny<Exception>(() =>
            fx.Warehouse.ExecuteGoverned(Forged("DELETE FROM defects", DemoUsers.Carol), SemanticLayer.Default.Functions));

    [Fact]
    public void AllowsGrantedReads() =>
        Assert.Equal(12, fx.Warehouse.ExecuteGoverned(Forged("SELECT name FROM programs", DemoUsers.Carol), SemanticLayer.Default.Functions).Rows.Count);
}
