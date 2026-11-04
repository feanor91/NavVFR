using VFRNav.Core.Models;

namespace VFRNav.Core.Aerodromes;

/// <summary>Fusionne les terrains OurAirports (positions pour tous) avec la base d'aérodromes de MSFS Rhumb (noms, altitudes, VAC).</summary>
public static class AirportCatalog
{
    public static IReadOnlyList<Airport> Merge(IReadOnlyList<Airport> baseAirports, AerodromeRepository? aerodromes)
    {
        if (aerodromes is null) return baseAirports;
        var result = new List<Airport>(baseAirports.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in baseAirports)
        {
            seen.Add(a.Ident);
            var s = aerodromes.Find(a.Ident);
            result.Add(s is null ? a : a with
            {
                Name = s.Name,
                Lat = s.Lat ?? a.Lat,
                Lon = s.Lon ?? a.Lon,
                ElevationFt = s.ElevationFt ?? a.ElevationFt,
                HasVac = s.HasChart
            });
        }
        // Aérodromes de la base absents d'OurAirports mais dont le profil donne une position.
        foreach (var s in aerodromes.All)
            if (!seen.Contains(s.Icao) && s is { Lat: { } lat, Lon: { } lon })
                result.Add(new Airport(s.Icao, s.Name, "small_airport", lat, lon, s.ElevationFt ?? 0, s.HasChart, Country: "FR", Continent: "EU"));
        return result;
    }
}
