using System.Globalization;

namespace VFRNav.Core.Ifr;

/// <summary>
/// Un point d'une procédure tel que MSFS le donne (données d'installation SimConnect). Les altitudes sont en mètres,
/// comme dans le simulateur ; <see cref="AltDesc"/> dit comment les lire : 0 aucune, 1 « à », 2 « au moins », 3 « au plus », 4 « entre ».
/// </summary>
public sealed record FacilityLeg(int LegType, string Fix, double Lat, double Lon, int AltDesc, double Alt1Meters, double Alt2Meters, double SpeedKt,
    string Region = "", char FixType = 'W')
{
    /// <summary>Clé du point dans la base IFR : le nom seul en France, « NOM/RÉGION » ailleurs.</summary>
    public string Key => Fix.Length == 0 ? "" : FixKey.Make(Fix, Region);

    public FixKind Kind => FixType switch { 'V' => FixKind.Vor, 'N' => FixKind.Ndb, 'A' => FixKind.Airport, _ => FixKind.Waypoint };
}

/// <summary>Une transition d'une procédure : par piste (numéro et lettre) ou en route (nom du point d'entrée ou de sortie).</summary>
public sealed class FacilityTransition
{
    public string Name { get; init; } = "";

    /// <summary>Piste d'une transition de piste, ex. « 26L » ; vide pour une transition en route.</summary>
    public string Runway { get; init; } = "";

    public List<FacilityLeg> Legs { get; } = [];
}

/// <summary>Une SID ou une STAR comme MSFS la décrit : points communs, transitions de piste et transitions en route.</summary>
public sealed class FacilityProcedure
{
    public ProcedureKind Kind { get; init; }
    public string Name { get; init; } = "";
    public List<FacilityLeg> Common { get; } = [];
    public List<FacilityTransition> RunwayTransitions { get; } = [];
    public List<FacilityTransition> EnrouteTransitions { get; } = [];
}

/// <summary>Résultat de la conversion : les procédures du modèle IFR et les points qu'elles utilisent (avec leurs coordonnées).</summary>
public sealed record SimProcedureImport(IReadOnlyList<IfrProcedure> Procedures, IReadOnlyList<IfrFix> Fixes);

/// <summary>Convertit les procédures lues dans MSFS en procédures du modèle IFR de MSFS Rhumb.</summary>
public static class SimProcedures
{
    private const double MetersToFeet = 3.28084;

    /// <summary>Piste en texte : numéro sur deux chiffres puis lettre (0 aucune, 1 L, 2 R, 3 C, 4 W, 5 A, 6 B).</summary>
    public static string RunwayName(int number, int designator)
    {
        if (number <= 0) return "";
        string letter = designator switch { 1 => "L", 2 => "R", 3 => "C", 4 => "W", 5 => "A", 6 => "B", _ => "" };
        return number.ToString("00", CultureInfo.InvariantCulture) + letter;
    }

