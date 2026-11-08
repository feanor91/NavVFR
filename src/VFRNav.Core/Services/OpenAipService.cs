using System.Text.Json;
using VFRNav.Core.Models;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Services;

/// <summary>Espaces aériens via l'API OpenAIP (clé gratuite requise).</summary>
public sealed class OpenAipService(HttpClient http)
{
    private static readonly string[] Classes = ["A", "B", "C", "D", "E", "F", "G"];

    public async Task<IReadOnlyList<Airspace>> LoadAsync(string country, string apiKey, CancellationToken ct = default) =>
        Parse(await FetchItemsAsync(country, apiKey, ct));

    /// <summary>
    /// Comme <see cref="LoadAsync"/>, avec un cache sur disque par pays (<c>FR.json.gz</c>) : tant que le fichier a moins de <paramref name="maxAge"/>,
    /// les zones sont relues du disque au lieu de plusieurs requêtes à OpenAIP. <paramref name="refresh"/> force le téléchargement.
    /// </summary>
    public async Task<IReadOnlyList<Airspace>> LoadCachedAsync(string country, string apiKey, string cacheDirectory, TimeSpan maxAge, bool refresh = false, CancellationToken ct = default)
    {
        string file = Path.Combine(cacheDirectory, country.ToUpperInvariant() + ".json.gz");
        bool exists = File.Exists(file);
        if (!refresh && exists && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < maxAge)
            if (await ReadCacheAsync(file, ct) is { } cached) return cached;

        string json;
        try { json = await FetchItemsAsync(country, apiKey, ct); }
        catch (Exception ex) when (exists && !ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Hors ligne ou OpenAIP en panne : les zones changent rarement, un cache périmé vaut mieux que rien.
            if (await ReadCacheAsync(file, ct) is { } stale) return stale;
            throw;
        }
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            string temporary = file + ".tmp";
            await using (var gz = new System.IO.Compression.GZipStream(File.Create(temporary), System.IO.Compression.CompressionLevel.Fastest))
            await using (var writer = new StreamWriter(gz)) await writer.WriteAsync(json);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* le cache est facultatif */ }
        return Parse(json);
    }

    private static async Task<IReadOnlyList<Airspace>?> ReadCacheAsync(string file, CancellationToken ct)
    {
        try
        {
            await using var gz = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            return Parse(await reader.ReadToEndAsync(ct));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { return null; }   // cache abîmé
    }

    /// <summary>Toutes les zones d'un pays, telles que les donne OpenAIP, en un tableau JSON.</summary>
    private async Task<string> FetchItemsAsync(string country, string apiKey, CancellationToken ct)
    {
        var raw = new List<string>();
        int page = 1, totalPages = 1;
        while (page <= totalPages && page <= 30)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.core.openaip.net/api/airspaces?country={country}&limit=500&page={page}");
            req.Headers.Add("x-openaip-api-key", apiKey);
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("totalPages", out var tp)) totalPages = tp.GetInt32();
            if (root.TryGetProperty("items", out var items))
                foreach (var item in items.EnumerateArray()) raw.Add(item.GetRawText());
            page++;
        }
        return "[" + string.Join(",", raw) + "]";
    }

    /// <summary>Zones d'un tableau JSON d'éléments OpenAIP (celui de <see cref="FetchItemsAsync"/>).</summary>
    public static IReadOnlyList<Airspace> Parse(string itemsJson)
    {
        var result = new List<Airspace>();
        using var doc = JsonDocument.Parse(itemsJson);
        foreach (var item in doc.RootElement.EnumerateArray())
            if (TryParse(item) is { } a) result.Add(a);
        return result;
    }

    private static Airspace? TryParse(JsonElement e)
    {
        if (!e.TryGetProperty("geometry", out var g) || g.ValueKind != JsonValueKind.Object) return null;
        var rings = new List<IReadOnlyList<(double Lon, double Lat)>>();
        string type = g.GetProperty("type").GetString() ?? "";
        var coords = g.GetProperty("coordinates");
        if (type == "Polygon") AddOuterRing(coords, rings);
        else if (type == "MultiPolygon") foreach (var poly in coords.EnumerateArray()) AddOuterRing(poly, rings);
        else return null;

        int cls = e.TryGetProperty("icaoClass", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
        int typeCode = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        var kind = Airspace.KindFromOpenAipType(typeCode);
        var lower = Limit(e, "lowerLimit", 0);
        var upper = Limit(e, "upperLimit", double.MaxValue);
        return new Airspace(e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            cls >= 0 && cls < Classes.Length ? Classes[cls] : "", lower.Text, upper.Text, rings, kind, lower.Feet, upper.Feet, typeCode, Details(e));
    }

    private static readonly Localized<(string Property, string Text)[]> FlagPropertiesByLanguage = new(() => [
        ("byNotam", Loc.Get("OpenAipService_Activee_par_NOTAM")),
        ("onDemand", Loc.Get("OpenAip_On_demand")),
        ("onRequest", Loc.Get("OpenAipService_Sur_requete")),
        ("specialAgreement", Loc.Get("OpenAipService_Accord_special_requis")),
        ("requestCompliance", Loc.Get("OpenAipService_Se_conformer_aux_instructions"))
    ]);
    private static (string Property, string Text)[] FlagProperties => FlagPropertiesByLanguage.Value;

    /// <summary>Remarques, conditions d'accès et fréquences : tous ces champs sont facultatifs dans l'API.</summary>
    private static AirspaceDetails Details(JsonElement e)
    {
        string? remarks = e.TryGetProperty("remarks", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var flags = FlagProperties
            .Where(f => e.TryGetProperty(f.Property, out var v) && v.ValueKind == JsonValueKind.True)
            .Select(f => f.Text)
            .ToList();
        var freqs = new List<AirspaceFrequency>();
        if (e.TryGetProperty("frequencies", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var f in list.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object || !f.TryGetProperty("value", out var value)) continue;
                string number = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
                if (number.Length == 0) continue;
                string name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                freqs.Add(new AirspaceFrequency(name, number));
            }
        return new AirspaceDetails(remarks, flags, freqs);
    }

    private static void AddOuterRing(JsonElement polygon, List<IReadOnlyList<(double Lon, double Lat)>> rings)
    {
        var outer = polygon.EnumerateArray().First();
        rings.Add(outer.EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList());
    }

    private static (string Text, double Feet) Limit(JsonElement e, string name, double missingFeet)
    {
        if (!e.TryGetProperty(name, out var l) || l.ValueKind != JsonValueKind.Object) return ("", missingFeet);
        int value = l.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
        int unit = l.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : 1;
        int datum = l.TryGetProperty("referenceDatum", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : 0;
        return Airspace.FormatLimit(value, unit, datum);
    }
}
