using System.Globalization;
using VFRNav.Core.Localization;
using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>
/// Profil vertical d'un vol : altitude de croisière réellement atteignable sur la distance, début de la descente (TOD), fin de la montée (TOC).
/// </summary>
/// <param name="RequestedFt">Altitude demandée par le pilote.</param>
/// <param name="CruiseFt">Altitude retenue : la demandée si l'avion peut l'atteindre, sinon la plus haute atteignable.</param>
/// <param name="Limited">Vrai si la distance est trop courte pour la croisière demandée.</param>
/// <param name="TopOfClimbNm">Distance depuis le départ où la croisière est atteinte.</param>
/// <param name="TopOfDescentNm">Distance depuis le départ où la descente commence.</param>
public sealed record VerticalPlan(
    double DistanceNm, double RequestedFt, double CruiseFt, bool Limited,
    double TopOfClimbNm, double TopOfDescentNm, double ClimbMinutes, double DescentMinutes)
{
    /// <summary>Distance du début de la descente à l'arrivée.</summary>
    public double TopOfDescentFromArrivalNm => Math.Max(0, DistanceNm - TopOfDescentNm);

    /// <summary>Niveau de vol (centaines de pieds) de la croisière retenue.</summary>
    public int CruiseFl => (int)Math.Round(CruiseFt / 100);
}

/// <summary>
/// Calcule ce que l'avion peut faire sur une distance donnée. Montée et descente suivent les pentes moyennes de l'appareil
/// (<see cref="Aircraft.ClimbFtPerNm"/>, <see cref="Aircraft.DescentFtPerNm"/>) ; une marge de <see cref="TerminalAllowanceNm"/> couvre les départs
/// et approches (limitations de vitesse, guidage radar) pendant lesquels on ne monte ni ne descend à la pente moyenne.
/// Estimation de planification : pas un calcul de performances d'après le manuel.
/// </summary>
public static class VerticalPlanner
{
    /// <summary>Distance perdue au total (départ et approche) : vitesse limitée à 250 kt, paliers, vecteurs radar.</summary>
    public const double TerminalAllowanceNm = 20;

    /// <summary>Longueur minimale de la croisière : en dessous, on ne monte pas plus haut.</summary>
    public const double MinCruiseNm = 5;

    /// <summary>
    /// Altitude de croisière atteignable (pieds) : celle où la montée depuis le départ et la descente vers l'arrivée tiennent dans la distance.
    /// </summary>
    public static double MaxReachableFt(double distanceNm, double departureElevationFt, double arrivalElevationFt, Aircraft aircraft)
    {
        double up = aircraft.ClimbFtPerNm, down = aircraft.DescentFtPerNm;
        double usable = distanceNm - TerminalAllowanceNm - MinCruiseNm;
        double h = (usable + departureElevationFt / up + arrivalElevationFt / down) / (1 / up + 1 / down);
        return Math.Max(h, Math.Max(departureElevationFt, arrivalElevationFt));
    }

    /// <summary>Dernier niveau (multiple de 1 000 ft) à ou sous <paramref name="ft"/> ; avec une route, celui de la règle semi-circulaire (impair vers l'est, pair vers l'ouest).</summary>
    public static double RoundDownToLevel(double ft, double? magneticTrack)
    {
        double thousands = Math.Floor(ft / 1000);
        if (magneticTrack is { } track && ft >= 3000)
        {
            bool east = NavMath.Norm360(track) < 180;
            bool odd = thousands % 2 != 0;
            if (east != odd) thousands -= 1;      // vers l'est : milliers impairs ; vers l'ouest : pairs
        }
        return Math.Max(thousands, 0) * 1000;
    }

    /// <param name="magneticTrack">Route magnétique du départ vers l'arrivée, pour la règle semi-circulaire ; null = sans.</param>
    public static VerticalPlan Compute(double distanceNm, double departureElevationFt, double arrivalElevationFt, double requestedFt, Aircraft aircraft, double? magneticTrack = null)
    {
        double reachable = MaxReachableFt(distanceNm, departureElevationFt, arrivalElevationFt, aircraft);
        double ceiling = aircraft.ServiceCeilingFt ?? double.MaxValue;
        double cruise = requestedFt;
        bool limited = false;
        if (requestedFt > reachable || requestedFt > ceiling)
        {
            limited = true;
            cruise = RoundDownToLevel(Math.Min(reachable, ceiling), magneticTrack);
            double floor = Math.Max(departureElevationFt, arrivalElevationFt) + 1000;
            if (cruise < floor) cruise = Math.Min(requestedFt, floor);   // trop court pour monter : on reste bas
        }

        double climbNm = Math.Max(0, (cruise - departureElevationFt) / aircraft.ClimbFtPerNm);
        double descentNm = Math.Max(0, (cruise - arrivalElevationFt) / aircraft.DescentFtPerNm);
        double toc = Math.Min(climbNm, distanceNm);
        double tod = Math.Max(toc, distanceNm - descentNm);
        return new VerticalPlan(distanceNm, requestedFt, cruise, limited, toc, tod,
            Math.Max(0, cruise - departureElevationFt) / aircraft.ClimbRateFpm, Math.Max(0, cruise - arrivalElevationFt) / aircraft.DescentRateFpm);
    }

    /// <summary>« FL260 » pour une altitude en pieds au-dessus de la transition (FL100 et plus), « 5 500 ft » dessous.</summary>
    public static string LevelText(double ft) =>
        ft >= 10000 ? $"FL{Math.Round(ft / 100):000}" : $"{ft.ToString("N0", Loc.FormatCulture)} ft";

    /// <summary>Lignes de synthèse du profil vertical, localisées (altitude atteignable, TOC, TOD, durées).</summary>
    public static IReadOnlyList<string> Lines(VerticalPlan plan, string departure, string arrival, string aircraftName)
    {
        var fr = Loc.FormatCulture;
        var lines = new List<string>();
        lines.Add(plan.Limited
            ? Loc.F(fr, "Vertical_Limite", LevelText(plan.RequestedFt), plan.DistanceNm.ToString("0", fr), aircraftName, LevelText(plan.CruiseFt))
            : Loc.F(fr, "Vertical_Atteignable", LevelText(plan.CruiseFt), plan.DistanceNm.ToString("0", fr)));
        lines.Add(Loc.F(fr, "Vertical_Montee", LevelText(plan.CruiseFt), NavMath.FormatTime(plan.ClimbMinutes), plan.TopOfClimbNm.ToString("0", fr), departure));
        lines.Add(Loc.F(fr, "Vertical_Descente", NavMath.FormatTime(plan.DescentMinutes), plan.TopOfDescentFromArrivalNm.ToString("0", fr), arrival));
        return lines;
    }

    /// <summary>« V1 140 · VR 143 · V2 147 kt » avec les vitesses connues, ou null si l'appareil n'en a aucune.</summary>
    public static string? TakeoffSpeedsLine(Aircraft aircraft)
    {
        var parts = new List<string>();
        if (aircraft.V1Kt is { } v1) parts.Add($"V1 {v1:0}");
        if (aircraft.VrKt is { } vr) parts.Add($"VR {vr:0}");
        if (aircraft.V2Kt is { } v2) parts.Add($"V2 {v2:0}");
        return parts.Count == 0 ? null : Loc.F(Loc.FormatCulture, "Vertical_Vitesses_decollage", string.Join(" · ", parts));
    }
}
