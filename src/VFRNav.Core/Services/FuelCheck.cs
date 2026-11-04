using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

public enum FuelStatus
{
    /// <summary>Le carburant de la route tient dans le réservoir, réserve comprise.</summary>
    Enough,

    /// <summary>La route tient dans le réservoir, mais pas en gardant la réserve de l'appareil.</summary>
    EatsReserve,

    /// <summary>La route consomme plus que le réservoir : impossible sans escale.</summary>
    Insufficient,
}

/// <param name="NeededL">Carburant de la route, d'après le log de navigation.</param>
/// <param name="UsableL">Carburant disponible avant d'entamer la réserve (capacité moins réserve).</param>
/// <param name="ShortfallL">Ce qui manque : pour garder la réserve (EatsReserve), ou au-delà du réservoir (Insufficient).</param>
/// <param name="ShortfallMinutes">Le manque en minutes de vol à la consommation de l'appareil.</param>
/// <param name="ShortfallNm">Le manque en milles, à la vitesse sol moyenne de la route.</param>
public sealed record FuelCheckResult(
    FuelStatus Status, double NeededL, double UsableL, double ShortfallL, double ShortfallMinutes, double ShortfallNm, double ReserveMinutes);

/// <summary>Compare le carburant d'une route au réservoir et à la réserve d'un appareil.</summary>
public static class FuelCheck
{
    public static FuelCheckResult Evaluate(Aircraft aircraft, FlightMode mode, NavLog log)
    {
        double reserveMin = aircraft.ReserveFor(mode);
        double reserveL = reserveMin / 60 * aircraft.BurnLph;
        double usableL = Math.Max(0, aircraft.FuelCapacityL - reserveL);
        double needed = log.TotalFuelL;

        var status = needed <= usableL ? FuelStatus.Enough : needed <= aircraft.FuelCapacityL ? FuelStatus.EatsReserve : FuelStatus.Insufficient;
        double shortfall = status switch
        {
            FuelStatus.EatsReserve => needed - usableL,
            FuelStatus.Insufficient => needed - aircraft.FuelCapacityL,
            _ => 0,
        };

        double minutes = aircraft.BurnLph > 0 ? shortfall / aircraft.BurnLph * 60 : 0;
        // Vitesse sol moyenne de la route (vent compris) ; à défaut, la vitesse de croisière.
        double groundSpeed = log.TotalMinutes > 0 ? log.TotalDistanceNm / (log.TotalMinutes / 60) : aircraft.CruiseKt;
        return new FuelCheckResult(status, needed, usableL, shortfall, minutes, minutes / 60 * groundSpeed, reserveMin);
    }

    /// <summary>Phrase d'alerte pour le panneau ou la feuille ; null quand le carburant suffit, réserve comprise.</summary>
    public static string? Message(Aircraft aircraft, FlightMode mode, NavLog log)
    {
        var fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
        var r = Evaluate(aircraft, mode, log);
        return r.Status switch
        {
            FuelStatus.EatsReserve => string.Create(fr,
                $"⚠ la route entame la réserve de {r.ReserveMinutes:0} min de {aircraft.Name} : il manque {r.ShortfallL:N0} L pour la garder (environ {r.ShortfallMinutes:0} min ou {r.ShortfallNm:N0} NM)"),
            FuelStatus.Insufficient => string.Create(fr,
                $"⚠ la route dépasse les {aircraft.FuelCapacityL:N0} L de {aircraft.Name} de {r.ShortfallL:N0} L (environ {r.ShortfallMinutes:0} min ou {r.ShortfallNm:N0} NM) : escale ou autre appareil nécessaire"),
            _ => null,
        };
    }
}
