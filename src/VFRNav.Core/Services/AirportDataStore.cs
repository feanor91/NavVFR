using System.Globalization;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <param name="Airports">Terrains du monde, pistes comprises.</param>
/// <param name="DownloadedAt">Date du téléchargement des fichiers.</param>
/// <param name="FromCache">Vrai si les fichiers viennent du disque, sans téléchargement.</param>
/// <param name="Note">Message à montrer (téléchargement impossible, pistes absentes...), ou null.</param>
public sealed record WorldAirportData(IReadOnlyList<Airport> Airports, DateTimeOffset DownloadedAt, bool FromCache, string? Note);

/// <summary>
/// Fichiers mondiaux d'OurAirports (terrains : 12,7 Mo, pistes : 3,9 Mo) téléchargés une fois et gardés dans <c>%AppData%\VFRNav\airports</c>,
/// refaits au bout d'une semaine ou à la demande. Sans réseau, les fichiers déjà là, même anciens, servent quand même.
/// </summary>
public sealed class AirportDataStore(HttpClient http, string? directory = null, TimeProvider? clock = null)
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VFRNav", "airports");

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private readonly string _directory = directory ?? DefaultDirectory;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string AirportsPath => Path.Combine(_directory, "airports.csv");
    private string RunwaysPath => Path.Combine(_directory, "runways.csv");
    private string StampPath => Path.Combine(_directory, "downloaded.txt");

    /// <summary>Date du téléchargement des fichiers en cache ; null s'il n'y en a pas.</summary>
    public DateTimeOffset? CachedAt =>
        File.Exists(AirportsPath) && File.Exists(StampPath)
        && DateTimeOffset.TryParse(File.ReadAllText(StampPath), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    public async Task<WorldAirportData> LoadAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var cached = CachedAt;
        bool fresh = cached is { } at && _clock.GetUtcNow() - at < MaxAge;
        string? note = null;

        if (forceRefresh || !fresh)
        {
            try
            {
                await DownloadAsync(ct);
                cached = CachedAt;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // Sans réseau, des fichiers anciens valent mieux que rien.
                if (cached is null) throw;
                note = $"Téléchargement impossible ({ex.Message}) : terrains du {cached.Value.LocalDateTime:dd/MM/yyyy} gardés en cache.";
            }
        }

        var (airports, runwayNote) = await Task.Run(ReadCache, ct);
        bool downloadedNow = note is null && (forceRefresh || !fresh);
        return new WorldAirportData(airports, cached ?? _clock.GetUtcNow(), !downloadedNow, note ?? runwayNote);
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        // Chaque fichier arrive dans un fichier temporaire : une coupure ne laisse pas un fichier tronqué à la place du bon.
        await DownloadFileAsync(OurAirportsService.Url, AirportsPath, ct);
        try { await DownloadFileAsync(OurAirportsService.RunwaysUrl, RunwaysPath, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or TaskCanceledException or IOException))
        {
            // Les pistes sont un plus : sans elles, les filtres de piste ne retiennent rien, mais les terrains restent utilisables.
        }
        await File.WriteAllTextAsync(StampPath, _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), ct);
    }

    private async Task DownloadFileAsync(string url, string path, CancellationToken ct)
    {
        string temp = path + ".part";
        await using (var source = await http.GetStreamAsync(url, ct))
        await using (var target = File.Create(temp))
            await source.CopyToAsync(target, ct);
        File.Move(temp, path, overwrite: true);
    }

    private (IReadOnlyList<Airport> Airports, string? Note) ReadCache()
    {
        IReadOnlyList<Airport> airports;
        using (var reader = new StreamReader(AirportsPath)) airports = OurAirportsService.ParseWorld(reader);

        if (!File.Exists(RunwaysPath)) return (airports, "Pistes indisponibles : les filtres de longueur et de revêtement ne retiennent aucun terrain.");
        using var runways = new StreamReader(RunwaysPath);
        return (OurAirportsService.WithRunways(airports, OurAirportsService.ParseRunways(runways)), null);
    }
}
