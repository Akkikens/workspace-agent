using System.Text.Json.Nodes;

namespace WorkspaceAgent.Llm;

public enum MessageRole { User, Assistant, Tool }

public sealed record ToolCall(string Id, string Name, JsonObject Args);

/// <summary>Provider-neutral transcript entry. Tool results are one message per call.</summary>
public sealed record ChatMessage(MessageRole Role, string? Text = null)
{
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = [];
    public string? ToolCallId { get; init; }
    public bool IsError { get; init; }

    /// <summary>
    /// Opaque provider data for an assistant turn (for Anthropic: the content blocks, including
    /// thinking signatures, that must be echoed back unchanged). Null for scripted models.
    /// </summary>
    public object? ProviderState { get; init; }

    public static ChatMessage User(string text) => new(MessageRole.User, text);
    public static ChatMessage ToolResult(string callId, string content, bool isError = false) =>
        new(MessageRole.Tool, content) { ToolCallId = callId, IsError = isError };
}

public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema);

public sealed record ModelResponse(string Text, IReadOnlyList<ToolCall> ToolCalls, object? ProviderState = null);

/// <summary>
/// The only seam between the agent and a model. CI uses <see cref="ScriptedChatModel"/> or
/// <see cref="CassetteChatModel"/>; the Anthropic provider lives in WorkspaceAgent.Anthropic.
/// Responses are whole turns: the HTTP host streams agent events, not provider tokens.
/// </summary>
public interface IChatModel
{
    string Name { get; }

    Task<ModelResponse> CompleteAsync(
        string system,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        CancellationToken ct = default);
}
