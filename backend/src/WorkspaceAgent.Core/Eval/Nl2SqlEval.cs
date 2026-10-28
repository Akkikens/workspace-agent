using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WorkspaceAgent.Identity;
using WorkspaceAgent.Llm;
using WorkspaceAgent.Sql;

namespace WorkspaceAgent.Eval;

/// <param name="Expect">"match": governed result must equal the gold query's result. "blocked": must not execute.</param>
/// <param name="Gold">Reference SQL run ungoverned on the raw warehouse, with the user's scope written out explicitly.</param>
public sealed record EvalCase(string Id, string Category, string User, string Question, string Expect, string? Gold = null, bool Ordered = false);

public sealed record EvalCaseResult(
    string Id, string Category, string User, string Expect, bool Passed, string Outcome, string? ProposedSql, string? Detail);

public sealed record EvalReport(string Model, int Total, int Passed, IReadOnlyList<EvalCaseResult> Cases)
{
    public int Leaks => Cases.Count(c => c.Outcome == "leak");
    public IEnumerable<string> Categories => Cases.Select(c => c.Category).Distinct();
    public (int Passed, int Total) For(string category) =>
        (Cases.Count(c => c.Category == category && c.Passed), Cases.Count(c => c.Category == category));
    public (int Passed, int Total) Expecting(string expect) =>
        (Cases.Count(c => c.Expect == expect && c.Passed), Cases.Count(c => c.Expect == expect));
}

/// <summary>
/// Execution-accuracy eval for NL-to-SQL under governance. For each case the model proposes SQL from the
/// question and the role's schema description; the SQL goes through the same governor and executor as the
/// agent tool; the result is compared with the gold query's result (multiset of rows, or ordered when the
/// question asks for an order). Governance cases pass only if nothing executes.
/// </summary>
public sealed class Nl2SqlEval(IChatModel model, SqlGovernor governor, Warehouse warehouse)
{
    public static IReadOnlyList<EvalCase> LoadCases(string path) =>
        JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public string SystemPrompt(UserContext user) =>
        "Translate the user's question into one SQLite SELECT statement. Reply with only the SQL in a ```sql code block.\n\n"
        + governor.Layer.DescribeFor(user);

    public async Task<EvalReport> RunAsync(IReadOnlyList<EvalCase> cases, CancellationToken ct = default)
    {
        var results = new List<EvalCaseResult>();
        foreach (var c in cases) results.Add(await RunCaseAsync(c, ct));
        return new EvalReport(model.Name, results.Count, results.Count(r => r.Passed), results);
    }

    private async Task<EvalCaseResult> RunCaseAsync(EvalCase c, CancellationToken ct)
    {
        var user = DemoUsers.Find(c.User) ?? throw new ArgumentException($"case {c.Id}: unknown user {c.User}");
        EvalCaseResult R(bool passed, string outcome, string? sql, string? detail) =>
            new(c.Id, c.Category, c.User, c.Expect, passed, outcome, sql, detail);

        string sql;
        try
        {
            var resp = await model.CompleteAsync(SystemPrompt(user), [ChatMessage.User(c.Question)], [], ct);
            sql = ExtractSql(resp.Text);
        }
        catch (CassetteMissException e) { return R(false, "cassette_drift", null, e.Message); }

        var decision = governor.Govern(sql, user);
        if (!decision.Allowed)
            return c.Expect == "blocked"
                ? R(true, "blocked", sql, string.Join("; ", decision.Reasons))
                : R(false, "rejected", sql, string.Join("; ", decision.Reasons));

        QueryResult actual;
        try { actual = warehouse.ExecuteGoverned(decision.Query!, governor.Layer.Functions); }
        catch (AuthorizerDeniedException e)
        {
            return c.Expect == "blocked" ? R(true, "blocked", sql, e.Message) : R(false, "rejected", sql, e.Message);
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            return c.Expect == "blocked" ? R(true, "blocked", sql, e.Message) : R(false, "sql_error", sql, e.Message);
        }

        if (c.Expect == "blocked")
            return R(false, "leak", sql, $"executed and returned {actual.Rows.Count} row(s); expected it to be blocked");

        var gold = warehouse.ExecuteRaw(c.Gold ?? throw new ArgumentException($"case {c.Id}: match case without gold SQL"));
        var (same, why) = Compare(gold, actual, c.Ordered);
        return same ? R(true, "match", sql, null) : R(false, "mismatch", sql, why);
    }

    private static readonly Regex Fence = new("```(?:sql)?\\s*(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static string ExtractSql(string text)
    {
        var m = Fence.Match(text);
        return (m.Success ? m.Groups[1].Value : text).Trim();
    }

    public static (bool Same, string? Why) Compare(QueryResult gold, QueryResult actual, bool ordered)
    {
        if (gold.Columns.Count != actual.Columns.Count)
            return (false, $"expected {gold.Columns.Count} column(s), got {actual.Columns.Count}");
        var g = gold.Rows.Select(Key).ToList();
        var a = actual.Rows.Select(Key).ToList();
        if (g.Count != a.Count) return (false, $"expected {g.Count} row(s), got {a.Count}");
        if (!ordered) { g.Sort(StringComparer.Ordinal); a.Sort(StringComparer.Ordinal); }
        for (int i = 0; i < g.Count; i++)
            if (g[i] != a[i]) return (false, $"row {i}: expected [{g[i]}], got [{a[i]}]");
        return (true, null);
    }

    /// <summary>Numbers are compared after rounding to 1 decimal place, so ROUND(x, 1) in the model's SQL still matches.</summary>
    private static string Key(object?[] row) => string.Join(" | ", row.Select(Normalize));

    private static string Normalize(object? v) => v switch
    {
        null => "NULL",
        long or int or double or float or decimal =>
            Math.Round(Convert.ToDecimal(v, CultureInfo.InvariantCulture), 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    public static string Markdown(EvalReport r)
    {
        var sb = new StringBuilder();
        var (mp, mt) = r.Expecting("match");
        var (bp, bt) = r.Expecting("blocked");
        sb.AppendLine($"# NL-to-SQL eval ({r.Model})").AppendLine();
        sb.AppendLine($"- Execution accuracy (answerable questions): **{mp}/{mt}**");
        sb.AppendLine($"- Governance cases blocked: **{bp}/{bt}**, leaks: **{r.Leaks}**");
        sb.AppendLine($"- Overall: {r.Passed}/{r.Total}").AppendLine();
        sb.AppendLine("| category | passed |").AppendLine("|---|---|");
        foreach (var cat in r.Categories) { var (p, t) = r.For(cat); sb.AppendLine($"| {cat} | {p}/{t} |"); }
        sb.AppendLine().AppendLine("| case | user | expect | outcome | detail |").AppendLine("|---|---|---|---|---|");
        foreach (var c in r.Cases)
            sb.AppendLine($"| {c.Id} | {c.User} | {c.Expect} | {(c.Passed ? "pass" : "FAIL")}: {c.Outcome} | {(c.Detail ?? "").Replace("|", "\\|")} |");
        return sb.ToString();
    }
}
