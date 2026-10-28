using WorkspaceAgent.Eval;
using WorkspaceAgent.Llm;
using WorkspaceAgent.Sql;

namespace WorkspaceAgent.Tests;

public sealed class EvalTests(WarehouseFixture fx) : IClassFixture<WarehouseFixture>
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public async Task CommittedCassetteReplaysWithNoLeaks()
    {
        var cases = Nl2SqlEval.LoadCases(RepoFile("evals/nl2sql/cases.json"));
        var model = CassetteChatModel.FromFile(RepoFile("evals/nl2sql/cassette.json"));
        var report = await new Nl2SqlEval(model, fx.Governor, fx.Warehouse).RunAsync(cases);

        Assert.Equal(0, report.Leaks);
        Assert.All(report.Cases.Where(c => c.Expect == "blocked"), c => Assert.True(c.Passed, c.Id));
    }

    [Fact]
    public async Task AGovernanceCaseThatExecutesIsReportedAsALeak()
    {
        var model = new CassetteChatModel(new Dictionary<string, string> { ["q"] = "```sql\nSELECT name FROM programs\n```" });
        var report = await new Nl2SqlEval(model, fx.Governor, fx.Warehouse)
            .RunAsync([new EvalCase("x", "governance", "alice", "q", "blocked")]);
        Assert.Equal(1, report.Leaks);
    }

    [Fact]
    public async Task AChangedQuestionIsCassetteDriftNotAPass()
    {
        var model = new CassetteChatModel(new Dictionary<string, string>());
        var report = await new Nl2SqlEval(model, fx.Governor, fx.Warehouse)
            .RunAsync([new EvalCase("x", "answerable", "carol", "new question", "match", "SELECT 1")]);
        Assert.Equal("cassette_drift", Assert.Single(report.Cases).Outcome);
    }

    [Fact]
    public void ComparisonIsOrderInsensitiveUnlessAskedAndRoundsToOneDecimal()
    {
        var gold = new QueryResult(["a", "b"], [["x", 1.04], ["y", 2L]]);
        var swapped = new QueryResult(["c", "d"], [["y", 2.0], ["x", 1.0]]);
        Assert.True(Nl2SqlEval.Compare(gold, swapped, ordered: false).Same);
        Assert.False(Nl2SqlEval.Compare(gold, swapped, ordered: true).Same);
        Assert.False(Nl2SqlEval.Compare(gold, new QueryResult(["a"], [["x"], ["y"]]), false).Same);
        Assert.False(Nl2SqlEval.Compare(gold, new QueryResult(["a", "b"], [["x", 1.2], ["y", 2L]]), false).Same);
    }

    [Fact]
    public void ExtractsSqlFromAFencedReply() =>
        Assert.Equal("SELECT 1", Nl2SqlEval.ExtractSql("Here you go:\n```sql\nSELECT 1\n```\nDone."));
}
