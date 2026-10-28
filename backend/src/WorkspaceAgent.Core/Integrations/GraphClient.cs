using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace WorkspaceAgent.Integrations;

public sealed record CalendarEvent(string Subject, string Start, string End, string? Organizer, string? Location);
public sealed record MailMessage(string Subject, string? From, string? Received, string Preview);
public sealed record Person(string DisplayName, string? Email, string? JobTitle, string? Department);

/// <summary>
/// Read-only Microsoft Graph v1.0 client for the signed-in user: /me/calendarView, /me/messages, /me/people.
/// Only the first page is read ($top caps it); @odata.nextLink is not followed.
/// </summary>
public sealed class GraphClient(HttpClient http, GraphOptions options, IGraphTokenProvider tokens)
{
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<CalendarEvent>> CalendarViewAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        if (end <= start) throw new ArgumentException("end must be after start");
        var q = $"startDateTime={Iso(start)}&endDateTime={Iso(end)}&$select=subject,start,end,organizer,location&$orderby=start/dateTime&$top=50";
        var json = await GetAsync($"/me/calendarView?{q}", ct, ("Prefer", "outlook.timezone=\"UTC\""));
        return Values(json).Select(v => new CalendarEvent(
            v["subject"]?.GetValue<string>() ?? "",
            v["start"]?["dateTime"]?.GetValue<string>() ?? "",
            v["end"]?["dateTime"]?.GetValue<string>() ?? "",
            v["organizer"]?["emailAddress"]?["name"]?.GetValue<string>(),
            v["location"]?["displayName"]?.GetValue<string>())).ToList();
    }

    public async Task<IReadOnlyList<MailMessage>> ListMessagesAsync(string? search, int top, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 25);
        var q = $"$select=subject,from,receivedDateTime,bodyPreview&$top={top}";
        // $search cannot be combined with $orderby on messages; results come back by relevance.
        q += string.IsNullOrWhiteSpace(search)
            ? "&$orderby=receivedDateTime desc"
            : $"&$search={Uri.EscapeDataString("\"" + search.Replace("\"", "") + "\"")}";
        var json = await GetAsync($"/me/messages?{q}", ct);
        return Values(json).Select(v => new MailMessage(
            v["subject"]?.GetValue<string>() ?? "",
            v["from"]?["emailAddress"]?["address"]?.GetValue<string>(),
            v["receivedDateTime"]?.GetValue<string>(),
            v["bodyPreview"]?.GetValue<string>() ?? "")).ToList();
    }

    public async Task<IReadOnlyList<Person>> FindPeopleAsync(string query, int top, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 25);
        var q = $"$search={Uri.EscapeDataString("\"" + query.Replace("\"", "") + "\"")}&$top={top}&$select=displayName,scoredEmailAddresses,jobTitle,department";
        var json = await GetAsync($"/me/people?{q}", ct);
        return Values(json).Select(v => new Person(
            v["displayName"]?.GetValue<string>() ?? "",
            (v["scoredEmailAddresses"] as JsonArray)?.FirstOrDefault()?["address"]?.GetValue<string>(),
            v["jobTitle"]?.GetValue<string>(),
            v["department"]?.GetValue<string>())).ToList();
    }

    private static string Iso(DateTimeOffset t) => Uri.EscapeDataString(t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z");

    private static IEnumerable<JsonNode> Values(JsonNode json) => (json["value"] as JsonArray ?? []).Where(v => v is not null)!;

    private async Task<JsonNode> GetAsync(string pathAndQuery, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        var token = await tokens.GetTokenAsync(ct);
        var url = options.BaseUrl.TrimEnd('/') + pathAndQuery;
        using var resp = await HttpSupport.SendWithRetryAsync(http, () =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new("application/json"));
            foreach (var (n, v) in headers) req.Headers.TryAddWithoutValidation(n, v);
            return req;
        }, MaxRetryDelay, ct);
        return await HttpSupport.ReadJsonOrThrowAsync(resp, "graph", ct);
    }
}
