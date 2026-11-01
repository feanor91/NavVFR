using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>Position de l'avion du joueur telle que NavVFR la partage.</summary>
public sealed record FeedAircraft(double Lat, double Lon, double AltitudeFt, double HeadingTrue, double GroundSpeedKt, bool OnGround);

public sealed record FeedWaypoint(string Name, string? Icao, double Lat, double Lon, string? Notes);

public sealed record FeedAirspace(string Name, string IcaoClass, string Type, string Kind, string Lower, string Upper,
    double LowerFt, double UpperFt, double[][][] Rings);

public sealed record FeedAirport(string Ident, string Name, double Lat, double Lon);

/// <summary>Ce que NavVFR partage : la route, l'avion, les réglages de vol.</summary>
/// <param name="BaseMap">Fond de carte de NavVFR : le panneau de StormATC dessine le même, tuile par tuile, par <c>/api/tile</c>.</param>
public sealed record LiveFeedState(int Version, IReadOnlyList<FeedWaypoint> Route, FeedAircraft? Aircraft, double CruiseAltFt, long AircraftAgeMs, FeedBaseMap? BaseMap = null);

public sealed record FeedBaseMap(string Key, int Layers, int MaxZoom);

/// <summary>Zones et terrains d'un rectangle de la carte.</summary>
public sealed record LiveFeedMap(IReadOnlyList<FeedAirspace> Airspaces, IReadOnlyList<FeedAirport> Airports, bool AirspacesLoaded, bool Truncated);

/// <summary>Construit les réponses du flux, sans réseau ni interface : le serveur local n'a plus qu'à les sérialiser.</summary>
public static class LiveFeed
{
    public const int ProtocolVersion = 1;

    /// <summary>Au-delà, la réponse est coupée : le panneau du simulateur doit rester fluide.</summary>
    public const int MaxAirspaces = 400;
    public const int MaxAirports = 150;

    public static LiveFeedState State(IEnumerable<Waypoint> route, FeedAircraft? aircraft, DateTimeOffset? aircraftAt, double cruiseAltFt, DateTimeOffset now) =>
        new(ProtocolVersion,
            [.. route.Select(w => new FeedWaypoint(w.Name, w.Icao, w.Lat, w.Lon, string.IsNullOrWhiteSpace(w.Notes) ? null : w.Notes.Trim()))],
            aircraft, cruiseAltFt,
            aircraft is null || aircraftAt is null ? -1 : (long)Math.Max(0, (now - aircraftAt.Value).TotalMilliseconds));

    /// <summary>Zones (déjà filtrées par les réglages de l'utilisateur) et terrains qui touchent le rectangle.</summary>
    public static LiveFeedMap Map(IEnumerable<Airspace> shownAirspaces, bool airspacesLoaded, IEnumerable<Airport> airports,
        double minLat, double minLon, double maxLat, double maxLon)
    {
        var zones = new List<FeedAirspace>();
        bool truncated = false;
        foreach (var z in shownAirspaces)
        {
            if (!Touches(z, minLat, minLon, maxLat, maxLon)) continue;
            if (zones.Count >= MaxAirspaces) { truncated = true; break; }
            zones.Add(new FeedAirspace(z.Name, z.IcaoClass, Airspace.TypeName(z.TypeCode), z.Kind.ToString().ToLowerInvariant(),
                z.Lower, z.Upper, z.LowerFt, z.UpperFt >= 1e9 ? 1e9 : z.UpperFt,
                [.. z.Rings.Select(r => r.Select(p => new[] { Math.Round(p.Lon, 4), Math.Round(p.Lat, 4) }).ToArray())]));
        }

        var apts = new List<FeedAirport>();
        foreach (var a in airports)
        {
            if (a.Lat < minLat || a.Lat > maxLat || a.Lon < minLon || a.Lon > maxLon) continue;
            if (apts.Count >= MaxAirports) { truncated = true; break; }
            apts.Add(new FeedAirport(a.Ident, a.Name, a.Lat, a.Lon));
        }
        return new LiveFeedMap(zones, apts, airspacesLoaded, truncated);
    }

    /// <summary>Vrai si l'enveloppe d'un des contours de la zone touche le rectangle.</summary>
    public static bool Touches(Airspace z, double minLat, double minLon, double maxLat, double maxLon)
    {
        foreach (var ring in z.Rings)
        {
            if (ring.Count == 0) continue;
            double lo = double.MaxValue, hi = double.MinValue, west = double.MaxValue, east = double.MinValue;
            foreach (var (lon, lat) in ring)
            {
                lo = Math.Min(lo, lat); hi = Math.Max(hi, lat);
                west = Math.Min(west, lon); east = Math.Max(east, lon);
            }
            if (hi >= minLat && lo <= maxLat && east >= minLon && west <= maxLon) return true;
        }
        return false;
    }
}
