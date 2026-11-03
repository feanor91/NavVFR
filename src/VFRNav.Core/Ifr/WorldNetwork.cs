using System.IO.Compression;
using System.Text;
using VFRNav.Core.Services;

namespace VFRNav.Core.Ifr;

/// <summary>Famille de points de navigation : point, balise NDB ou VOR.</summary>
public enum SimPointKind { Waypoint, Ndb, Vor }

/// <summary>Un point de navigation de MSFS : identifiant, région (deux lettres, qui distingue deux points de même nom), position et fréquence.</summary>
/// <param name="FrequencyHz">Fréquence en Hz pour une balise (0 pour un point simple).</param>
public sealed record SimPoint(SimPointKind Kind, string Ident, string Region, double Lat, double Lon, double AltitudeM, double FrequencyHz);

/// <summary>Un tronçon de route ATS vu depuis l'un de ses points : le point précédent et le suivant sur la route. Les altitudes sont des minima, en mètres.</summary>
/// <param name="Type">Route basse (1), haute (2), ou les deux (3).</param>
public sealed record SimRouteLink(string Airway, int Type, string PrevIdent, string PrevRegion, double PrevLat, double PrevLon, double PrevAltitudeM,
    string NextIdent, string NextRegion, double NextLat, double NextLon, double NextAltitudeM, char PrevType = 'W', char NextType = 'W');

/// <summary>Réseau de routes ATS hors de France lu dans MSFS : points, terrains et tronçons.</summary>
public sealed record WorldNetwork(string SimVersion, DateTimeOffset ReadAt, IReadOnlyList<IfrFix> Fixes, IReadOnlyList<AirwaySegment> Segments)
{
    public int Airports => Fixes.Count(f => f.Kind == FixKind.Airport);
    public int Airways => Segments.Select(s => s.Airway).Distinct(StringComparer.Ordinal).Count();
}

/// <summary>
/// Construit le réseau de routes ATS à mesure que MSFS donne les tronçons de chaque point. La France (région LF) est écartée de
/// l'exploration : elle vient du SIA. Les points voisins à explorer sont rendus à l'appelant, qui les demande à MSFS à son tour.
/// </summary>
public sealed class WorldNetworkBuilder
{
    /// <summary>Un tronçon est limité à ce niveau quand c'est une route basse (type 1) : par convention, les routes basses vont jusqu'au FL180.</summary>
    public const int LowRouteCeilingFl = 180;

