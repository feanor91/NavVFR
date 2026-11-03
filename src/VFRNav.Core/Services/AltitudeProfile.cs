using VFRNav.Core.Models;

namespace VFRNav.Core.Services;

/// <summary>
/// Altitude prévue tout au long de la route : celle saisie à chaque point (sinon l'altitude de croisière), avec une
/// variation linéaire entre deux points (montée ou descente). Un terrain sans altitude saisie est au sol.
/// </summary>
public sealed class AltitudeProfile
{
    private readonly double[] _distance;
    private readonly double[] _altitude;

    public AltitudeProfile(IReadOnlyList<Waypoint> route, double defaultFt, double climbFtPerNm = GradientFtPerNm, double descentFtPerNm = GradientFtPerNm)
    {
        _distance = new double[route.Count];
        _altitude = new double[route.Count];
        var fixedFt = new double?[route.Count];
        for (int i = 0; i < route.Count; i++)
        {
            _distance[i] = i == 0 ? 0 : _distance[i - 1] + NavMath.DistanceNm(route[i - 1], route[i]);
            // Altitude saisie, ou au sol pour un terrain : ces points-là ne bougent pas.
            fixedFt[i] = route[i].AltitudeFt ?? (route[i].IsAirport ? route[i].ElevationFt : null);
        }
        // Les autres points (ceux d'une SID ou d'une STAR sans contrainte, par exemple) montent ou descendent à pente
        // réaliste depuis les points fixés, sans dépasser la croisière : on ne passe pas du sol au FL350 en un point.
        for (int i = 0; i < route.Count; i++)
        {
            if (fixedFt[i] is { } ft) { _altitude[i] = ft; continue; }
            double alt = defaultFt;
            for (int j = 0; j < route.Count; j++)
            {
                if (fixedFt[j] is not { } fj) continue;
                // Un point fixé avant celui-ci : on en monte ; un point fixé après : on y descend.
                alt = Math.Min(alt, fj + (_distance[j] <= _distance[i] ? climbFtPerNm : descentFtPerNm) * Math.Abs(_distance[i] - _distance[j]));
            }
            _altitude[i] = alt;
        }
        DefaultFt = defaultFt;
        ClimbFtPerNm = climbFtPerNm;
        DescentFtPerNm = descentFtPerNm;
        MaxFt = _altitude.Length == 0 ? defaultFt : _altitude.Max();
        MinFt = _altitude.Length == 0 ? defaultFt : _altitude.Min();
        HasCustom = route.Any(w => w.AltitudeFt is not null);
    }

    /// <summary>Pente par défaut, en montée comme en descente, entre un point fixé et ceux qui n'ont pas d'altitude (environ 3°).</summary>
    public const double GradientFtPerNm = 300;

    public double ClimbFtPerNm { get; }
    public double DescentFtPerNm { get; }

    public double DefaultFt { get; }
    public double MaxFt { get; }
    public double MinFt { get; }

    /// <summary>Vrai si au moins un point a une altitude saisie.</summary>
    public bool HasCustom { get; }

    /// <summary>Vrai si l'altitude est la même partout.</summary>
    public bool IsFlat => MaxFt - MinFt < 1;

    public int Count => _altitude.Length;
    public double DistanceAt(int index) => _distance[index];
    public double AtWaypoint(int index) => _altitude[index];

    /// <summary>Altitude à une distance donnée depuis le départ (avant le départ et après l'arrivée : celle du point le plus proche).</summary>
    public double AtDistance(double nm)
    {
        if (_altitude.Length == 0) return DefaultFt;
        if (nm <= _distance[0]) return _altitude[0];
        for (int i = 1; i < _distance.Length; i++)
        {
            if (nm > _distance[i]) continue;
            double span = _distance[i] - _distance[i - 1];
            return span <= 0 ? _altitude[i] : _altitude[i - 1] + (_altitude[i] - _altitude[i - 1]) * (nm - _distance[i - 1]) / span;
        }
        return _altitude[^1];
    }
}
