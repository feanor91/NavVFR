namespace VFRNav.Core.Models;

public sealed record LegResult(string From, string To, double DistanceNm, double TrueCourse, double MagneticCourse,
    double WindCorrection, double MagneticHeading, double GroundSpeedKt, double Minutes, double FuelL)
{
    public string Route => $"{From} → {To}";
    public string Time => Services.NavMath.FormatTime(Minutes);
}

public sealed record NavLog(IReadOnlyList<LegResult> Legs, double TotalDistanceNm, double TotalMinutes, double TotalFuelL);
