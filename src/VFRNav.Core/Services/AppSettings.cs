using System.Text.Json;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

public sealed class AppSettings
{
    public string Country { get; set; } = "FR";
    public string OpenAipKey { get; set; } = "";
    public string CartoKey { get; set; } = "";
    /// <summary>Clé de l'ICAO API Data Service (NOTAM).</summary>
    public string NotamKey { get; set; } = "";
    public double TasKt { get; set; } = 110;
    public double BurnLph { get; set; } = 28;
    public double CruiseAltFt { get; set; } = 2500;
    public double VariationEast { get; set; } = 1;
    public double WindDir { get; set; } = 270;
    public double WindKt { get; set; } = 10;
    /// <summary>Dossier de StormATC ; vide = détection automatique.</summary>
    public string StormAtcPath { get; set; } = "";
    public bool AutoVariation { get; set; } = true;
    public AirportFilter Filter { get; set; } = new();
    public AirspaceFilter AirspaceFilter { get; set; } = new();
    /// <summary>clair, relief ou rues.</summary>
    public string BaseMap { get; set; } = "clair";
    /// <summary>Panneau d'explication des zones, en bas de la carte.</summary>
    public bool ZoneHelp { get; set; } = true;
    /// <summary>Terrains colorés selon la catégorie de vol de leur METAR.</summary>
    public bool WxDots { get; set; } = true;
    public bool ShowVor { get; set; } = true;
    public bool ShowNdb { get; set; } = true;
    public bool VacVisible { get; set; }
    /// <summary>right ou bottom.</summary>
    public string VacDock { get; set; } = "right";
    public List<Waypoint> Route { get; set; } = [];

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VFRNav", "settings.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception) { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
