namespace VFRNav.Core.Services;

public sealed class WeatherService(HttpClient http, TimeProvider? clock = null)
{
    private const string Base = "https://aviationweather.gov/api/data";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, (DateTimeOffset At, string Metar, string Taf)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<(string Metar, string Taf)> GetAsync(IEnumerable<string> icaoIds, CancellationToken ct = default)
    {
        string ids = string.Join(',', icaoIds.Distinct());
        var metar = http.GetStringAsync($"{Base}/metar?ids={ids}&format=raw", ct);
        var taf = http.GetStringAsync($"{Base}/taf?ids={ids}&format=raw", ct);
        await Task.WhenAll(metar, taf);
        return (metar.Result.Trim(), taf.Result.Trim());
    }

    private readonly Dictionary<string, (DateTimeOffset At, string Metar)> _boxCache = [];

    /// <summary>
    /// Tous les METAR d'une région (un seul appel). La région est arrondie au degré pour que de petits déplacements
    /// de la carte réutilisent la réponse ; elle est gardée 10 minutes.
    /// </summary>
    public async Task<string> GetMetarsInBoxAsync(double minLat, double minLon, double maxLat, double maxLon, CancellationToken ct = default)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double a = Math.Floor(minLat), b = Math.Floor(minLon), c = Math.Ceiling(maxLat), d = Math.Ceiling(maxLon);
        string box = string.Create(inv, $"{a},{b},{c},{d}");
        if (_boxCache.TryGetValue(box, out var hit) && _clock.GetUtcNow() - hit.At < CacheLifetime) return hit.Metar;
        string text = (await http.GetStringAsync($"{Base}/metar?bbox={box}&format=raw", ct)).Trim();
        _boxCache[box] = (_clock.GetUtcNow(), text);
        return text;
    }

    /// <summary>Météo d'un seul terrain, gardée 10 minutes : survoler la carte ne doit pas multiplier les requêtes.</summary>
    public async Task<(string Metar, string Taf)> GetCachedAsync(string icao, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(icao, out var hit) && _clock.GetUtcNow() - hit.At < CacheLifetime) return (hit.Metar, hit.Taf);
        var (metar, taf) = await GetAsync([icao], ct);
        _cache[icao] = (_clock.GetUtcNow(), metar, taf);
        return (metar, taf);
    }
}
