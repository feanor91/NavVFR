using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Core.Ifr;

public enum IfrLegSource { Airport, Sid, Airway, Star, Direct }

/// <summary>Point de la route IFR calculée. <see cref="Airway"/> = route ATS empruntée pour y arriver.</summary>
public sealed record IfrRoutePoint(
    string Ident, double Lat, double Lon, IfrLegSource Source,
    string? Airway = null, int? MinAltFt = null, int? MaxAltFt = null);

public sealed record IfrRoute(
    IReadOnlyList<IfrRoutePoint> Points, double DistanceNm, string? Sid, string? Star, string Description)
{
    /// <summary>Points sous la forme utilisée par le reste de l'application (log de navigation, .pln, carte).</summary>
    public IReadOnlyList<Waypoint> ToWaypoints() => Points.Select(p => new Waypoint
    {
        Name = FixKey.Display(p.Ident),
        Icao = p.Source == IfrLegSource.Airport ? FixKey.Display(p.Ident) : null,
        Lat = p.Lat,
        Lon = p.Lon,
        // Une contrainte « au moins / au plus » devient une altitude de passage quand une seule borne existe.
        AltitudeFt = p.MinAltFt ?? p.MaxAltFt,
    }).ToList();
}

public sealed record IfrPlanResult(IfrRoute? Route, string? Error)
{
    public bool Success => Route is not null;
}

public sealed class IfrPlanOptions
{
    /// <summary>SID imposée (ex. « AMBO1A ») ; null = la meilleure disponible.</summary>
    public string? Sid { get; set; }
    public string? Star { get; set; }
    public string? DepartureRunway { get; set; }
    public string? ArrivalRunway { get; set; }

    /// <summary>Niveau de vol de croisière : écarte les tronçons d'airway qui ne le couvrent pas.</summary>
    public int? FlightLevel { get; set; }

    /// <summary>Rayon de recherche des points d'entrée sur le réseau quand un terrain n'a ni SID ni STAR.</summary>
    public double DirectRadiusNm { get; set; } = 80;

    /// <summary>
    /// Niveau à partir duquel la route libre est supposée : on peut alors enchaîner des tronçons directs entre points proches
    /// (au-dessus du FL195, l'espace supérieur français est en route libre FRA et n'a presque plus d'airways).
    /// null = jamais.
    /// </summary>
    public int? FreeRouteFromFl { get; set; } = 195;

    /// <summary>Portée maximale d'un tronçon direct en route libre.</summary>
    public double FreeRouteLegNm { get; set; } = 150;

    /// <summary>Autorise un tronçon direct (DCT) quand aucun airway ne relie départ et arrivée.</summary>
    public bool AllowDirect { get; set; } = true;
}

/// <summary>
/// Calcule une route IFR : SID → airways → STAR, en cherchant le plus court chemin dans le réseau de routes ATS.
/// </summary>
public static class IfrRouter
{
    /// <summary>Nombre de points du réseau essayés autour d'un terrain sans procédure (les plus proches utilisables au niveau demandé).</summary>
    private const int DirectAnchorCount = 20;

    /// <summary>Nombre de points voisins visés par un tronçon direct en route libre.</summary>
    private const int FreeRouteNeighbours = 10;

    private sealed record Anchor(string Node, double Cost, string? Procedure, IReadOnlyList<IfrRoutePoint> Points, IfrLegSource Source);

    public static IfrPlanResult Plan(IfrNavData data, string departure, string arrival, IfrPlanOptions? options = null)
    {
        options ??= new IfrPlanOptions();
        var dep = data.Find(departure);
        var arr = data.Find(arrival);
        if (dep is null) return Fail($"Terrain de départ inconnu : {departure}.");
        if (arr is null) return Fail($"Terrain d'arrivée inconnu : {arrival}.");

        var starts = Anchors(data, dep, ProcedureKind.Sid, options.Sid, options.DepartureRunway, options.DirectRadiusNm, options.FlightLevel, FreeRoute(options), out var startError);
        if (startError is not null) return Fail(startError);
        var ends = Anchors(data, arr, ProcedureKind.Star, options.Star, options.ArrivalRunway, options.DirectRadiusNm, options.FlightLevel, FreeRoute(options), out var endError);
        if (endError is not null) return Fail(endError);
        if (starts.Count == 0) return Fail($"Aucun point du réseau IFR à moins de {options.DirectRadiusNm:0} NM de {departure}.");
        if (ends.Count == 0) return Fail($"Aucun point du réseau IFR à moins de {options.DirectRadiusNm:0} NM de {arrival}.");

        var (dist, prev, root) = Dijkstra(data, starts, ends, arr, options);

        Anchor? bestStart = null, bestEnd = null;
        List<(string Node, AirwaySegment? Segment)> path = [];
        double best = double.MaxValue;

        foreach (var e in ends)
        {
            if (!dist.TryGetValue(e.Node, out double d) || d + e.Cost >= best) continue;
            best = d + e.Cost;
            bestEnd = e;
        }

        if (bestEnd is not null)
        {
            path = Trace(bestEnd.Node, prev);
            bestStart = root[path[0].Node];
        }
        else if (options.AllowDirect)
        {
            // Aucun airway ne relie les deux : tronçon direct entre la fin de la SID et le début de la STAR.
            foreach (var s in starts)
                foreach (var e in ends)
                {
                    double d = s.Cost + Distance(data, s.Node, e.Node) + e.Cost;
                    if (d >= best) continue;
                    best = d;
                    bestStart = s;
                    bestEnd = e;
                }
            if (bestStart is null || bestEnd is null) return Fail("Aucune route trouvée.");
            path = [(bestStart.Node, null)];
            if (!bestStart.Node.Equals(bestEnd.Node, StringComparison.OrdinalIgnoreCase)) path.Add((bestEnd.Node, null));
        }
        else
        {
            return Fail($"Aucune route ATS ne relie {departure} à {arrival}" +
                        (options.FlightLevel is { } fl ? $" au FL{fl}." : "."));
        }

        return new IfrPlanResult(Assemble(data, dep, arr, bestStart!, bestEnd!, path), null);
    }

