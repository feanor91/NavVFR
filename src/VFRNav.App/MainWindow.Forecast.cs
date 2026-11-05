using System.IO;
using System.Net.Http;
using System.Windows;
using VFRNav.Core.Localization;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Weather;

namespace VFRNav.App;

// ---------- Heure de départ, heures de passage et prévisions météo le long de la route (Open-Meteo) ----------
public partial class MainWindow
{
    private ForecastService? _forecastService;
    private IReadOnlyList<ForecastRow> _forecastRows = [];
    private IReadOnlyList<PointForecast?>? _lastForecasts;
    private AltitudeProfile? _lastForecastAltitudes;
    private string _forecastSignature = "";

    /// <summary>Vent prévu de chaque tronçon, valable pour la route décrite par <see cref="_forecastWindsSignature"/> ; null = vent supposé.</summary>
    private IReadOnlyList<(double Dir, double Kt)?>? _forecastWinds;
    private string _forecastWindsSignature = "";

    /// <summary>Heure de départ : celle saisie (UTC), sinon maintenant (à la minute).</summary>
    private DateTimeOffset DepartureUtc()
    {
        var now = DateTimeOffset.UtcNow;
        if (DepartureTime.TryParse(DepartureBox.Text, now, out var time)) return time;
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
    }

    /// <summary>Ce qui fait changer les vents prévus et les heures : la route, les altitudes, l'heure de départ.</summary>
    private string WindSignature(IReadOnlyList<Waypoint> route, double cruiseFt) =>
        Signature(route, cruiseFt) + "|" + string.Join(",", route.Select(w => w.AltitudeFt)) + "|" + DepartureBox.Text.Trim();

    /// <summary>Log de la route avec le vent prévu (si demandé et chargé pour cette route) et l'heure de passage de chaque tronçon.</summary>
    private NavLog ComputeRouteLog(IReadOnlyList<Waypoint> route, FlightParameters p)
    {
        var winds = _forecastWinds is not null && _forecastWindsSignature == WindSignature(route, p.CruiseAltFt) ? _forecastWinds : null;
        var log = NavMath.ComputeLog(route, p, winds);
        var etas = DepartureTime.Etas(DepartureUtc(), log);
        return log with { Legs = [.. log.Legs.Select((leg, i) => leg with { Eta = DepartureTime.ClockText(etas[i + 1]) })] };
    }

    private void Departure_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded || _loadingParams) return;
        string text = DepartureBox.Text.Trim();
        var now = DateTimeOffset.UtcNow;
        DepartureHint.Text = text.Length == 0 ? Loc.F("Forecast_Depart_maintenant", DepartureTime.ClockText(DepartureUtc()))
            : DepartureTime.TryParse(text, now, out var t) ? Loc.F("Forecast_Depart_hint", DepartureTime.Format(t))
            : Loc.F("Forecast_Depart_invalide", text);
        RefreshLog();
        ScheduleLive();
    }

    private void DepartureNow_Click(object sender, RoutedEventArgs e) => DepartureBox.Text = "";

    private void ForecastWind_Click(object sender, RoutedEventArgs e)
    {
        ApplyForecastWinds();
        RefreshLog();
        ScheduleLive();
    }

    /// <summary>Met à jour le vent par tronçon à partir des dernières prévisions, selon la case à cocher.</summary>
    private void ApplyForecastWinds()
    {
        if (ForecastWindCheck.IsChecked == true && _lastForecasts is not null && _lastForecastAltitudes is not null)
        {
            _forecastWinds = ForecastTable.LegWinds(_lastForecastAltitudes, _lastForecasts);
            _forecastWindsSignature = WindSignature(_route.ToList(), ReadParams().CruiseAltFt);
        }
        else _forecastWinds = null;
    }

    private void ClearForecast(string status)
    {
        _forecastRows = [];
        _lastForecasts = null;
        _lastForecastAltitudes = null;
        _forecastSignature = "";
        ForecastGrid.ItemsSource = null;
        ForecastStatus.Text = status;
        if (_forecastWinds is not null) { _forecastWinds = null; RefreshLog(); }
    }

    /// <summary>Charge les prévisions de chaque point de la route à l'heure de passage et remplit l'onglet Prévisions.</summary>
    private async Task ShowForecastAsync(List<Waypoint> route, FlightParameters p, CancellationToken ct)
    {
        if (route.Count < 2) { ClearForecast(Loc.Get("Forecast_Pas_de_route")); return; }

        // Les heures de passage servent à choisir l'heure de prévision de chaque point : calculées avec le vent supposé,
        // pour que le vent prévu (qui en dépend) ne les fasse pas bouger à chaque calcul.
        var etas = DepartureTime.Etas(DepartureUtc(), NavMath.ComputeLog(route, p));
        var altitudes = NewAltitudeProfile(route, p.CruiseAltFt);
        string signature = string.Join("|", route.Select((w, i) => $"{w.Lat:0.0000},{w.Lon:0.0000}@{etas[i]:yyyyMMddHH}@{altitudes.AtWaypoint(i):0}"));
        if (signature == _forecastSignature && _lastForecasts is not null) return;   // rien n'a changé : pas de nouvelle requête

        ForecastStatus.Text = Loc.Get("Forecast_Chargement");
        IReadOnlyList<PointForecast?> forecasts;
        try { forecasts = await (_forecastService ??= new ForecastService(_http)).ForecastAsync(ForecastTable.Requests(route, etas), ct); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            ClearForecast(Loc.F("Forecast_Indisponible_msg", ex.Message));
            return;
        }
        ct.ThrowIfCancellationRequested();

        _forecastRows = ForecastTable.Rows(route, etas, altitudes, forecasts);
        _lastForecasts = forecasts;
        _lastForecastAltitudes = altitudes;
        _forecastSignature = signature;
        ForecastGrid.ItemsSource = _forecastRows;
        ApplyForecastWinds();
        RefreshLog();

        string from = route[0].Icao ?? route[0].Name, to = route[^1].Icao ?? route[^1].Name;
        ForecastStatus.Text = Loc.F("Forecast_Status", from, to, DepartureTime.Format(etas[0]), _forecastRows.Count(r => r.HasAdvisory));
    }
}