    private readonly Dictionary<string, IfrFix> _fixes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Airway, string A, string B), AirwaySegment> _segments = [];
    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase);

    public int FixCount => _fixes.Count;
    public int SegmentCount => _segments.Count;
    public int Visited => _visited.Count;

    /// <summary>Points de départ de l'exploration : on les compte comme déjà vus pour ne pas les redemander.</summary>
    public IReadOnlyList<SimPoint> Seed(IEnumerable<SimPoint> points)
    {
        var seeds = new List<SimPoint>();
        foreach (var p in points)
        {
            if (p.Ident.Length == 0 || FixKey.Region(FixKey.Make(p.Ident, p.Region)) == FixKey.France) continue;
            if (!_visited.Add(FixKey.Make(p.Ident, p.Region))) continue;
            AddFix(p.Ident, p.Region, p.Lat, p.Lon, Kind(p.Kind));
            seeds.Add(p);
        }
        return seeds;
    }

    /// <summary>Un terrain du monde (code OACI, position) : il sert de départ ou d'arrivée à un vol hors de France.</summary>
    public void AddAirport(string icao, double lat, double lon)
    {
        if (icao.Length == 0 || (Math.Abs(lat) < 1e-6 && Math.Abs(lon) < 1e-6)) return;
        _fixes.TryAdd(icao, new IfrFix(icao, lat, lon, FixKind.Airport));
    }

    /// <summary>Enregistre les tronçons d'un point lu. Rend les points voisins pas encore vus (hors de France), à lire à leur tour.</summary>
    public IReadOnlyList<SimPoint> AddLinks(SimPoint at, IReadOnlyList<SimRouteLink> links)
    {
        var next = new List<SimPoint>();
        string atKey = FixKey.Make(at.Ident, at.Region);
        if (Math.Abs(at.Lat) > 1e-6 || Math.Abs(at.Lon) > 1e-6) AddFix(at.Ident, at.Region, at.Lat, at.Lon, Kind(at.Kind));

        foreach (var link in links)
        {
            // Une région vide veut dire « la même que le point de départ ».
            string prevRegion = link.PrevRegion.Length > 0 ? link.PrevRegion : at.Region;
            string nextRegion = link.NextRegion.Length > 0 ? link.NextRegion : at.Region;

            if (link.PrevIdent.Length > 0 && HasPosition(link.PrevLat, link.PrevLon))
            {
                string prevKey = AddFix(link.PrevIdent, prevRegion, link.PrevLat, link.PrevLon, Kind(link.PrevType));
                AddSegment(link, prevKey, atKey, link.PrevAltitudeM);
                Explore(link.PrevIdent, prevRegion, link.PrevLat, link.PrevLon, link.PrevType, next);
            }
            if (link.NextIdent.Length > 0 && HasPosition(link.NextLat, link.NextLon))
            {
                string nextKey = AddFix(link.NextIdent, nextRegion, link.NextLat, link.NextLon, Kind(link.NextType));
                AddSegment(link, atKey, nextKey, link.NextAltitudeM);
                Explore(link.NextIdent, nextRegion, link.NextLat, link.NextLon, link.NextType, next);
            }
        }
        return next;
    }

    public WorldNetwork Build(string simVersion, DateTimeOffset readAt) => new(simVersion, readAt, [.. _fixes.Values], [.. _segments.Values]);

    private void Explore(string ident, string region, double lat, double lon, char type, List<SimPoint> into)
    {
        string key = FixKey.Make(ident, region);
        if (FixKey.Region(key) == FixKey.France || !_visited.Add(key)) return;
        into.Add(new SimPoint(type switch { 'V' => SimPointKind.Vor, 'N' => SimPointKind.Ndb, _ => SimPointKind.Waypoint }, ident, region.ToUpperInvariant(), lat, lon, 0, 0));
    }

    private string AddFix(string ident, string region, double lat, double lon, FixKind kind)
    {
        string key = FixKey.Make(ident, region);
        _fixes.TryAdd(key, new IfrFix(key, lat, lon, kind));
        return key;
    }

    private void AddSegment(SimRouteLink link, string from, string to, double minAltitudeM)
    {
        if (link.Airway.Length == 0 || from.Equals(to, StringComparison.OrdinalIgnoreCase)) return;
        // Un tronçon est vu depuis ses deux extrémités : on range les deux points dans un ordre fixe pour ne le garder qu'une fois.
        var (a, b) = string.CompareOrdinal(from, to) <= 0 ? (from, to) : (to, from);
        int? lower = Fl(minAltitudeM);
        int? upper = link.Type == 1 ? LowRouteCeilingFl : null;
        var key = (link.Airway, a, b);
        if (_segments.TryGetValue(key, out var known))
        {
            lower = known.LowerFl is { } l && lower is { } m ? Math.Min(l, m) : known.LowerFl ?? lower;
            upper = known.UpperFl is { } u && upper is { } v ? Math.Max(u, v) : null; // sans plafond d'un côté : sans plafond
        }
        _segments[key] = new AirwaySegment(link.Airway, a, b, AirwayDirection.Both, lower, upper);
    }

    /// <summary>Niveau de vol d'un minimum d'altitude donné en mètres, arrondi à 5 niveaux ; null si MSFS n'en donne pas.</summary>
    public static int? Fl(double meters) => meters <= 1 ? null : (int)Math.Round(meters * 3.28084 / 500.0) * 5;

    private static bool HasPosition(double lat, double lon) => Math.Abs(lat) > 1e-6 || Math.Abs(lon) > 1e-6;

    private static FixKind Kind(char type) => type switch { 'V' => FixKind.Vor, 'N' => FixKind.Ndb, 'A' => FixKind.Airport, _ => FixKind.Waypoint };
    private static FixKind Kind(SimPointKind kind) => kind switch { SimPointKind.Vor => FixKind.Vor, SimPointKind.Ndb => FixKind.Ndb, _ => FixKind.Waypoint };
}

/// <summary>Fichier du réseau mondial : binaire compressé, pour se relire en une ou deux secondes malgré des centaines de milliers de tronçons.</summary>
public static class WorldNetworkStore
{
    public const string FileName = "world-network.bin.gz";
    private const string Magic = "VFRW";
    private const int Version = 1;
    private const int MaxCount = 5_000_000;

    /// <summary>%AppData%\VFRNav\network\world-network.bin.gz</summary>
    public static string UserPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VFRNav", "network", FileName);

