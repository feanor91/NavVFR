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
    /// <summary>Dossier des cartes VAC (PDF du SIA) ; vide = %AppData%\VFRNav\Cartes VAC.</summary>
    public string VacFolder { get; set; } = "";

    /// <summary>Ancien réglage (dossier de StormATC), relu une fois pour reprendre le dossier des cartes ; plus jamais écrit.</summary>
    public string StormAtcPath { get; set; } = "";
    public bool AutoVariation { get; set; } = true;
    /// <summary>Langue de l'interface : « fr », « en » ; vide = celle de Windows (français si elle n'est pas proposée).</summary>
    public string Language { get; set; } = "";
    public AirportFilter Filter { get; set; } = new();
    /// <summary>Terrains limités à un couloir autour de la route.</summary>
    public bool FilterNearRoute { get; set; }
    public double FilterNearRouteNm { get; set; } = 30;
    public AirspaceFilter AirspaceFilter { get; set; } = new();
    /// <summary>clair, relief ou rues.</summary>
    public string BaseMap { get; set; } = "clair";
    /// <summary>Panneau d'explication des zones, en bas de la carte.</summary>
    public bool ZoneHelp { get; set; } = true;
    /// <summary>Terrains colorés selon la catégorie de vol de leur METAR.</summary>
    public bool WxDots { get; set; } = true;
    public bool ShowVor { get; set; } = true;
    public bool ShowNdb { get; set; } = true;
    /// <summary>Points de report VFR (triangles jaunes) affichés sur la carte.</summary>
    public bool ShowReportingPoints { get; set; } = true;
    public bool VacVisible { get; set; }
    /// <summary>right ou bottom.</summary>
    public string VacDock { get; set; } = "right";
    /// <summary>Fichier AIXM du SIA (zip ou xml) chargé pour le calcul de route IFR ; vide = données de test.</summary>
    public string IfrDataPath { get; set; } = "";
    /// <summary>Panneaux repliables du côté gauche : true = ouvert.</summary>
    /// <summary>Appareil choisi pour chaque type de navigation (null = le premier de la liste).</summary>
    public Guid? VfrAircraftId { get; set; }
    public Guid? IfrAircraftId { get; set; }
    /// <summary>Type de navigation dont l'appareil sert au log de navigation : vfr ou ifr.</summary>
    public string ActiveMode { get; set; } = "vfr";
    /// <summary>Altitude de croisière IFR en pieds (niveau de vol x 100).</summary>
    public double IfrCruiseAltFt { get; set; }
    /// <summary>Cercle de portée de l'appareil autour du point de départ.</summary>
    public bool ShowRange { get; set; } = true;
    /// <summary>Couches météo en temps réel sur la carte : radar de précipitations et nuages du satellite, avec leur opacité.</summary>
    public bool ShowRadar { get; set; }
    /// <summary>Ancienne case unique « Nuages » : lue une fois pour reprendre le choix (les deux couches), puis remise à faux.</summary>
    public bool ShowClouds { get; set; }
    /// <summary>SIGMET sur la carte : affichage, opacité, et restriction aux messages valables à l'altitude de croisière.</summary>
    public bool ShowSigmet { get; set; }
    /// <summary>Ajoute la finale ILS (indicative) à la route IFR quand la piste d'arrivée a un ILS dans les données du SIA.</summary>
    public bool IfrIlsApproach { get; set; } = true;
    public double SigmetOpacity { get; set; } = 0.7;
    public bool SigmetAltitudeOnly { get; set; }
    public bool ShowCloudCover { get; set; }
    public bool ShowCloudTops { get; set; }
    public double CloudCoverOpacity { get; set; } = 0.5;
    public double CloudTopsOpacity { get; set; } = 0.7;
    public double RadarOpacity { get; set; } = 0.7;
    public double CloudOpacity { get; set; } = 0.6;
    public bool CommonPanelOpen { get; set; } = true;
    public bool VfrPanelOpen { get; set; } = true;
    public bool IfrPanelOpen { get; set; } = true;
    public bool SettingsPanelOpen { get; set; } = true;

    /// <summary>Aéroports de dégagement saisis dans chaque panneau (codes OACI séparés par des espaces).</summary>
    public string VfrAlternates { get; set; } = "";

    /// <summary>Heure de départ saisie (UTC) ; vide = maintenant.</summary>
    public string DepartureUtc { get; set; } = "";

    /// <summary>Dernier dossier où un plan de vol .pln a été ouvert ou enregistré ; vide = dossier de plans de MSFS.</summary>
    public string LastPlanFolder { get; set; } = "";

    /// <summary>Le log de navigation utilise le vent prévu à l'altitude de chaque tronçon plutôt que le vent supposé.</summary>
    public bool UseForecastWind { get; set; }

    /// <summary>Dessiner sur la carte les SID du départ et les STAR de l'arrivée choisis dans le panneau IFR.</summary>
    public bool ShowProcedureMap { get; set; } = true;

    /// <summary>Ouvrir la feuille de route après le calcul d'une route IFR.</summary>
    public bool IfrOpenSheetAfterCompute { get; set; } = true;
    public string IfrAlternates { get; set; } = "";
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
