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

    public bool IsAirport => !string.IsNullOrEmpty(Icao);
}