    /// <summary>Vrai quand le niveau demandé est dans l'espace en route libre.</summary>
    private static bool FreeRoute(IfrPlanOptions o) => o.FlightLevel is { } level && o.FreeRouteFromFl is { } from && level >= from;

    private static IfrPlanResult Fail(string error) => new(null, error);

    private static double Distance(IfrNavData data, string a, string b)
    {
        var fa = data.Fixes[a];
        var fb = data.Fixes[b];
        return NavMath.DistanceNm(fa.Lat, fa.Lon, fb.Lat, fb.Lon);
    }

    /// <summary>
    /// Points d'accrochage au réseau pour un terrain : le point de sortie de chaque SID (ou d'entrée de chaque STAR)
    /// avec le coût de la procédure ; à défaut de procédure, les points du réseau les plus proches en direct.
    /// </summary>
    private static List<Anchor> Anchors(IfrNavData data, IfrFix airport, ProcedureKind kind, string? ident, string? runway, double radiusNm, int? flightLevel, bool freeRoute, out string? error)
    {
        error = null;
        var procedures = data.ProceduresFor(airport.Ident, kind).ToList();
        bool sid = kind == ProcedureKind.Sid;
        string label = sid ? "SID" : "STAR";

        if (ident is not null)
        {
            procedures = procedures.Where(p => p.Ident.Equals(ident, StringComparison.OrdinalIgnoreCase)).ToList();
            if (procedures.Count == 0)
            {
                error = $"{label} {ident} introuvable pour {airport.Ident}.";
                return [];
            }
        }

        var anchors = new List<Anchor>();
        foreach (var proc in procedures)
            foreach (var variant in proc.Variants)
            {
                if (!variant.AppliesTo(runway) || variant.Legs.Count == 0) continue;
                var points = new List<IfrRoutePoint> { Point(airport, IfrLegSource.Airport) };
                bool resolvable = true;
                foreach (var leg in variant.Legs)
                {
                    var fix = data.Find(leg.Fix);
                    if (fix is null) { resolvable = false; break; }
                    points.Add(Point(fix, sid ? IfrLegSource.Sid : IfrLegSource.Star, null, leg.MinAltFt, leg.MaxAltFt));
                }
                if (!resolvable) continue;

                // Une STAR se lit de l'entrée vers le terrain : on la fait finir sur le terrain.
                if (!sid) { points.RemoveAt(0); points.Add(Point(airport, IfrLegSource.Airport)); }

                double cost = Length(points);
                var node = sid ? points[^1].Ident : points[0].Ident;
                anchors.Add(new Anchor(node, cost, proc.Ident, points, sid ? IfrLegSource.Sid : IfrLegSource.Star));
            }

        if (anchors.Count > 0) return anchors;

        if (procedures.Count > 0)
        {
            // Des procédures existent mais aucune n'est utilisable : pas de repli silencieux vers un tronçon direct.
            error = runway is not null
                ? $"Aucune {label}{(ident is null ? "" : " " + ident)} ne convient à la piste {runway} de {airport.Ident}."
                : $"Les {label} de {airport.Ident} référencent des points absents des données.";
            return [];
        }

        // Pas de procédure publiée : on se raccroche directement aux points du réseau les plus proches.
        var onNetwork = data.Fixes.Values
            .Where(f => f.Kind != FixKind.Airport && (freeRoute || data.EdgesFrom(f.Ident).Any(e => e.Segment.AllowsFlightLevel(flightLevel))))
            .Select(f => (Fix: f, D: NavMath.DistanceNm(airport.Lat, airport.Lon, f.Lat, f.Lon)))
            .Where(x => x.D <= radiusNm)
            .OrderBy(x => x.D)
            .Take(DirectAnchorCount);
        foreach (var (fix, d) in onNetwork)
        {
            var points = sid
                ? new List<IfrRoutePoint> { Point(airport, IfrLegSource.Airport), Point(fix, IfrLegSource.Direct) }
                : [Point(fix, IfrLegSource.Direct), Point(airport, IfrLegSource.Airport)];
            anchors.Add(new Anchor(fix.Ident, d, null, points, IfrLegSource.Direct));
        }
        return anchors;
    }

