namespace VFRNav.Core.Models;

public sealed record Airport(string Ident, string Name, string Type, double Lat, double Lon, double ElevationFt, bool HasVac = false)
{
    public Waypoint ToWaypoint() => new()
    {
        Name = Ident,
        Icao = Ident,
        FullName = Name,
        ElevationFt = ElevationFt,
        Lat = Lat,
        Lon = Lon
    };
}
