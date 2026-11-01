namespace VFRNav.Core.StormAtc;

/// <summary>Où StormATC range ses données (voir <see cref="StormAtcPaths.Locate"/>).</summary>
/// <param name="Root">Dossier racine : celui qui contient <c>data</c> et <c>Cartes VAC</c>.</param>
/// <param name="UserProfilesDir">Profils du pilote (%LOCALAPPDATA%\StormATC\vfr-profiles), prioritaires.</param>
public sealed record StormAtcPaths(string Root, string UserProfilesDir)
{
    public string DataDir => Path.Combine(Root, "data");
    public string AerodromesCsv => Path.Combine(DataDir, "aerodromes-france.csv");
    public string BundledProfilesDir => Path.Combine(DataDir, "vfr-profiles");
    public string ChartsDir => Path.Combine(Root, "Cartes VAC");

    private static bool IsStormAtcRoot(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "data", "aerodromes-france.csv"));

    /// <summary>Cherche StormATC : dossier configuré, puis le dépôt de dev, puis l'installation de l'utilisateur.</summary>
    public static StormAtcPaths? Locate(string? configured = null)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            configured ?? "",
            @"D:\Repos\StormATC",
            Path.Combine(local, "Programs", "StormATC"),
        ];
        string? root = candidates.FirstOrDefault(IsStormAtcRoot);
        return root is null ? null : new StormAtcPaths(root, Path.Combine(local, "StormATC", "vfr-profiles"));
    }
}

public sealed record AerodromeFrequency(string Service, decimal Mhz, string Remark);

public sealed record AerodromeRunway(string Id, IReadOnlyList<int> Qfu, string? Surface, int? LengthMetres, int? WidthMetres);

/// <summary>Un aérodrome de la base de StormATC (CSV national + profil VFR s'il existe).</summary>
public sealed record StormAtcAerodrome(
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
    string? ChartPath)
{
    public bool HasChart => ChartPath is not null;
    public bool HasPosition => Lat is not null && Lon is not null;
}
