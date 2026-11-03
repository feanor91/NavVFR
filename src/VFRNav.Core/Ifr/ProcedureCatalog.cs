using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VFRNav.Core.Ifr;

/// <summary>Ce que l'index sait d'un terrain : quand ses procédures ont été lues, combien il y en a, et leur empreinte (pour voir ce qui change d'une mise à jour à l'autre).</summary>
public sealed record CatalogAirport(DateTimeOffset ReadAt, int Procedures, string Hash);

/// <param name="SimVersion">Version du simulateur qui a fourni les données, ex. « MSFS 1.5.4.0 ».</param>
public sealed record CatalogIndex(int Version, string Source, string SimVersion, DateTimeOffset ReadAt, Dictionary<string, CatalogAirport> Airports)
{
    public const int CurrentVersion = 3;
    public int ProcedureCount => Airports.Values.Sum(a => a.Procedures);
}

/// <summary>Ce qu'a changé une mise à jour.</summary>
public sealed record CatalogUpdate(int New, int Updated, int Unchanged, int Removed)
{
    public int Changed => New + Updated + Removed;
}

public enum CatalogChange { New, Updated, Unchanged }

/// <summary>
/// Dossier de SID/STAR, un fichier compressé par terrain (<c>LFPG.json.gz</c>) et un index. Les procédures du monde entier pèsent
/// plusieurs dizaines de Mo : un fichier par terrain permet de ne lire que le départ et l'arrivée d'un vol.
/// </summary>
public sealed class ProcedureCatalog
{
    public const string IndexName = "index.json";
    public const string Source = "MSFS (SimConnect)";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private CatalogIndex? _index;
    private bool _indexRead;

    public ProcedureCatalog(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>Dossier de l'utilisateur : %AppData%\VFRNav\procedures.</summary>
    public static string UserDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VFRNav", "procedures");

    /// <summary>Dossier livré avec l'application, à côté de l'exécutable.</summary>
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "data", "procedures");

    /// <summary>Index du catalogue, ou null s'il n'existe pas ou est inutilisable (<see cref="Problem"/> dit pourquoi).</summary>
    public CatalogIndex? Index
    {
        get
        {
            lock (_gate)
            {
                if (!_indexRead) ReadIndex();
                return _index;
            }
        }
    }

    public string? Problem
    {
        get
        {
            lock (_gate)
            {
                if (!_indexRead) ReadIndex();
                return _problem;
            }
        }
    }

    private string? _problem;

    private string IndexPath => Path.Combine(Directory, IndexName);
    private string FilePath(string icao) => Path.Combine(Directory, Normalize(icao) + ".json.gz");

    private static string Normalize(string icao) => icao.Trim().ToUpperInvariant();

    private static bool Valid(string icao) => icao.Length is >= 2 and <= 8 && icao.All(char.IsAsciiLetterOrDigit);

    private void ReadIndex()
    {
        _indexRead = true;
        _problem = null;
        _index = null;
        if (!File.Exists(IndexPath)) return;
        try
        {
            var index = JsonSerializer.Deserialize<CatalogIndex>(File.ReadAllText(IndexPath), Options);
            if (index?.Airports is null) _problem = "index vide";
            else if (index.Version != CatalogIndex.CurrentVersion) _problem = $"version {index.Version} non prise en charge";
            else _index = index with { Airports = new Dictionary<string, CatalogAirport>(index.Airports, StringComparer.OrdinalIgnoreCase) };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _problem = ex.Message;
        }
    }

    public bool Has(string icao)
    {
        lock (_gate) return Index?.Airports.ContainsKey(Normalize(icao)) == true;
    }

