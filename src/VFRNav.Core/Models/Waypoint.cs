using System.Text.Json.Serialization;

namespace VFRNav.Core.Models;

/// <summary>Point de la route : terrain (avec Icao) ou point tournant.</summary>
public sealed class Waypoint
{
    public string Name { get; set; } = "";
    public string? Icao { get; set; }
    public string? FullName { get; set; }
    public double ElevationFt { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }

    /// <summary>Notes du pilote pour ce point (consignes, repères visuels, fréquences...).</summary>
    public string? Notes { get; set; }

    public bool IsAirport => !string.IsNullOrEmpty(Icao);

    [JsonIgnore]
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>Repère affiché dans la liste de route quand le point a une note.</summary>
    [JsonIgnore]
    public string NoteMark => HasNotes ? "📝" : "";
}
