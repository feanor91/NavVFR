using VFRNav.Core.Models;

namespace VFRNav.Core.StormAtc;

/// <summary>Fusionne les terrains OurAirports (positions pour tous) avec la base de StormATC (noms, altitudes, VAC).</summary>
public static class AirportCatalog
{
    public static IReadOnlyList<Airport> Merge(IReadOnlyList<Airport> baseAirports, StormAtcRepository? stormAtc)
    {
        if (stormAtc is null) return baseAirports;
        var result = new List<Airport>(baseAirports.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in baseAirports)
        {
            seen.Add(a.Ident);
            var s = stormAtc.Find(a.Ident);
            result.Add(s is null ? a : a with
            {
                Name = s.Name,
                Lat = s.Lat ?? a.Lat,
                Lon = s.Lon ?? a.Lon,
                ElevationFt = s.ElevationFt ?? a.ElevationFt,
                HasVac = s.HasChart
            });
        }
        // Aérodromes de StormATC absents d'OurAirports mais dont le profil donne une position.
        foreach (var s in stormAtc.All)
            if (!seen.Contains(s.Icao) && s is { Lat: { } lat, Lon: { } lon })
                result.Add(new Airport(s.Icao, s.Name, "small_airport", lat, lon, s.ElevationFt ?? 0, s.HasChart));
        return result;
    }
}
