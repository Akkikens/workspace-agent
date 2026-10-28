using System.Text.Json;
using System.Text.Json.Nodes;
using global::Anthropic;
using global::Anthropic.Models.Messages;
using WorkspaceAgent.Llm;
using ChatMessage = WorkspaceAgent.Llm.ChatMessage;

namespace WorkspaceAgent.Providers;

/// <summary>
/// <see cref="IChatModel"/> over the official Anthropic C# SDK (Messages API, client-side tools).
/// Credentials come from the environment (ANTHROPIC_API_KEY). Not exercised in CI: CI has no key.
/// Assistant turns keep their content blocks (including thinking signatures) in ProviderState so they can
/// be echoed back unchanged on the next request.
/// </summary>
public sealed class AnthropicChatModel(string model = "claude-opus-5-5", int maxTokens = 16000) : IChatModel
{
    private readonly AnthropicClient _client = new();

    public string Name => $"anthropic:{model}";

    public async Task<ModelResponse> CompleteAsync(
        string system, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, CancellationToken ct = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxTokens,
            System = system,
            Messages = ToParams(messages),
            Tools = tools.Select(ToTool).Select(t => (ToolUnion)t).ToList(),
        };
        Message response = await _client.Messages.Create(parameters, cancellationToken: ct);

        if (response.StopReason == StopReason.Refusal)
            throw new InvalidOperationException("the model declined this request (stop_reason: refusal)");

        var text = new List<string>();
        var calls = new List<ToolCall>();
        var echo = new List<ContentBlockParam>();
        foreach (ContentBlock block in response.Content)
        {
            if (block.TryPickText(out TextBlock? t))
            {
                text.Add(t.Text);
                echo.Add(new TextBlockParam { Text = t.Text });
            }
            else if (block.TryPickThinking(out ThinkingBlock? th))
                echo.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? rt))
                echo.Add(new RedactedThinkingBlockParam { Data = rt.Data });
            else if (block.TryPickToolUse(out ToolUseBlock? tu))
            {
                echo.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                var args = JsonSerializer.SerializeToNode(tu.Input) as JsonObject ?? new JsonObject();
                calls.Add(new ToolCall(tu.ID, tu.Name, args));
            }
        }
        return new ModelResponse(string.Join("\n", text), calls, echo);
    }

    private static Tool ToTool(ToolSpec spec) => new()
    {
        Name = spec.Name,
        Description = spec.Description,
        InputSchema = new()
        {
            Properties = (spec.InputSchema["properties"] as JsonObject ?? [])
                .ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
            Required = (spec.InputSchema["required"] as JsonArray ?? []).Select(n => n!.GetValue<string>()).ToList(),
        },
    };

    private static List<MessageParam> ToParams(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<MessageParam>();
        List<ContentBlockParam>? toolResults = null;

        void FlushToolResults()
        {
            if (toolResults is null) return;
            result.Add(new UserMessageParam { Content = toolResults });
            toolResults = null;
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.Tool:
                    toolResults ??= [];
                    toolResults.Add(new ToolResultBlockParam { ToolUseID = m.ToolCallId!, Content = m.Text ?? "", IsError = m.IsError });
                    break;
                case MessageRole.User:
                    FlushToolResults();
                    result.Add(new UserMessageParam { Content = m.Text ?? "" });
                    break;
                case MessageRole.Assistant:
                    FlushToolResults();
                    if (m.ProviderState is List<ContentBlockParam> blocks)
                        result.Add(new AssistantMessageParam { Content = blocks });
                    else
                    {
                        var content = new List<ContentBlockParam>();
                        if (!string.IsNullOrEmpty(m.Text)) content.Add(new TextBlockParam { Text = m.Text });
                        foreach (var c in m.ToolCalls)
                            content.Add(new ToolUseBlockParam
                            {
                                ID = c.Id,
                                Name = c.Name,
                                Input = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(c.Args.ToJsonString())!,
                            });
                        result.Add(new AssistantMessageParam { Content = content });
                    }
                    break;
            }
        }
        FlushToolResults();
        return result;
    }
}
