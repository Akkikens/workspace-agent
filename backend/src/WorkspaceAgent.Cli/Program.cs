using System.Text.Json;
using WorkspaceAgent.Eval;
using WorkspaceAgent.Llm;
using WorkspaceAgent.Providers;
using WorkspaceAgent.Sql;

// workspace-agent CLI
//   eval   --cases=PATH --cassette=PATH [--baseline=PATH] [--out=DIR] [--write-baseline]
//          [--model=cassette|anthropic] [--record=PATH]
//   schema --user=alice       print the schema description the model sees for a user
//   seed   --out=PATH         write the seeded demo warehouse to a SQLite file (for inspection)

var opts = args.Skip(1)
    .Select(a => a.TrimStart('-').Split('=', 2))
    .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "true");
string? Opt(string k) => opts.GetValueOrDefault(k);

switch (args.FirstOrDefault())
{
    case "eval": return await Eval();
    case "seed":
        Warehouse.CreateSeeded(Opt("out") ?? "warehouse.db");
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Console.WriteLine($"wrote {Opt("out") ?? "warehouse.db"}");
        return 0;
    case "schema":
        Console.WriteLine(SemanticLayer.Default.DescribeFor(WorkspaceAgent.Identity.DemoUsers.Find(Opt("user")) ?? WorkspaceAgent.Identity.DemoUsers.Alice));
        return 0;
    default:
        Console.Error.WriteLine("usage: eval --cases=... --cassette=... [--baseline=...] [--out=...] | schema --user=...");
        return 2;
}

async Task<int> Eval()
{
    var cases = Nl2SqlEval.LoadCases(Opt("cases") ?? "evals/nl2sql/cases.json");
    IChatModel model = (Opt("model") ?? "cassette") switch
    {
        "anthropic" => new AnthropicChatModel(Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-opus-5-5"),
        _ => CassetteChatModel.FromFile(Opt("cassette") ?? "evals/nl2sql/cassette.json"),
    };
    RecordingChatModel? recorder = Opt("record") is not null ? new RecordingChatModel(model) : null;

    var dbPath = Path.Combine(Path.GetTempPath(), $"workspace-agent-eval-{Environment.ProcessId}.db");
    var warehouse = Warehouse.CreateSeeded(dbPath);
    try
    {
        var report = await new Nl2SqlEval((IChatModel?)recorder ?? model, new SqlGovernor(SemanticLayer.Default), warehouse).RunAsync(cases);
        recorder?.Save(Opt("record")!);

        var md = Nl2SqlEval.Markdown(report);
        Console.WriteLine(md);
        if (Opt("out") is { } outDir)
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "results.md"), md);
            File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        var passing = report.Cases.Where(c => c.Passed).Select(c => c.Id).ToList();
        if (Opt("baseline") is { } baselinePath)
        {
            if (Opt("write-baseline") is not null)
            {
                File.WriteAllText(baselinePath, JsonSerializer.Serialize(new { passing }, new JsonSerializerOptions { WriteIndented = true }) + "\n");
                Console.WriteLine($"wrote baseline with {passing.Count} passing case(s)");
            }
            else
            {
                var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath)).RootElement.GetProperty("passing")
                    .EnumerateArray().Select(e => e.GetString()!).ToHashSet();
                var regressed = baseline.Except(passing).ToList();
                if (regressed.Count > 0)
                {
                    Console.Error.WriteLine($"GATE FAILED: regressed vs baseline: {string.Join(", ", regressed)}");
                    return 1;
                }
            }
        }
        if (report.Leaks > 0)
        {
            Console.Error.WriteLine($"GATE FAILED: {report.Leaks} governance case(s) executed instead of being blocked");
            return 1;
        }
        Console.WriteLine("gate: ok");
        return 0;
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch (IOException) { }
    }
}