    /// <summary>Fichier livré avec l'application, à côté de l'exécutable.</summary>
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "data", "network", FileName);

    public static void Write(string path, WorldNetwork network)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        using (var file = File.Create(temporary))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(gzip, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(network.SimVersion);
            w.Write(network.ReadAt.UtcTicks);

            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            w.Write(network.Fixes.Count);
            foreach (var f in network.Fixes)
            {
                index[f.Ident] = index.Count;
                w.Write(f.Ident);
                w.Write((byte)f.Kind);
                w.Write(f.Lat);
                w.Write(f.Lon);
            }

            var airways = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var s in network.Segments) airways.TryAdd(s.Airway, airways.Count);
            w.Write(airways.Count);
            foreach (var name in airways.Keys) w.Write(name);

            var segments = network.Segments.Where(s => index.ContainsKey(s.From) && index.ContainsKey(s.To)).ToList();
            w.Write(segments.Count);
            foreach (var s in segments)
            {
                w.Write(airways[s.Airway]);
                w.Write(index[s.From]);
                w.Write(index[s.To]);
                w.Write((byte)s.Direction);
                w.Write((short)(s.LowerFl ?? -1));
                w.Write((short)(s.UpperFl ?? -1));
            }
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Lit le fichier. Retourne null, avec la raison dans <paramref name="problem"/>, s'il est absent ou inutilisable.</summary>
    public static WorldNetwork? Read(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var r = new BinaryReader(gzip, Encoding.UTF8);
            if (r.ReadString() != Magic) { problem = "ce n'est pas un fichier de réseau VFRNav"; return null; }
            int version = r.ReadInt32();
            if (version != Version) { problem = $"version {version} non prise en charge"; return null; }
            string simVersion = r.ReadString();
            var readAt = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);

            int fixCount = Count(r);
            var fixes = new List<IfrFix>(fixCount);
            for (int i = 0; i < fixCount; i++)
            {
                string ident = r.ReadString();
                var kind = (FixKind)r.ReadByte();
                double lat = r.ReadDouble(), lon = r.ReadDouble();
                fixes.Add(new IfrFix(ident, lat, lon, kind));
            }

            int airwayCount = Count(r);
            var airways = new string[airwayCount];
            for (int i = 0; i < airwayCount; i++) airways[i] = r.ReadString();

            int segmentCount = Count(r);
            var segments = new List<AirwaySegment>(segmentCount);
            for (int i = 0; i < segmentCount; i++)
            {
                int airway = r.ReadInt32(), from = r.ReadInt32(), to = r.ReadInt32();
                var direction = (AirwayDirection)r.ReadByte();
                short lower = r.ReadInt16(), upper = r.ReadInt16();
                if ((uint)airway >= (uint)airwayCount || (uint)from >= (uint)fixCount || (uint)to >= (uint)fixCount) { problem = "fichier abîmé (index hors limites)"; return null; }
                segments.Add(new AirwaySegment(airways[airway], fixes[from].Ident, fixes[to].Ident, direction, lower < 0 ? null : lower, upper < 0 ? null : upper));
            }
            return new WorldNetwork(simVersion, readAt, fixes, segments);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            problem = ex.Message;
            return null;
        }
    }

    private static int Count(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n is < 0 or > MaxCount) throw new InvalidDataException("nombre d'éléments invraisemblable");
        return n;
    }
}

/// <summary>Ajoute le réseau mondial à la base du SIA, qui garde la France.</summary>
public static class WorldNetworkMerge
{
    /// <summary>Deux points de même nom à moins de cette distance sont le même point (un point frontalier du SIA et de MSFS).</summary>
    public const double SamePointNm = 2;

    public static IfrNavData Apply(IfrNavData data, WorldNetwork network)
    {
        // Un point de MSFS « ABCDE/EB » est le même que le « ABCDE » du SIA s'il est au même endroit : on garde la clé du SIA.
        var alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in network.Fixes)
        {
            if (!f.Ident.Contains('/')) continue;
            string plain = FixKey.Display(f.Ident);
            if (data.Fixes.TryGetValue(plain, out var sia) && NavMath.DistanceNm(f.Lat, f.Lon, sia.Lat, sia.Lon) <= SamePointNm) alias[f.Ident] = plain;
        }

        string Resolve(string key) => alias.TryGetValue(key, out var plain) ? plain : key;

        var fixes = data.Fixes.Values.ToList();
        var known = new HashSet<string>(data.Fixes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var f in network.Fixes)
            if (!alias.ContainsKey(f.Ident) && known.Add(f.Ident)) fixes.Add(f);

        var segments = new List<AirwaySegment>(data.Segments);
        foreach (var s in network.Segments)
        {
            string from = Resolve(s.From), to = Resolve(s.To);
            if (!from.Equals(to, StringComparison.OrdinalIgnoreCase)) segments.Add(s with { From = from, To = to });
        }
        return new IfrNavData(fixes, segments, data.Procedures, data.AiracCycle, data.EffectiveFrom);
    }
}
