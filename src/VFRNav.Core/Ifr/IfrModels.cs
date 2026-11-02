namespace VFRNav.Core.Ifr;

public enum FixKind { Waypoint, Vor, Ndb, Dme, Airport }

/// <summary>
/// Point de navigation IFR : point désigné, balise ou terrain.
/// <see cref="Ident"/> sert de clé : il doit être unique dans un <see cref="IfrNavData"/>.
/// </summary>
public sealed record IfrFix(string Ident, double Lat, double Lon, FixKind Kind = FixKind.Waypoint);

/// <summary>Sens d'utilisation d'un tronçon de route ATS, par rapport à (From → To).</summary>
public enum AirwayDirection { Both, Forward, Backward }

/// <summary>
/// Tronçon d'une route ATS (airway) entre deux points.
/// Les niveaux sont des niveaux de vol (FL) ; null = pas de limite.
/// </summary>
public sealed record AirwaySegment(
    string Airway, string From, string To,
    AirwayDirection Direction = AirwayDirection.Both,
    int? LowerFl = null, int? UpperFl = null)
{
    public bool AllowsFlightLevel(int? fl) =>
        fl is null || ((LowerFl is null || fl >= LowerFl) && (UpperFl is null || fl <= UpperFl));
}

public enum ProcedureKind { Sid, Star }

/// <summary>Point d'une procédure avec ses contraintes éventuelles (altitudes en pieds, vitesse en nœuds).</summary>
public sealed record ProcedureLeg(string Fix, int? MinAltFt = null, int? MaxAltFt = null, int? SpeedKt = null);

/// <summary>
/// Une façon de voler une procédure : pour une SID, des points qui partent de la piste jusqu'au point de sortie
/// (le dernier) ; pour une STAR, des points qui partent du point d'entrée (le premier) jusqu'à la fin de la STAR.
/// Une procédure à plusieurs transitions donne plusieurs variantes. <see cref="Runways"/> vide = toutes les pistes.
/// </summary>
public sealed record ProcedureVariant(IReadOnlyList<string> Runways, IReadOnlyList<ProcedureLeg> Legs)
{
    public bool AppliesTo(string? runway) =>
        runway is null || Runways.Count == 0 || Runways.Contains(runway, StringComparer.OrdinalIgnoreCase);
}

/// <summary>SID ou STAR d'un terrain, ex. « AMBO1A ».</summary>
public sealed record IfrProcedure(ProcedureKind Kind, string Airport, string Ident, IReadOnlyList<ProcedureVariant> Variants);
