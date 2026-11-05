using System.Net;
using System.Text.Json;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Weather;

/// <summary>
/// Prévisions Open-Meteo le long d'une route (gratuit pour un usage non commercial, sans clé) : dix points par requête,
/// et une réponse déjà lue n'est pas redemandée pendant dix minutes.
/// </summary>
public sealed class ForecastService(HttpClient http)
{
    private const int PointsPerRequest = 10;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (DateTimeOffset At, string Body)> _cache = [];

    /// <exception cref="HttpRequestException">Service injoignable.</exception>
    /// <exception cref="InvalidDataException">Réponse refusée (date hors de la période prévue, par exemple) ou illisible.</exception>
    public async Task<IReadOnlyList<PointForecast?>> ForecastAsync(IReadOnlyList<ForecastRequest> points, CancellationToken ct = default)
    {
        var result = new List<PointForecast?>();
        for (int i = 0; i < points.Count; i += PointsPerRequest)
        {
            var chunk = points.Skip(i).Take(PointsPerRequest).ToList();
            result.AddRange(OpenMeteoForecast.Parse(await GetAsync(OpenMeteoForecast.Url(chunk), ct), chunk));
        }
        return result;
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        lock (_cache)
            if (_cache.TryGetValue(url, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheLifetime) return hit.Body;

        using var response = await http.GetAsync(url, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new HttpRequestException(Loc.Get("Forecast_Trop_de_requetes"));
            throw new InvalidDataException(Reason(body) ?? $"HTTP {(int)response.StatusCode}");
        }
        lock (_cache) _cache[url] = (DateTimeOffset.UtcNow, body);
        return body;
    }

    private static string? Reason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
