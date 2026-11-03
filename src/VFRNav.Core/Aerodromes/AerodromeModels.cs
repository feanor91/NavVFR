namespace VFRNav.Core.Aerodromes;

/// <summary>Où MSFS Rhumb range ses données d'aérodromes (voir <see cref="AerodromePaths.Locate"/>).</summary>
/// <param name="DataDir">Dossier livré avec l'application : <c>aerodromes-france.csv</c> et <c>vfr-profiles</c>.</param>
/// <param name="UserProfilesDir">Profils du pilote (%AppData%\VFRNavfr-profiles), prioritaires sur ceux livrés.</param>
/// <param name="ChartsDir">Dossier des cartes VAC du pilote (fichiers <c>AD-2.&lt;OACI&gt;.pdf</c>, © SIA : jamais livrées).</param>
public sealed record AerodromePaths(string DataDir, string UserProfilesDir, string ChartsDir)
{
    public const string DataFolderName = "vfr-data";
    public const string ChartsFolderName = "Cartes VAC";

    public string AerodromesCsv => Path.Combine(DataDir, "aerodromes-france.csv");
    public string BundledProfilesDir => Path.Combine(DataDir, "vfr-profiles");

    private static bool IsDataDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "aerodromes-france.csv"));

    /// <summary>Dossier des données livrées : à côté de l'exécutable, sinon en remontant (compilation, tests).</summary>
    public static string? FindDataDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, DataFolderName);
            if (IsDataDir(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Dossier des cartes : celui qui est configuré (ou son sous-dossier « Cartes VAC » s'il en a un), sinon
    /// %AppData%\VFRNav\Cartes VAC.
    /// </summary>
    public static string ResolveChartsDir(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string sub = Path.Combine(configured.Trim(), ChartsFolderName);
            return Directory.Exists(sub) ? sub : configured.Trim();
        }
        return Path.Combine(AppDataDir, ChartsFolderName);
    }

    private static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VFRNav");

    /// <summary>Données d'aérodromes de l'application ; null si le dossier livré est introuvable.</summary>
    public static AerodromePaths? Locate(string? chartsFolder = null)
    {
        string? data = FindDataDir();
        return data is null ? null : new AerodromePaths(data, Path.Combine(AppDataDir, "vfr-profiles"), ResolveChartsDir(chartsFolder));
    }
}

public sealed record AerodromeFrequency(string Service, decimal Mhz, string Remark);

public sealed record AerodromeRunway(string Id, IReadOnlyList<int> Qfu, string? Surface, int? LengthMetres, int? WidthMetres);

/// <summary>Un aérodrome de la base de MSFS Rhumb (CSV national + profil VFR s'il existe).</summary>
public sealed record AerodromeInfo(
    string Icao,
    string Name,
    string? Commune,
    string? Remark,
    bool VacListed,
    double? Lat,
    double? Lon,
    double? ElevationFt,
    double? MagneticVariation,
    string? ServiceKind,
    IReadOnlyList<AerodromeFrequency> Frequencies,
    IReadOnlyList<AerodromeRunway> Runways,
    string? ChartPath,
    VfrProcedures? Procedures = null)
{
    public bool HasChart => ChartPath is not null;
    public bool HasPosition => Lat is not null && Lon is not null;
}
