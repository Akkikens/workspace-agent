using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace WorkspaceAgent.Integrations;

/// <summary>Jira Cloud and Confluence Cloud share a site URL and basic auth (account email + API token).</summary>
public sealed class AtlassianOptions
{
    /// <summary>e.g. https://your-site.atlassian.net (the demo points this at the local stub server).</summary>
    public string BaseUrl { get; set; } = "http://localhost:5090";
    public string Email { get; set; } = "demo@example.com";
    public string ApiToken { get; set; } = "stub-token";
}

public sealed class GraphOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5090/graph/v1.0";
}

/// <summary>
/// Supplies a delegated Microsoft Graph access token. Production would use MSAL (on-behalf-of the signed-in
/// user, scopes Calendars.Read Mail.Read People.Read); the demo uses a static token against the stub.
/// </summary>
public interface IGraphTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken ct);
}

public sealed class StaticGraphToken(string token) : IGraphTokenProvider
{
    public Task<string> GetTokenAsync(CancellationToken ct) => Task.FromResult(token);
}

public sealed class IntegrationException(string service, HttpStatusCode? status, string message)
    : Exception($"{service} error{(status is null ? "" : $" {(int)status}")}: {message}")
{
    public string Service { get; } = service;
    public HttpStatusCode? Status { get; } = status;
}

internal static class HttpSupport
{
    public static AuthenticationHeaderValue Basic(string user, string secret) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));

    /// <summary>
    /// Sends with one retry on 429/503, honoring Retry-After (capped at <paramref name="maxDelay"/>).
    /// The request factory is called per attempt because an HttpRequestMessage cannot be resent.
    /// </summary>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient http, Func<HttpRequestMessage> factory, TimeSpan maxDelay, CancellationToken ct)
    {
        var resp = await http.SendAsync(factory(), ct);
        if (resp.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)) return resp;

        var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
        if (delay > maxDelay) delay = maxDelay;
        resp.Dispose();
        await Task.Delay(delay, ct);
        return await http.SendAsync(factory(), ct);
    }

    public static async Task<JsonNode> ReadJsonOrThrowAsync(HttpResponseMessage resp, string service, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new IntegrationException(service, resp.StatusCode, DescribeError(body));
        try { return JsonNode.Parse(body) ?? throw new IntegrationException(service, resp.StatusCode, "empty response"); }
        catch (System.Text.Json.JsonException) { throw new IntegrationException(service, resp.StatusCode, "response was not JSON"); }
    }

    /// <summary>Extracts a readable message from Atlassian ({errorMessages, errors}) or Graph ({error:{code,message}}) bodies.</summary>
    private static string DescribeError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            if (n?["error"] is JsonObject g) return $"{g["code"]}: {g["message"]}";
            var parts = new List<string>();
            if (n?["errorMessages"] is JsonArray msgs) parts.AddRange(msgs.Select(m => m?.ToString() ?? ""));
            if (n?["errors"] is JsonObject errs) parts.AddRange(errs.Select(kv => $"{kv.Key}: {kv.Value}"));
            if (n?["message"] is JsonValue m1) parts.Add(m1.ToString());
            if (parts.Count > 0) return string.Join("; ", parts);
        }
        catch (System.Text.Json.JsonException) { }
        return body.Length > 200 ? body[..200] : body;
    }
}