    /// <summary>Copie de l'index des terrains, sans risque si une lecture du monde écrit en même temps.</summary>
    public IReadOnlyDictionary<string, CatalogAirport> Snapshot()
    {
        lock (_gate) return new Dictionary<string, CatalogAirport>(Index?.Airports ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Procédures et points d'un terrain, ou null s'il n'est pas dans le catalogue ou si son fichier est abîmé.</summary>
    public SimProcedureImport? Read(string icao)
    {
        if (!Has(icao) || !File.Exists(FilePath(icao))) return null;
        try
        {
            using var file = File.OpenRead(FilePath(icao));
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var import = JsonSerializer.Deserialize<SimProcedureImport>(gzip, Options);
            return import?.Procedures is null || import.Fixes is null ? null : Clean(import);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Enregistre un terrain, et dit s'il est nouveau, modifié ou identique à ce que le catalogue avait déjà (dans ce dernier cas rien n'est réécrit).</summary>
    public CatalogChange Write(string icao, SimProcedureImport import, DateTimeOffset readAt)
    {
        icao = Normalize(icao);
        if (!Valid(icao)) throw new ArgumentException("Code de terrain invalide : " + icao, nameof(icao));
        lock (_gate) return WriteLocked(icao, import, readAt);
    }

    private CatalogChange WriteLocked(string icao, SimProcedureImport import, DateTimeOffset readAt)
    {
        var index = EnsureIndex();
        string hash = Hash(import);
        int procedures = import.Procedures.Count;

        CatalogChange change = !index.Airports.TryGetValue(icao, out var previous) ? CatalogChange.New
            : previous.Hash == hash && File.Exists(FilePath(icao)) ? CatalogChange.Unchanged : CatalogChange.Updated;

        if (change != CatalogChange.Unchanged)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temporary = FilePath(icao) + ".tmp";
            using (var file = File.Create(temporary))
            using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
                JsonSerializer.Serialize(gzip, import, Options);
            File.Move(temporary, FilePath(icao), overwrite: true);
        }
        index.Airports[icao] = new CatalogAirport(change == CatalogChange.Unchanged ? previous!.ReadAt : readAt, procedures, hash);
        return change;
    }

    /// <summary>Retire un terrain dont MSFS n'a plus de procédures.</summary>
    public bool Remove(string icao)
    {
        icao = Normalize(icao);
        if (!Valid(icao)) return false;
        lock (_gate)
        {
            bool known = EnsureIndex().Airports.Remove(icao);
            if (File.Exists(FilePath(icao))) File.Delete(FilePath(icao));
            return known;
        }
    }

    /// <summary>Écrit l'index (par un fichier temporaire). À appeler après une série d'écritures, et de temps en temps pendant une longue lecture.</summary>
    public void SaveIndex(string simVersion, DateTimeOffset readAt)
    {
        lock (_gate)
        {
            var index = EnsureIndex() with { SimVersion = simVersion, ReadAt = readAt };
            _index = index;
            System.IO.Directory.CreateDirectory(Directory);
            string temporary = IndexPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(index, Options));
            File.Move(temporary, IndexPath, overwrite: true);
        }
    }

    private CatalogIndex EnsureIndex()
    {
        _index ??= Index ?? new CatalogIndex(CatalogIndex.CurrentVersion, Source, "", DateTimeOffset.MinValue,
            new Dictionary<string, CatalogAirport>(StringComparer.OrdinalIgnoreCase));
        return _index;
    }

    /// <summary>Empreinte du contenu d'un terrain : change dès qu'une procédure, un point ou une contrainte change.</summary>
    public static string Hash(SimProcedureImport import)
    {
        var sb = new StringBuilder();
        foreach (var p in import.Procedures.OrderBy(p => p.Kind).ThenBy(p => p.Ident, StringComparer.Ordinal))
        {
            sb.Append(p.Kind).Append(':').Append(p.Ident);
            foreach (var v in p.Variants)
            {
                sb.Append('|').AppendJoin(',', v.Runways).Append('>');
                foreach (var l in v.Legs) sb.Append(l.Fix).Append('@').Append(l.MinAltFt).Append('/').Append(l.MaxAltFt).Append('/').Append(l.SpeedKt).Append(' ');
            }
            sb.Append('\n');
        }
        foreach (var f in import.Fixes.OrderBy(f => f.Ident, StringComparer.Ordinal))
            sb.Append(f.Ident).Append(f.Lat.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)).Append(f.Lon.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>Retire ce qui est inutilisable pour qu'un fichier abîmé ne gêne pas le calcul de route.</summary>
    private static SimProcedureImport Clean(SimProcedureImport import) => new(
        [.. import.Procedures.Where(p => !string.IsNullOrWhiteSpace(p.Ident) && p.Variants is { Count: > 0 } && p.Variants.All(v => v.Legs is { Count: > 0 }))],
        [.. import.Fixes.Where(f => !string.IsNullOrWhiteSpace(f.Ident))]);
}

/// <summary>Les procédures disponibles : le catalogue de l'utilisateur d'abord, puis celui livré avec l'application.</summary>
public sealed class ProcedureLibrary(ProcedureCatalog? bundled, ProcedureCatalog user)
{
    public ProcedureCatalog User => user;
    public ProcedureCatalog? Bundled => bundled;

    public bool Has(string icao) => user.Has(icao) || bundled?.Has(icao) == true;

    public SimProcedureImport? Read(string icao) =>
        (user.Has(icao) ? user.Read(icao) : null) ?? (bundled?.Has(icao) == true ? bundled.Read(icao) : null);

    public int AirportCount => AllAirports().Count();
    public int ProcedureCount => AllAirports().Sum(a => a.Procedures);

    /// <summary>Version du simulateur et date de lecture de ce que contient la bibliothèque (le catalogue le plus récent).</summary>
    public (string SimVersion, DateTimeOffset ReadAt)? Origin =>
        new[] { user.Index, bundled?.Index }.Where(i => i is not null).OrderByDescending(i => i!.ReadAt).Select(i => ((string, DateTimeOffset)?)(i!.SimVersion, i.ReadAt)).FirstOrDefault();

    public string? Problem => user.Problem ?? bundled?.Problem;

    /// <summary>Un terrain n'est compté qu'une fois, même s'il est dans les deux catalogues (celui de l'utilisateur l'emporte).</summary>
    private IEnumerable<CatalogAirport> AllAirports()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in new[] { user, bundled })
            foreach (var (icao, airport) in catalog?.Snapshot() ?? new Dictionary<string, CatalogAirport>())
                if (seen.Add(icao)) yield return airport;
    }
}
