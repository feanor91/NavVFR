namespace VFRNav.Core.Models;

public sealed record LegResult(string From, string To, double DistanceNm, double TrueCourse, double MagneticCourse,
    double WindCorrection, double MagneticHeading, double GroundSpeedKt, double Minutes, double FuelL)
{
    public string Route => $"{From} → {To}";
    public string Time => Services.NavMath.FormatTime(Minutes);

    /// <summary>Heure d'arrivée à la fin du tronçon (UTC, « 14:35 »), quand une heure de départ est connue.</summary>
    public string Eta { get; init; } = "";
}

public sealed record NavLog(IReadOnlyList<LegResult> Legs, double TotalDistanceNm, double TotalMinutes, double TotalFuelL);