    private static IfrRoutePoint Point(IfrFix f, IfrLegSource source, string? airway = null, int? min = null, int? max = null) =>
        new(f.Ident, f.Lat, f.Lon, source, airway, min, max);

    private static double Length(IReadOnlyList<IfrRoutePoint> points)
    {
        double total = 0;
        for (int i = 1; i < points.Count; i++)
            total += NavMath.DistanceNm(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);
        return total;
    }

    /// <summary>
    /// Plus court chemin (A*) : la distance à l'arrivée sert d'estimation, ce qui reste exact (jamais trop grande : une procédure et un trajet
    /// ne sont pas plus courts qu'une ligne droite) et évite d'explorer tout le réseau du monde. La recherche s'arrête quand aucun chemin
    /// restant ne peut battre le meilleur trouvé jusqu'à un point d'arrivée.
    /// </summary>
    private static (Dictionary<string, double> Dist, Dictionary<string, (string From, AirwaySegment? Segment)> Prev, Dictionary<string, Anchor> Root)
        Dijkstra(IfrNavData data, List<Anchor> starts, List<Anchor> ends, IfrFix arrival, IfrPlanOptions options)
    {
        int? flightLevel = options.FlightLevel;
        bool freeRoute = FreeRoute(options);
        var grid = freeRoute ? new FixGrid(data) : null;
        var cmp = StringComparer.OrdinalIgnoreCase;
        var dist = new Dictionary<string, double>(cmp);
        var prev = new Dictionary<string, (string, AirwaySegment?)>(cmp);
        var root = new Dictionary<string, Anchor>(cmp);
        var queue = new PriorityQueue<string, double>();

        var endCost = new Dictionary<string, double>(cmp);
        foreach (var e in ends)
            if (!endCost.TryGetValue(e.Node, out double c) || e.Cost < c) endCost[e.Node] = e.Cost;
        double best = double.MaxValue;

        double Estimate(string node)
        {
            var f = data.Fixes[node];
            return NavMath.DistanceNm(f.Lat, f.Lon, arrival.Lat, arrival.Lon);
        }

        void Reached(string node, double cost)
        {
            if (endCost.TryGetValue(node, out double ec)) best = Math.Min(best, cost + ec);
        }

        foreach (var s in starts)
        {
            if (dist.TryGetValue(s.Node, out double known) && known <= s.Cost) continue;
            dist[s.Node] = s.Cost;
            root[s.Node] = s;
            queue.Enqueue(s.Node, s.Cost + Estimate(s.Node));
            Reached(s.Node, s.Cost);
        }

        while (queue.TryDequeue(out var u, out double priority))
        {
            double du = dist[u];
            if (priority > du + Estimate(u) + 1e-9) continue; // entrée périmée : ce point a été atteint plus court depuis
            if (priority >= best) break;                      // plus aucun chemin ne peut battre le meilleur trouvé

            foreach (var edge in data.EdgesFrom(u))
            {
                if (!edge.Segment.AllowsFlightLevel(flightLevel)) continue;
                double nd = du + Distance(data, u, edge.To);
                if (dist.TryGetValue(edge.To, out double dv) && dv <= nd) continue;
                dist[edge.To] = nd;
                prev[edge.To] = (u, edge.Segment);
                queue.Enqueue(edge.To, nd + Estimate(edge.To));
                Reached(edge.To, nd);
            }

            if (grid is null) continue;
            var here = data.Fixes[u];
            foreach (var other in grid.Nearest(here, FreeRouteNeighbours, options.FreeRouteLegNm))
            {
                double nd = du + NavMath.DistanceNm(here.Lat, here.Lon, other.Lat, other.Lon);
                if (dist.TryGetValue(other.Ident, out double dv) && dv <= nd) continue;
                dist[other.Ident] = nd;
                prev[other.Ident] = (u, null);
                queue.Enqueue(other.Ident, nd + Estimate(other.Ident));
                Reached(other.Ident, nd);
            }
        }
        return (dist, prev, root);
    }

