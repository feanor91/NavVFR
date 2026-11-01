using System.Text.Json;

namespace VFRNav.Core.Services;

public sealed record Notam(string Id, string Location, string Text, string? Start, string? End);

/// <summary>
/// NOTAM d'un terrain via l'ICAO API Data Service (<c>notams-realtime-list</c>, clé gratuite, 100 appels offerts).
/// Les résultats sont gardés 30 minutes par terrain pour ne pas gaspiller le quota.
/// </summary>
public sealed class NotamService(HttpClient http, TimeProvider? clock = null)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<Notam> Items)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<Notam>> GetAsync(string icao, string apiKey, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(icao, out var hit) && _clock.GetUtcNow() - hit.At < CacheLifetime) return hit.Items;

        string url = $"https://applications.icao.int/dataservices/api/notams-realtime-list?api_key={Uri.EscapeDataString(apiKey)}&format=json&locations={Uri.EscapeDataString(icao)}";
        using var response = await http.GetAsync(url, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ICAO API Data Service : HTTP {(int)response.StatusCode}. {Truncate(body, 200)}");

        var items = Parse(body, icao);
        _cache[icao] = (_clock.GetUtcNow(), items);
        return items;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly string[] IdKeys = ["id", "Id", "notamId", "NotamId", "number", "Number", "key"];
    private static readonly string[] TextKeys = ["message", "Message", "all", "text", "Text", "e", "E", "NotamText", "notamText"];
    private static readonly string[] StartKeys = ["startdate", "startDate", "StartDate", "start", "Start", "from", "From", "b", "B"];
    private static readonly string[] EndKeys = ["enddate", "endDate", "EndDate", "end", "End", "to", "To", "c", "C"];

    /// <summary>Lecture tolérante : liste JSON directe ou objet contenant une liste. Un champ inconnu n'est jamais inventé.</summary>
    public static IReadOnlyList<Notam> Parse(string json, string location)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement list = root;
        if (root.ValueKind == JsonValueKind.Object)
        {
            var array = root.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);
            if (array.Value.ValueKind != JsonValueKind.Array) return [];
            list = array.Value;
        }
        if (list.ValueKind != JsonValueKind.Array) return [];

        var result = new List<Notam>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string text = First(item, TextKeys) ?? item.ToString();
            result.Add(new Notam(First(item, IdKeys) ?? "", First(item, ["location", "Location", "icao"]) ?? location,
                text.Trim(), First(item, StartKeys), First(item, EndKeys)));
        }
        return result;
    }

    private static string? First(JsonElement item, string[] keys)
    {
        foreach (string key in keys)
            if (item.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                string s = v.ToString();
                if (s.Length > 0) return s;
            }
        return null;
    }
}
