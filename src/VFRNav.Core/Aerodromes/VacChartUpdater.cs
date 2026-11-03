using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VFRNav.Core.Aerodromes;

/// <param name="Changed">Cartes remplacées parce que le SIA en publie une différente.</param>
/// <param name="Added">Cartes absentes du dossier, téléchargées.</param>
/// <param name="Same">Cartes déjà à jour.</param>
/// <param name="Absent">Aérodromes dont le SIA ne publie pas de carte dans ce cycle.</param>
public sealed record VacUpdateResult(DateTime Cycle, int Changed, int Added, int Same, int Absent, bool Cancelled, bool DryRun)
{
    public string Summary =>
        $"Cycle AIRAC {Cycle:yyyy-MM-dd} : {Changed} modifiée(s), {Added} nouvelle(s), {Same} à jour, {Absent} non publiée(s) par le SIA" +
        (DryRun ? " (rien n'a été écrit)." : Cancelled ? " (arrêté avant la fin)." : ".");
}

/// <summary>
/// Met à jour le dossier des cartes VAC depuis l'eAIP du SIA : trouve le cycle AIRAC en vigueur, télécharge la carte de chaque
/// aérodrome du dossier (et, sur demande, de ceux qui y manquent) et ne remplace un fichier que si la carte publiée est différente.
/// Les cartes sont celles du SIA, conservées telles que publiées ; la date du cycle est écrite dans <c>_cycle.txt</c>.
/// Utilisation en simulation uniquement.
/// </summary>
public sealed partial class VacChartUpdater
{
    /// <summary>Une date d'entrée en vigueur AIRAC connue : les autres sont à des multiples de 28 jours.</summary>
    public static readonly DateTime ReferenceCycle = new(2026, 10, 1);

    private readonly HttpClient _http;
    private readonly TimeSpan _pause;

    /// <param name="pause">Attente entre deux téléchargements : une carte à la fois, sans solliciter le site.</param>
    public VacChartUpdater(HttpClient http, TimeSpan? pause = null)
    {
        _http = http;
        _pause = pause ?? TimeSpan.FromMilliseconds(400);
    }

    /// <summary>Date d'entrée en vigueur du cycle AIRAC en vigueur à cette date.</summary>
    public static DateTime CycleFor(DateTime date) =>
        ReferenceCycle.AddDays(Math.Floor((date.Date - ReferenceCycle).TotalDays / 28) * 28);

    public static string ChartUrl(DateTime cycle, string icao) =>
        "https://www.sia.aviation-civile.gouv.fr/media/dvd/eAIP_" +
        cycle.ToString("dd_MMM_yyyy", CultureInfo.InvariantCulture).ToUpperInvariant() +
        $"/Atlas-VAC/PDF_AIPparSSection/VAC/AD/AD-2.{icao}.pdf";

    [GeneratedRegex(@"^AD-2\.(L[FG][A-Z]{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ChartName();

    [GeneratedRegex(@"^L[FG][A-Z]{2}$")]
    private static partial Regex FrenchIcao();

    /// <summary>
    /// Codes OACI à traiter : les cartes déjà dans le dossier, plus (<paramref name="missing"/>) ceux que la liste nationale
    /// annonce avec une carte.
    /// </summary>
    public static IReadOnlyList<string> Wanted(string directory, IEnumerable<string>? missing)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(directory))
            foreach (string file in Directory.EnumerateFiles(directory, "AD-2.*.pdf"))
                if (ChartName().Match(Path.GetFileNameWithoutExtension(file)) is { Success: true } m) set.Add(m.Groups[1].Value.ToUpperInvariant());
        foreach (string icao in missing ?? [])
            if (FrenchIcao().IsMatch(icao.ToUpperInvariant())) set.Add(icao.ToUpperInvariant());
        return [.. set];
    }

    /// <summary>Vrai si le SIA publie ce cycle (testé avec la carte de LFPL, qui existe dans tous les cycles).</summary>
    public async Task<bool> IsOnlineAsync(DateTime cycle, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, ChartUrl(cycle, "LFPL"));
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    /// <exception cref="InvalidOperationException">Le cycle n'est pas (encore) en ligne.</exception>
    public async Task<VacUpdateResult> UpdateAsync(string directory, IEnumerable<string> icaos, DateTime? cycle = null, bool dryRun = false,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var effective = cycle ?? CycleFor(DateTime.UtcNow);
        if (!await IsOnlineAsync(effective, ct))
            throw new InvalidOperationException($"Le cycle AIRAC {effective:yyyy-MM-dd} n'est pas en ligne au SIA (pas encore, ou plus d'accès au site).");
        if (!dryRun) Directory.CreateDirectory(directory);

        int changed = 0, added = 0, same = 0, absent = 0, done = 0;
        var list = icaos.ToList();
        bool cancelled = false;
        foreach (string icao in list)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            progress?.Report($"Cartes VAC : {icao} ({++done}/{list.Count})");
            byte[]? bytes;
            try { bytes = await DownloadAsync(effective, icao, ct); }
            catch (OperationCanceledException) { cancelled = true; break; }
            if (bytes is null) { absent++; continue; }

            string target = Path.Combine(directory, $"AD-2.{icao}.pdf");
            if (!File.Exists(target))
            {
                added++;
                if (!dryRun) await File.WriteAllBytesAsync(target, bytes, CancellationToken.None);
            }
            else if (!SameContent(bytes, await File.ReadAllBytesAsync(target, CancellationToken.None)))
            {
                changed++;
                if (!dryRun) await File.WriteAllBytesAsync(target, bytes, CancellationToken.None);
            }
            else same++;

            if (_pause > TimeSpan.Zero)
            {
                try { await Task.Delay(_pause, ct); }
                catch (OperationCanceledException) { cancelled = true; break; }
            }
        }

        if (!dryRun && !cancelled)
            await File.WriteAllTextAsync(Path.Combine(directory, "_cycle.txt"),
                $"Source : SIA (Service de l'Information Aéronautique, DGAC), AIP France, atlas des cartes VAC, cycle AIRAC en vigueur le {effective:yyyy-MM-dd}. " +
                $"Cartes conservées telles que publiées. Mises à jour le {DateTime.Now:yyyy-MM-dd}.", new UTF8Encoding(false), CancellationToken.None);
        return new VacUpdateResult(effective, changed, added, same, absent, cancelled, dryRun);
    }

    private static bool SameContent(byte[] a, byte[] b) => SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));

    /// <summary>La carte publiée, ou null si le SIA n'en a pas (absente, ou réponse qui n'est pas un PDF).</summary>
    private async Task<byte[]?> DownloadAsync(DateTime cycle, string icao, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(ChartUrl(cycle, icao), ct);
            if (!response.IsSuccessStatusCode) return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length >= 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' ? bytes : null;
        }
        catch (HttpRequestException) { return null; }
    }
}
