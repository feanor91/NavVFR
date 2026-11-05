using System.Globalization;
using VFRNav.Core.Localization;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Core.Weather;

/// <summary>Une ligne du tableau de prévisions : un point de la route, à l'heure où l'on y passe.</summary>
public sealed record ForecastRow(
    int Number, string Point, string Eta, string AltitudeFt, string Wind, string Temperature, string Clouds,
    string Visibility, string Precipitation, string FreezingLevel, string Weather, string Advisories, bool HasAdvisory);

/// <summary>Met les prévisions d'Open-Meteo en forme pour la route : tableau par point, vent par tronçon pour le log de navigation.</summary>
public static class ForecastTable
{
    private const string None = "—";

    /// <summary>Requêtes (point et heure de passage) pour chaque point de la route.</summary>
    public static IReadOnlyList<ForecastRequest> Requests(IReadOnlyList<Waypoint> route, IReadOnlyList<DateTimeOffset> etas) =>
        [.. route.Select((w, i) => new ForecastRequest(w.Lat, w.Lon, etas[i]))];

    public static IReadOnlyList<ForecastRow> Rows(IReadOnlyList<Waypoint> route, IReadOnlyList<DateTimeOffset> etas, AltitudeProfile altitudes, IReadOnlyList<PointForecast?> forecasts)
    {
        var fr = Loc.FormatCulture;
        var rows = new List<ForecastRow>();
        for (int i = 0; i < route.Count; i++)
        {
            double alt = altitudes.AtWaypoint(i);
            string name = route[i].Icao ?? route[i].Name;
            string eta = DepartureTime.ClockText(etas[i]);
            if (forecasts[i] is not { } f)
            {
                rows.Add(new ForecastRow(i + 1, name, eta, alt.ToString("N0", fr), None, None, None, None, None, None, Loc.Get("Forecast_Indisponible"), "", false));
                continue;
            }

            var atAlt = f.At(alt);
            var advisories = ForecastAssessment.Advisories(f, alt);
            rows.Add(new ForecastRow(
                i + 1, name, eta, alt.ToString("N0", fr),
                $"{atAlt.WindDir:000}°/{atAlt.WindKt:0} kt" + (f.GustKt >= 25 ? Loc.F(fr, "Forecast_Gust", f.GustKt) : ""),
                $"{atAlt.TempC.ToString("0", fr)} °C",
                Loc.F(fr, "Forecast_Clouds", f.CloudLow, f.CloudMid, f.CloudHigh),
                f.VisibilityM is { } v ? (v >= 10000 ? "≥ 10 km" : $"{(v / 1000).ToString("0.#", fr)} km") : None,
                f.PrecipMm >= 0.05 ? $"{f.PrecipMm.ToString("0.#", fr)} mm/h" : "0",
                f.FreezingLevelFt is { } z ? $"{z.ToString("N0", fr)} ft" : None,
                ForecastAssessment.WeatherText(f.WeatherCode),
                string.Join(", ", advisories.Select(a => a.Text)),
                advisories.Count > 0));
        }
        return rows;
    }

    /// <summary>
    /// Vent prévu de chaque tronçon : moyenne (par composantes) des vents aux deux extrémités, pris à l'altitude prévue à chaque point.
    /// Null quand une extrémité n'a pas de prévision : le tronçon garde le vent supposé.
    /// </summary>
    public static IReadOnlyList<(double Dir, double Kt)?> LegWinds(AltitudeProfile altitudes, IReadOnlyList<PointForecast?> forecasts)
    {
        var result = new List<(double, double)?>();
        for (int i = 0; i + 1 < forecasts.Count; i++)
        {
            if (forecasts[i] is not { } a || forecasts[i + 1] is not { } b) { result.Add(null); continue; }
            var wa = a.At(altitudes.AtWaypoint(i));
            var wb = b.At(altitudes.AtWaypoint(i + 1));
            double ua = -wa.WindKt * Math.Sin(wa.WindDir * Math.PI / 180), va = -wa.WindKt * Math.Cos(wa.WindDir * Math.PI / 180);
            double ub = -wb.WindKt * Math.Sin(wb.WindDir * Math.PI / 180), vb = -wb.WindKt * Math.Cos(wb.WindDir * Math.PI / 180);
            double u = (ua + ub) / 2, v = (va + vb) / 2;
            double kt = Math.Sqrt(u * u + v * v);
            result.Add((kt < 0.01 ? 0 : (Math.Atan2(-u, -v) * 180 / Math.PI + 360) % 360, kt));
        }
        return result;
    }
}