    public static SimProcedureImport ToIfr(string airport, IEnumerable<FacilityProcedure> procedures)
    {
        var result = new List<IfrProcedure>();
        var fixes = new Dictionary<string, IfrFix>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in procedures)
        {
            var variants = p.Kind == ProcedureKind.Sid ? SidVariants(p) : StarVariants(p);
            if (variants.Count == 0) continue;
            result.Add(new IfrProcedure(p.Kind, airport.ToUpperInvariant(), p.Name, variants));
            foreach (var leg in AllLegs(p).Where(l => l.Fix.Length > 0 && HasPosition(l)))
                fixes.TryAdd(leg.Key, new IfrFix(leg.Key, leg.Lat, leg.Lon, leg.Kind));
        }
        return new SimProcedureImport(result, [.. fixes.Values]);
    }

    private static IEnumerable<FacilityLeg> AllLegs(FacilityProcedure p) =>
        p.Common.Concat(p.RunwayTransitions.SelectMany(t => t.Legs)).Concat(p.EnrouteTransitions.SelectMany(t => t.Legs));

    private static bool HasPosition(FacilityLeg l) => Math.Abs(l.Lat) > 1e-6 || Math.Abs(l.Lon) > 1e-6;

    /// <summary>SID : de la piste au point de sortie. Piste, puis points communs, puis transition en route.</summary>
    private static List<ProcedureVariant> SidVariants(FacilityProcedure p)
    {
        var variants = new List<ProcedureVariant>();
        var runways = p.RunwayTransitions.Count > 0 ? p.RunwayTransitions : [null];
        var exits = p.EnrouteTransitions.Count > 0 ? p.EnrouteTransitions : [null];
        foreach (var runway in runways)
            foreach (var exit in exits)
                Add(variants, runway is null ? [] : [runway.Runway], Concat(runway?.Legs, p.Common, exit?.Legs));
        return Dedupe(variants);
    }

    /// <summary>STAR : du point d'entrée vers la piste. Transition en route, puis points communs, puis piste.</summary>
    private static List<ProcedureVariant> StarVariants(FacilityProcedure p)
    {
        var variants = new List<ProcedureVariant>();
        var entries = p.EnrouteTransitions.Count > 0 ? p.EnrouteTransitions : [null];
        var runways = p.RunwayTransitions.Count > 0 ? p.RunwayTransitions : [null];
        foreach (var entry in entries)
            foreach (var runway in runways)
                Add(variants, runway is null ? [] : [runway.Runway], Concat(entry?.Legs, p.Common, runway?.Legs));
        return Dedupe(variants);
    }

    private static IEnumerable<FacilityLeg> Concat(IEnumerable<FacilityLeg>? a, IEnumerable<FacilityLeg> b, IEnumerable<FacilityLeg>? c) =>
        (a ?? []).Concat(b).Concat(c ?? []);

    private static void Add(List<ProcedureVariant> into, IReadOnlyList<string> runways, IEnumerable<FacilityLeg> legs)
    {
        var points = new List<ProcedureLeg>();
        foreach (var leg in legs)
        {
            // Un point sans nom (montée au cap, interception…) ne peut pas servir de repère sur la carte : on l'ignore.
            if (leg.Fix.Length == 0 || !HasPosition(leg)) continue;
            if (points.Count > 0 && points[^1].Fix.Equals(leg.Key, StringComparison.OrdinalIgnoreCase))
            {
                points[^1] = Merge(points[^1], leg); // le point de raccord figure souvent à la fin d'un tronçon et au début du suivant
                continue;
            }
            points.Add(ToLeg(leg));
        }
        if (points.Count > 0) into.Add(new ProcedureVariant([.. runways.Where(r => r.Length > 0)], points));
    }

    private static ProcedureLeg ToLeg(FacilityLeg leg)
    {
        int? min = null, max = null;
        int alt1 = Feet(leg.Alt1Meters), alt2 = Feet(leg.Alt2Meters);
        switch (leg.AltDesc)
        {
            case 1: min = max = alt1; break;
            case 2: min = alt1; break;
            case 3: max = alt1; break;
            case 4: min = Math.Min(alt1, alt2); max = Math.Max(alt1, alt2); break;
        }
        int? speed = leg.SpeedKt >= 1 ? (int)Math.Round(leg.SpeedKt) : null;
        return new ProcedureLeg(leg.Key, min, max, speed);
    }

    private static ProcedureLeg Merge(ProcedureLeg previous, FacilityLeg next)
    {
        var n = ToLeg(next);
        return new ProcedureLeg(previous.Fix, previous.MinAltFt ?? n.MinAltFt, previous.MaxAltFt ?? n.MaxAltFt, previous.SpeedKt ?? n.SpeedKt);
    }

    private static int Feet(double meters) => (int)Math.Round(meters * MetersToFeet);

    /// <summary>Retire les variantes identiques (mêmes pistes, mêmes points) que le produit des transitions peut créer.</summary>
    private static List<ProcedureVariant> Dedupe(List<ProcedureVariant> variants)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. variants.Where(v => seen.Add(string.Join(",", v.Runways) + "|" + string.Join(">", v.Legs.Select(l => l.Fix))))];
    }

    /// <summary>
    /// Ajoute à une base les procédures et les points lus dans MSFS pour un terrain, en remplaçant celles que la base avait déjà pour lui.
    /// Un point déjà connu garde la position de la base (elle vient du SIA) : MSFS ne complète que ceux qui manquent.
    /// </summary>
    public static IfrNavData Merge(IfrNavData data, string airport, SimProcedureImport import) =>
        MergeAll(data, new Dictionary<string, SimProcedureImport> { [airport] = import });

    /// <summary>Comme <see cref="Merge"/> pour plusieurs terrains, avec une seule reconstruction de la base.</summary>
    public static IfrNavData MergeAll(IfrNavData data, IReadOnlyDictionary<string, SimProcedureImport> imports)
    {
        if (imports.Count == 0) return data;
        var airports = new HashSet<string>(imports.Keys, StringComparer.OrdinalIgnoreCase);
        var procedures = data.Procedures
            .Where(p => !airports.Contains(p.Airport))
            .Concat(imports.Values.SelectMany(i => i.Procedures)).ToList();

        return data.WithProcedures(imports.Values.SelectMany(i => i.Fixes), procedures);
    }
}
