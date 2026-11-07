namespace VFRNav.Core.Models;

/// <param name="Iata">Code IATA (aéroports commerciaux), vide sinon.</param>
/// <param name="City">Commune desservie.</param>
/// <param name="SimIdent">Code que MSFS donne au même terrain quand il diffère (« EGGL » pour « GB-0309 ») : les deux bases se rapprochent par la position.</param>
/// <param name="Country">Code ISO du pays (« FR »).</param>
/// <param name="Continent">EU, NA, SA, AF, AS, OC ou AN.</param>
/// <param name="Scheduled">Des lignes régulières y sont exploitées.</param>
/// <param name="LongestRunwayFt">Piste la plus longue, en pieds ; null si inconnue.</param>
/// <param name="LongestPavedRunwayFt">Piste en dur la plus longue, en pieds ; null s'il n'y en a pas (ou si inconnue).</param>
public sealed record Airport(
    string Ident, string Name, string Type, double Lat, double Lon, double ElevationFt, bool HasVac = false,
    string? Iata = null, string? City = null, string? Country = null, string? Continent = null,
    bool Scheduled = false, int? LongestRunwayFt = null, int? LongestPavedRunwayFt = null, string? SimIdent = null)
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
