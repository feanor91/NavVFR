namespace VFRNav.Core.Ifr;

/// <summary>Une arête du graphe d'airways : on peut aller de l'origine à <see cref="To"/> par ce tronçon.</summary>
public sealed record AirwayEdge(string To, AirwaySegment Segment);

/// <summary>
/// Base de navigation IFR : points, routes ATS et procédures, avec le cycle AIRAC dont elle provient.
/// Indépendante de la source (AIXM, ARINC 424, etc.) : les lecteurs de formats la remplissent.
/// </summary>
public sealed class IfrNavData
{
    private readonly Dictionary<string, List<AirwayEdge>> _edges;

    public IfrNavData(
        IEnumerable<IfrFix> fixes,
        IEnumerable<AirwaySegment> segments,
        IEnumerable<IfrProcedure> procedures,
        string? airacCycle = null,
        DateTime? effectiveFrom = null)
    {
        _edges = new(StringComparer.OrdinalIgnoreCase);
        AiracCycle = airacCycle;
        EffectiveFrom = effectiveFrom;

        var fixMap = new Dictionary<string, IfrFix>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fixes) fixMap.TryAdd(f.Ident, f);
        Fixes = fixMap;

        var segs = new List<AirwaySegment>();
        foreach (var s in segments)
        {
            // Un tronçon dont une extrémité est inconnue est inutilisable : on l'ignore.
            if (!fixMap.ContainsKey(s.From) || !fixMap.ContainsKey(s.To)) continue;
            segs.Add(s);
            if (s.Direction != AirwayDirection.Backward) AddEdge(s.From, s.To, s);
            if (s.Direction != AirwayDirection.Forward) AddEdge(s.To, s.From, s);
        }
        Segments = segs;
        Procedures = procedures.ToList();
    }

    /// <summary>Copie qui partage les routes ATS (immuables) de <paramref name="source"/> : seuls les points et les procédures changent.</summary>
    private IfrNavData(IfrNavData source, IReadOnlyDictionary<string, IfrFix> fixes, IReadOnlyList<IfrProcedure> procedures)
    {
        _edges = source._edges;
        AiracCycle = source.AiracCycle;
        EffectiveFrom = source.EffectiveFrom;
        Fixes = fixes;
        Segments = source.Segments;
        Procedures = procedures;
    }

    /// <summary>
    /// Même base avec d'autres procédures et des points en plus (ceux qui manquent). Les routes ATS ne sont pas reconstruites : c'est
    /// rapide même avec le réseau du monde entier. Les points ajoutés ne sont reliés à aucune route ATS.
    /// </summary>
    public IfrNavData WithProcedures(IEnumerable<IfrFix> extraFixes, IReadOnlyList<IfrProcedure> procedures)
    {
        var map = new Dictionary<string, IfrFix>(Fixes, StringComparer.OrdinalIgnoreCase);
        foreach (var f in extraFixes) map.TryAdd(f.Ident, f);
        return new IfrNavData(this, map, procedures);
    }

    /// <summary>Cycle AIRAC, ex. « 2611 » (onzième cycle de 2026).</summary>
    public string? AiracCycle { get; }

    /// <summary>Date de mise en vigueur du cycle (cycles de 28 jours).</summary>
    public DateTime? EffectiveFrom { get; }

    public IReadOnlyDictionary<string, IfrFix> Fixes { get; }
    public IReadOnlyList<AirwaySegment> Segments { get; }
    public IReadOnlyList<IfrProcedure> Procedures { get; }

    /// <summary>Un cycle AIRAC dure 28 jours : au-delà, les données sont périmées.</summary>
    public bool IsExpired(DateTime nowUtc) => EffectiveFrom is { } from && nowUtc.Date >= from.Date.AddDays(28);

    public IfrFix? Find(string ident) => Fixes.TryGetValue(ident, out var f) ? f : null;

    public IReadOnlyList<AirwayEdge> EdgesFrom(string fix) =>
        _edges.TryGetValue(fix, out var list) ? list : Array.Empty<AirwayEdge>();

    public IEnumerable<IfrProcedure> ProceduresFor(string airport, ProcedureKind kind) =>
        Procedures.Where(p => p.Kind == kind && p.Airport.Equals(airport, StringComparison.OrdinalIgnoreCase));

    private void AddEdge(string from, string to, AirwaySegment s)
    {
        if (!_edges.TryGetValue(from, out var list)) _edges[from] = list = [];
        list.Add(new AirwayEdge(to, s));
    }
}
