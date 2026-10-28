using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WorkspaceAgent.Llm;

/// <summary>
/// Deterministic stand-in for a model, driven by a JSON script. A rule whose regex matches the latest
/// user message supplies one step per model turn: either tool calls or final text. The step index is
/// the number of assistant turns since that user message, so a paused-then-resumed run picks up at the
/// right step. Used by the demo and by the HTTP tests; it is not a model and makes no decisions.
/// </summary>
public sealed class ScriptedChatModel : IChatModel
{
    private readonly IReadOnlyList<ScriptRule> _rules;
    private int _ids;

    public ScriptedChatModel(IReadOnlyList<ScriptRule> rules) => _rules = rules;

    public string Name => "scripted";

    public static ScriptedChatModel FromFile(string path) => FromJson(File.ReadAllText(path));

    public static ScriptedChatModel FromJson(string json)
    {
        var doc = JsonNode.Parse(json)!.AsObject();
        var rules = new List<ScriptRule>();
        foreach (var r in doc["rules"]!.AsArray())
        {
            var steps = new List<ScriptStep>();
            foreach (var s in r!["steps"]!.AsArray())
            {
                var calls = s!["tools"]?.AsArray()
                    .Select(t => (t!["name"]!.GetValue<string>(), t["args"]?.AsObject() ?? new JsonObject()))
                    .ToList() ?? [];
                steps.Add(new ScriptStep(s["text"]?.GetValue<string>() ?? "", calls));
            }
            rules.Add(new ScriptRule(new Regex(r["match"]!.GetValue<string>(), RegexOptions.IgnoreCase), steps));
        }
        return new ScriptedChatModel(rules);
    }

    public Task<ModelResponse> CompleteAsync(
        string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
    {
        var lastUser = messages.Select((m, i) => (m, i)).LastOrDefault(x => x.m.Role == MessageRole.User);
        var prompt = lastUser.m?.Text ?? "";
        var step = messages.Skip(lastUser.i + 1).Count(m => m.Role == MessageRole.Assistant);

        var rule = _rules.FirstOrDefault(r => r.Match.IsMatch(prompt));
        if (rule is null)
            return Task.FromResult(new ModelResponse(Fallback(prompt), []));
        if (step >= rule.Steps.Count)
            return Task.FromResult(new ModelResponse("", []));

        var s = rule.Steps[step];
        var calls = s.Tools
            .Select(t => new ToolCall($"call_{Interlocked.Increment(ref _ids):D3}", t.Name, (JsonObject)t.Args.DeepClone()))
            .ToList();
        return Task.FromResult(new ModelResponse(s.Text, calls));
    }

    private static readonly Regex ChunkTag = new("<chunk path=\"([^\"]+)\" lines=\"(\\d+)-(\\d+)\">", RegexOptions.Compiled);

    /// <summary>
    /// With no matching rule: if the prompt carries retrieved code chunks, answer extractively by citing
    /// the top ones (so the VS Code flow works offline); otherwise say that no script covers the prompt.
    /// </summary>
    private static string Fallback(string prompt)
    {
        var chunks = ChunkTag.Matches(prompt).Take(3).ToList();
        if (chunks.Count == 0)
            return "(scripted model) There is no scripted answer for this prompt. Start the API with " +
                   "WORKSPACE_AGENT_MODEL=anthropic and ANTHROPIC_API_KEY set to use Claude.";

        var sb = new StringBuilder("(scripted model, extractive) The most relevant retrieved code is ");
        sb.Append(string.Join(", ", chunks.Select(m => $"[{m.Groups[1].Value}:{m.Groups[2].Value}-{m.Groups[3].Value}]")));
        sb.Append(". A real model would summarize these chunks; this offline fallback only cites them in retrieval order.");
        return sb.ToString();
    }
}

public sealed record ScriptRule(Regex Match, IReadOnlyList<ScriptStep> Steps);

public sealed record ScriptStep(string Text, IReadOnlyList<(string Name, JsonObject Args)> Tools);

/// <summary>
/// Replays recorded responses keyed by the latest user message. A prompt with no recording throws, so a
/// changed eval question is reported as cassette drift instead of silently passing.
/// </summary>
public sealed class CassetteChatModel : IChatModel
{
    private readonly IReadOnlyDictionary<string, string> _responses;

    public CassetteChatModel(IReadOnlyDictionary<string, string> responses) => _responses = responses;

    public string Name => "cassette";

    public static CassetteChatModel FromFile(string path) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!);

    public Task<ModelResponse> CompleteAsync(
        string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
    {
        var prompt = messages.Last(m => m.Role == MessageRole.User).Text ?? "";
        return _responses.TryGetValue(prompt.Trim(), out var text)
            ? Task.FromResult(new ModelResponse(text, []))
            : throw new CassetteMissException(prompt);
    }
}

public sealed class CassetteMissException(string prompt)
    : Exception($"cassette drift: no recorded response for prompt \"{prompt}\"");

/// <summary>Wraps a live model and records each (prompt, response text) pair for later replay.</summary>
public sealed class RecordingChatModel(IChatModel inner) : IChatModel
{
    public Dictionary<string, string> Recorded { get; } = new();

    public string Name => $"{inner.Name}+record";

    public async Task<ModelResponse> CompleteAsync(
        string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
    {
        var r = await inner.CompleteAsync(system, messages, tools, ct);
        Recorded[(messages.Last(m => m.Role == MessageRole.User).Text ?? "").Trim()] = r.Text;
        return r;
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(Recorded, new JsonSerializerOptions { WriteIndented = true }));
}
