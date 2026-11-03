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

    public string? Notes { get; set; }

    public bool Supports(FlightMode mode) => mode == FlightMode.Vfr ? Vfr : Ifr;

    /// <summary>Autonomie à la consommation de croisière, en minutes (0 si la consommation est nulle).</summary>
    public double EnduranceMinutes => BurnLph > 0 ? FuelCapacityL / BurnLph * 60 : 0;

    /// <summary>Rayon d'action théorique en vent nul, sans réserve, en milles nautiques.</summary>
    public double RangeNm => EnduranceMinutes / 60 * CruiseKt;

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
        if (ServiceCeilingFt is { } ceiling && !(ceiling >= CruiseAltFt))
            errors.Add("Le plafond ne peut pas être inférieur à l'altitude de croisière.");
        return errors;
    }
}
