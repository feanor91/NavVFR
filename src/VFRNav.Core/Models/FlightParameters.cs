namespace VFRNav.Core.Models;

/// <param name="TasKt">Vitesse propre.</param>
/// <param name="BurnLph">Consommation en litres par heure.</param>
/// <param name="CruiseAltFt">Altitude de croisière.</param>
/// <param name="VariationEast">Déclinaison magnétique, Est positif.</param>
/// <param name="WindDir">Direction d'où vient le vent (vrai).</param>
/// <param name="WindKt">Force du vent.</param>
public sealed record FlightParameters(double TasKt, double BurnLph, double CruiseAltFt, double VariationEast, double WindDir, double WindKt);
