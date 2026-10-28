using System.Text.Json;
using WorkspaceAgent.Agent;
using WorkspaceAgent.Identity;
using WorkspaceAgent.Integrations;
using WorkspaceAgent.Llm;
using WorkspaceAgent.Providers;
using WorkspaceAgent.Sql;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton(config.GetSection("Atlassian").Get<AtlassianOptions>() ?? new AtlassianOptions());
builder.Services.AddSingleton(config.GetSection("Graph").Get<GraphOptions>() ?? new GraphOptions());
builder.Services.AddSingleton<IGraphTokenProvider>(new StaticGraphToken(config["Graph:AccessToken"] ?? "stub-token"));
builder.Services.AddHttpClient<JiraClient>();
builder.Services.AddHttpClient<ConfluenceClient>();
builder.Services.AddHttpClient<GraphClient>();

builder.Services.AddSingleton(SemanticLayer.Default);
builder.Services.AddSingleton<SqlGovernor>();
builder.Services.AddSingleton(_ =>
{
    var path = config["Warehouse:Path"] ?? Path.Combine(Path.GetTempPath(), "workspace-agent", $"warehouse-{Environment.ProcessId}.db");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    return Warehouse.CreateSeeded(path);
});

// Model: the scripted demo model by default; Claude when WORKSPACE_AGENT_MODEL=anthropic (needs ANTHROPIC_API_KEY).
builder.Services.AddSingleton<IChatModel>(_ =>
    (config["WORKSPACE_AGENT_MODEL"] ?? "scripted").ToLowerInvariant() switch
    {
        "anthropic" => new AnthropicChatModel(config["ANTHROPIC_MODEL"] ?? "claude-opus-5-5"),
        _ => ScriptedChatModel.FromFile(config["Model:ScriptPath"] ?? Path.Combine(AppContext.BaseDirectory, "demo", "demo-script.json")),
    });

builder.Services.AddTransient<WorkspaceTools>();
builder.Services.AddTransient<AgentRunner>();
builder.Services.AddTransient<CodeQa>();
builder.Services.AddSingleton<SessionStore>();

var app = builder.Build();
app.UseCors();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

UserContext? CurrentUser(HttpRequest req) => DemoUsers.Find(req.Headers["X-Demo-User"].FirstOrDefault());

async Task StreamAsync(HttpContext ctx, string sessionId, Func<EmitEvent, Task> run)
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    EmitEvent emit = async (type, data) =>
    {
        await ctx.Response.WriteAsync($"event: {type}\ndata: {JsonSerializer.Serialize(data, json)}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    };
    await emit("session", new { sessionId });
    await run(emit);
}

app.MapGet("/api/health", (IChatModel model) => Results.Ok(new { status = "ok", model = model.Name }));

app.MapGet("/api/users", (HttpRequest req) =>
    CurrentUser(req) is null
        ? Results.Unauthorized()
        : Results.Ok(DemoUsers.All.Select(u => new { id = u.Id, name = u.Name, role = u.RoleName, businessUnit = u.BusinessUnit })));

app.MapPost("/api/chat", async (HttpContext ctx, ChatRequest body, SessionStore sessions, AgentRunner runner) =>
{
    if (CurrentUser(ctx.Request) is not { } user) { ctx.Response.StatusCode = 401; return; }
    if (string.IsNullOrWhiteSpace(body.Message)) { ctx.Response.StatusCode = 400; return; }
    AgentSession session;
    try { session = sessions.GetOrCreate(body.SessionId, user); }
    catch (UnauthorizedAccessException) { ctx.Response.StatusCode = 403; return; }

    await session.Lock.WaitAsync(ctx.RequestAborted);
    try { await StreamAsync(ctx, session.Id, emit => runner.RunAsync(session, body.Message, emit, ctx.RequestAborted)); }
    finally { session.Lock.Release(); }
});

app.MapPost("/api/chat/{sessionId}/approvals/{callId}", async (HttpContext ctx, string sessionId, string callId, ApprovalRequest body,
    SessionStore sessions, AgentRunner runner) =>
{
    if (CurrentUser(ctx.Request) is not { } user) { ctx.Response.StatusCode = 401; return; }
    var session = sessions.Find(sessionId);
    if (session is null) { ctx.Response.StatusCode = 404; return; }
    if (session.User.Id != user.Id) { ctx.Response.StatusCode = 403; return; }

    await session.Lock.WaitAsync(ctx.RequestAborted);
    try { await StreamAsync(ctx, session.Id, emit => runner.ResumeAsync(session, callId, body.Approved, emit, ctx.RequestAborted)); }
    finally { session.Lock.Release(); }
});

app.MapPost("/api/code/ask", async (HttpContext ctx, CodeAskRequest body, CodeQa qa) =>
{
    if (CurrentUser(ctx.Request) is null) { ctx.Response.StatusCode = 401; return; }
    if (string.IsNullOrWhiteSpace(body.Question)) { ctx.Response.StatusCode = 400; return; }
    await StreamAsync(ctx, Guid.NewGuid().ToString("n")[..12],
        emit => qa.AskAsync(body.Question, body.Chunks ?? [], emit, ctx.RequestAborted));
});

app.Run();

public sealed record ChatRequest(string? SessionId, string Message);
public sealed record ApprovalRequest(bool Approved);
public sealed record CodeAskRequest(string Question, List<CodeChunk>? Chunks);

public partial class Program;
