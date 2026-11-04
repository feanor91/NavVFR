using System.Globalization;
using VFRNav.Core.Services;

namespace VFRNav.Core.Ifr;

/// <summary>Un ILS d'une piste, d'après l'export AIXM du SIA : localizer, pente de descente et seuil de piste.</summary>
/// <param name="Runway">Piste desservie, ex. « 26L ».</param>
/// <param name="Category">I, II, III, ou « NOCAT ».</param>
/// <param name="GlideSlopeDeg">Pente de descente en degrés ; null si le SIA ne la donne pas.</param>
/// <param name="TrueBearingDeg">Axe de la piste, en degrés vrais.</param>
/// <param name="MagneticBearingDeg">Axe de la piste, en degrés magnétiques (le QDM de l'ILS) ; null si inconnu.</param>
/// <param name="ElevationFt">Altitude de la zone de toucher (antenne de pente) ; null si inconnue.</param>
/// <param name="ReferenceHeightM">Hauteur de l'axe de pente au-dessus du seuil (RDH), en mètres ; null si inconnue.</param>
public sealed record IlsApproach(
    string Airport, string Runway, string Category, string LocalizerIdent, double LocalizerMhz, double? GlideSlopeDeg,
    double ThresholdLat, double ThresholdLon, double TrueBearingDeg, double? MagneticBearingDeg, double? ElevationFt, double? ReferenceHeightM);

/// <summary>Les ILS connus, par aérodrome (SIA : France seulement).</summary>
public sealed class IlsCatalog(IEnumerable<IlsApproach> approaches)
{
    public static IlsCatalog Empty { get; } = new([]);

    public IReadOnlyList<IlsApproach> All { get; } = approaches.ToList();