    /// <summary>Chemin du point d'accrochage jusqu'à <paramref name="end"/> ; chaque point porte l'airway qui y arrive.</summary>
    private static List<(string Node, AirwaySegment? Segment)> Trace(string end, Dictionary<string, (string From, AirwaySegment? Segment)> prev)
    {
        var path = new List<(string, AirwaySegment?)>();
        string node = end;
        while (prev.TryGetValue(node, out var p))
        {
            path.Add((node, p.Segment));
            node = p.From;
        }
        path.Add((node, null));
        path.Reverse();
        return path;
    }

    private static IfrRoute Assemble(IfrNavData data, IfrFix dep, IfrFix arr, Anchor start, Anchor end, List<(string Node, AirwaySegment? Segment)> path)
    {
        var points = new List<IfrRoutePoint>(start.Points);
        var enroute = new List<IfrRoutePoint>();

        foreach (var (node, segment) in path)
        {
            var fix = data.Fixes[node];
            enroute.Add(Point(fix, segment is null ? IfrLegSource.Direct : IfrLegSource.Airway, segment?.Airway));
        }

        // Le premier point de l'enroute est déjà la fin de la SID (ou le point d'accrochage direct).
        foreach (var p in enroute.Skip(1)) Add(points, p);
        foreach (var p in end.Points) Add(points, p);

        string description = Describe(dep, arr, start.Procedure, end.Procedure, enroute);
        return new IfrRoute(points, Length(points), start.Procedure, end.Procedure, description);
    }

    private static void Add(List<IfrRoutePoint> points, IfrRoutePoint p)
    {
        if (points.Count > 0 && points[^1].Ident.Equals(p.Ident, StringComparison.OrdinalIgnoreCase))
        {
            // Même point : on garde les contraintes de la procédure et l'airway d'arrivée.
            var last = points[^1];
            points[^1] = last with { Airway = last.Airway ?? p.Airway, MinAltFt = last.MinAltFt ?? p.MinAltFt, MaxAltFt = last.MaxAltFt ?? p.MaxAltFt };
            return;
        }
        points.Add(p);
    }

    /// <summary>Route en notation de plan de vol : « LFPG AMBO1A AMBO UN871 TOU TOU1B LFBO ».</summary>
    private static string Describe(IfrFix dep, IfrFix arr, string? sid, string? star, List<IfrRoutePoint> enroute)
    {
        var tokens = new List<string> { FixKey.Display(dep.Ident) };
        if (sid is not null) tokens.Add(sid);

        if (enroute.Count > 0) tokens.Add(FixKey.Display(enroute[0].Ident));
        for (int i = 1; i < enroute.Count; i++)
        {
            // On n'écrit que la fin de chaque série de points sur la même route ATS.
            bool endOfRun = i == enroute.Count - 1 || enroute[i].Airway is null || enroute[i + 1].Airway != enroute[i].Airway;
            if (!endOfRun) continue;
            tokens.Add(enroute[i].Airway ?? "DCT");
            tokens.Add(FixKey.Display(enroute[i].Ident));
        }

        if (star is not null) tokens.Add(star);
        tokens.Add(FixKey.Display(arr.Ident));
        return string.Join(' ', tokens);
    }

    /// <summary>Index par cases de 1° pour trouver vite les points voisins d'un point (route libre).</summary>
    private sealed class FixGrid
    {
        private readonly Dictionary<(int, int), List<IfrFix>> _cells = [];

        public FixGrid(IfrNavData data)
        {
            foreach (var f in data.Fixes.Values.Where(f => f.Kind != FixKind.Airport))
            {
                var key = Cell(f.Lat, f.Lon);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = [];
                list.Add(f);
            }
        }

        private static (int, int) Cell(double lat, double lon) => ((int)Math.Floor(lat), (int)Math.Floor(lon));

        public IEnumerable<IfrFix> Nearest(IfrFix from, int count, double maxNm)
        {
            var (cy, cx) = Cell(from.Lat, from.Lon);
            // Une case fait environ 60 NM de haut : on regarde assez de cases pour couvrir la portée demandée.
            int reach = (int)Math.Ceiling(maxNm / 45.0);
            var found = new List<(IfrFix Fix, double D)>();
            for (int y = cy - reach; y <= cy + reach; y++)
                for (int x = cx - reach; x <= cx + reach; x++)
                    if (_cells.TryGetValue((y, x), out var list))
                        foreach (var f in list)
                        {
                            if (ReferenceEquals(f, from)) continue;
                            double d = NavMath.DistanceNm(from.Lat, from.Lon, f.Lat, f.Lon);
                            if (d <= maxNm) found.Add((f, d));
                        }
            return found.OrderBy(x => x.D).Take(count).Select(x => x.Fix);
        }
    }
}
