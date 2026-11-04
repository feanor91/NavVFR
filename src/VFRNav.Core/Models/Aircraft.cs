namespace VFRNav.Core.Models;

/// <summary>Type de navigation : à vue ou aux instruments.</summary>
public enum FlightMode { Vfr, Ifr }

public enum AircraftCategory { Piston, Turboprop, Jet }

/// <summary>
/// Un appareil et ses performances de planification. Sert au log de navigation (vitesse, consommation)
/// et à la vérification du carburant. Un appareil peut servir en VFR, en IFR ou aux deux.
/// </summary>
public sealed class Aircraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public AircraftCategory Category { get; set; } = AircraftCategory.Piston;

    /// <summary>Capacités : l'appareil est proposé dans les panneaux VFR et/ou IFR.</summary>
    public bool Vfr { get; set; } = true;
    public bool Ifr { get; set; }

    /// <summary>Vitesse propre de croisière, en nœuds.</summary>
    public double CruiseKt { get; set; } = 100;

    /// <summary>Vitesse maximale, en nœuds.</summary>
    public double MaxKt { get; set; } = 120;

    /// <summary>Consommation en croisière, en litres par heure.</summary>
    public double BurnLph { get; set; } = 30;

    /// <summary>Carburant utilisable, en litres.</summary>
    public double FuelCapacityL { get; set; } = 100;

    /// <summary>Plafond pratique en pieds ; null = non renseigné.</summary>
    public double? ServiceCeilingFt { get; set; }

    /// <summary>Altitude de croisière proposée par défaut, en pieds (35000 = FL350).</summary>
    public double CruiseAltFt { get; set; } = 3000;

    /// <summary>Taux de montée et de descente moyens, en pieds par minute ; null = valeur usuelle de la catégorie.</summary>
    public double? ClimbFpm { get; set; }
    public double? DescentFpm { get; set; }

    /// <summary>Réserve de carburant en minutes de vol, retranchée de la portée utile ; null = valeur usuelle du type de navigation (30 min en VFR, 45 min en IFR).</summary>
    public double? ReserveMinutes { get; set; }

    public string? Notes { get; set; }

    /// <summary>Taux de montée effectif (saisi, sinon usuel de la catégorie), en pieds par minute.</summary>
    public double ClimbRateFpm => ClimbFpm ?? Category switch { AircraftCategory.Jet => 2200, AircraftCategory.Turboprop => 1500, _ => 700 };

    public double DescentRateFpm => DescentFpm ?? Category switch { AircraftCategory.Jet => 2000, AircraftCategory.Turboprop => 1000, _ => 500 };

    /// <summary>Pente moyenne de montée, en pieds par mille nautique (vitesse sol supposée à 80 % de la croisière en montée).</summary>
    public double ClimbFtPerNm => ClimbRateFpm / (CruiseKt * 0.8 / 60);

    /// <summary>Pente moyenne de descente, en pieds par mille nautique (vitesse supposée à 85 % de la croisière).</summary>
    public double DescentFtPerNm => DescentRateFpm / (CruiseKt * 0.85 / 60);

    public bool Supports(FlightMode mode) => mode == FlightMode.Vfr ? Vfr : Ifr;

    /// <summary>Autonomie à la consommation de croisière, en minutes (0 si la consommation est nulle).</summary>
    public double EnduranceMinutes => BurnLph > 0 ? FuelCapacityL / BurnLph * 60 : 0;

    /// <summary>Rayon d'action théorique avec tout le carburant, en vent nul, sans réserve, en milles nautiques.</summary>
    public double RangeNm => EnduranceMinutes / 60 * CruiseKt;

    /// <summary>Réserve retenue pour un type de navigation, en minutes.</summary>
    public double ReserveFor(FlightMode mode) => ReserveMinutes ?? (mode == FlightMode.Ifr ? 45 : 30);

    /// <summary>Autonomie une fois la réserve gardée de côté, en minutes (0 si la réserve consomme tout).</summary>
    public double UsableEnduranceMinutes(FlightMode mode) => Math.Max(0, EnduranceMinutes - ReserveFor(mode));

    /// <summary>Portée utile : distance parcourue en gardant la réserve, en vent nul, en milles nautiques.</summary>
    public double UsableRangeNm(FlightMode mode) => UsableEnduranceMinutes(mode) / 60 * CruiseKt;

    public Aircraft Clone() => (Aircraft)MemberwiseClone();

    /// <summary>Problèmes qui empêchent d'utiliser cette fiche ; vide = valide.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("Le nom est obligatoire.");
        if (!Vfr && !Ifr) errors.Add("Coche au moins une capacité : VFR ou IFR.");
        if (!(CruiseKt > 0)) errors.Add("La vitesse de croisière doit être positive.");
        if (!(MaxKt >= CruiseKt)) errors.Add("La vitesse maximale ne peut pas être inférieure à la vitesse de croisière.");
        if (!(BurnLph > 0)) errors.Add("La consommation doit être positive.");
        if (!(FuelCapacityL > 0)) errors.Add("Le carburant utilisable doit être positif.");
        if (!(CruiseAltFt >= 0)) errors.Add("L'altitude de croisière ne peut pas être négative.");
        if (ClimbFpm is { } c && !(c > 0)) errors.Add("Le taux de montée doit être positif.");
        if (DescentFpm is { } d && !(d > 0)) errors.Add("Le taux de descente doit être positif.");
        if (ReserveMinutes is { } r && !(r >= 0)) errors.Add("La réserve ne peut pas être négative.");
        if (ServiceCeilingFt is { } ceiling && !(ceiling >= CruiseAltFt))
            errors.Add("Le plafond ne peut pas être inférieur à l'altitude de croisière.");
        return errors;
    }
}