    public IReadOnlyList<IlsApproach> For(string airport) =>
        All.Where(i => i.Airport.Equals(airport.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    public IlsApproach? Find(string airport, string runway) =>
        For(airport).FirstOrDefault(i => i.Runway.Equals(runway.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Point de la finale ILS : nom, position, altitude sur la pente et distance au seuil.</summary>
public sealed record IlsFinalPoint(string Ident, double Lat, double Lon, int AltitudeFt, double DistanceNm);

/// <summary>Finale ILS construite pour une piste : ses points et le texte qui la décrit.</summary>
public sealed record IlsFinal(IlsApproach Ils, IReadOnlyList<IlsFinalPoint> Points, string Summary);

/// <summary>
/// Finale ILS <b>indicative</b> : la route rejoint l'axe du localizer à 10 NM du seuil, puis suit la pente jusqu'à la piste.
/// Ce n'est pas la procédure publiée (ses points IAF et FAF, ses altitudes et ses minima ne sont pas dans les données du SIA) :
/// c'est la finale « guidée par le contrôle ». À ne jamais utiliser pour un vol réel.
/// </summary>
public static class IlsPlanner
{
    /// <summary>Distance au seuil du point d'interception de l'axe, et du point de finale, en milles.</summary>
    public const double InterceptNm = 10, FinalFixNm = 5;

    private const double FeetPerNm = 6076.12;
    private const double DefaultSlopeDeg = 3;
    private const double DefaultReferenceHeightM = 15;      // ≈ 50 ft au-dessus du seuil
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Altitude sur la pente à une distance du seuil, arrondie à 100 ft.</summary>
    public static int GlidePathAltitudeFt(IlsApproach ils, double fieldElevationFt, double distanceNm)
    {
        double slope = ils.GlideSlopeDeg is > 0 and < 10 ? ils.GlideSlopeDeg.Value : DefaultSlopeDeg;
        double reference = (ils.ReferenceHeightM ?? DefaultReferenceHeightM) * 3.28084;
        double elevation = ils.ElevationFt ?? fieldElevationFt;
        double altitude = elevation + reference + distanceNm * FeetPerNm * Math.Tan(slope * Math.PI / 180);
        return (int)Math.Round(altitude / 100) * 100;
    }

    /// <summary>Points d'interception et de finale sur l'axe prolongé de la piste, avec leur altitude sur la pente.</summary>
    public static IlsFinal Build(IlsApproach ils, double fieldElevationFt)
    {
        double outbound = (ils.TrueBearingDeg + 180) % 360;     // de la piste vers l'amont : l'avion arrive en sens inverse
        var points = new List<IlsFinalPoint>();
        foreach (double nm in new[] { InterceptNm, FinalFixNm })
        {
            var (lat, lon) = RouteGeometry.Destination(ils.ThresholdLat, ils.ThresholdLon, outbound, nm);
            points.Add(new IlsFinalPoint($"ILS{ils.Runway}-{nm:0}", lat, lon, GlidePathAltitudeFt(ils, fieldElevationFt, nm), nm));
        }
        return new IlsFinal(ils, points, Summary(ils, points));
    }

    private static string Summary(IlsApproach ils, IReadOnlyList<IlsFinalPoint> points)
    {
        string qdm = ils.MagneticBearingDeg is { } m ? $" · QDM {Math.Round(m):000}°" : $" · axe {Math.Round(ils.TrueBearingDeg):000}° vrai";
        string slope = ils.GlideSlopeDeg is { } s ? $" · pente {s.ToString("0.0#", Fr)}°" : "";
        string category = ils.Category is "NOCAT" or "" ? "" : $" · cat {ils.Category}";
        var intercept = points[0];
        var final = points[1];
        return string.Create(Fr,
            $"ILS {ils.Runway} {ils.LocalizerIdent} {ils.LocalizerMhz:0.00} MHz{qdm}{slope}{category} · interception à {intercept.DistanceNm:0} NM ({intercept.AltitudeFt:N0} ft), " +
            $"finale à {final.DistanceNm:0} NM ({final.AltitudeFt:N0} ft) · finale indicative, pas la procédure publiée");
    }

    /// <summary>
    /// Piste ILS qui demande le moins de virage : celle dont l'axe est le plus proche de la direction d'arrivée
    /// (cap vrai du dernier point de la route vers le terrain).
    /// </summary>
    public static IlsApproach? ChooseRunway(IEnumerable<IlsApproach> candidates, double approachTrueBearingDeg) =>
        candidates.OrderBy(i => AngleDifference(i.TrueBearingDeg, approachTrueBearingDeg)).ThenBy(i => i.Runway, StringComparer.Ordinal).FirstOrDefault();

    private static double AngleDifference(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>
    /// Ajoute la finale ILS à une route IFR, juste avant le terrain d'arrivée. Sans piste demandée, la piste ILS la plus alignée
    /// avec l'arrivée est choisie. Un point de la finale n'est ajouté que s'il est plus près du seuil que le point précédent de la route
    /// (une STAR qui finit à 6 NM du seuil ne reçoit pas de point à 10 NM : la route ne reviendrait pas en arrière).
    /// </summary>
    /// <returns>La route (inchangée s'il n'y a rien à ajouter), la finale, et un message quand l'ILS n'a pas pu être ajouté.</returns>
    public static (IfrRoute Route, IlsFinal? Final, string? Note) Apply(
        IfrRoute route, IlsCatalog catalog, string? requestedRunway, double fieldElevationFt)
    {
        if (route.Points.Count < 2 || route.Points[^1].Source != IfrLegSource.Airport) return (route, null, null);

        string airport = FixKey.Display(route.Points[^1].Ident);
        var candidates = catalog.For(airport);
        if (candidates.Count == 0)
            return (route, null, $"Pas d'ILS dans les données pour {airport} (le SIA ne couvre que la France).");

        IlsApproach? ils;
        var previous = route.Points[^2];
        if (requestedRunway is not null)
        {
            ils = catalog.Find(airport, requestedRunway);
            if (ils is null)
                return (route, null, $"La piste {requestedRunway} de {airport} n'a pas d'ILS. Pistes avec ILS : {string.Join(", ", candidates.Select(c => c.Runway).Order())}.");
        }
        else
        {
            var last = route.Points[^1];
            double bearing = NavMath.TrueCourse(new Models.Waypoint { Lat = previous.Lat, Lon = previous.Lon }, new Models.Waypoint { Lat = last.Lat, Lon = last.Lon });
            ils = ChooseRunway(candidates, bearing)!;
        }

        var final = Build(ils, fieldElevationFt);
        double previousDistance = NavMath.DistanceNm(previous.Lat, previous.Lon, ils.ThresholdLat, ils.ThresholdLon);
        // Ordre de la route : du plus loin au plus près du seuil.
        var kept = final.Points.Where(p => p.DistanceNm < previousDistance - 1.5).ToList();
        final = final with { Points = kept };

        if (kept.Count == 0)
            return (route, final, $"L'ILS {ils.Runway} existe, mais la route finit déjà à {previousDistance:0} NM du seuil : aucun point de finale ajouté.");

        var points = route.Points.Take(route.Points.Count - 1)
            .Concat(kept.Select(p => new IfrRoutePoint(p.Ident, p.Lat, p.Lon, IfrLegSource.Ils, null, p.AltitudeFt, p.AltitudeFt)))
            .Append(route.Points[^1]).ToList();

        double distance = 0;
        for (int i = 1; i < points.Count; i++) distance += NavMath.DistanceNm(points[i - 1].Lat, points[i - 1].Lon, points[i].Lat, points[i].Lon);

        // « … STAR LFBB » devient « … STAR ILS32R LFBB ».
        string description = route.Description;
        int cut = description.LastIndexOf(' ');
        if (cut > 0) description = description[..cut] + $" ILS{ils.Runway}" + description[cut..];

        return (route with { Points = points, DistanceNm = distance, Description = description }, final, null);
    }
}
