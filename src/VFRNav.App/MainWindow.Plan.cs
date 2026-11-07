using System.IO;
using System.Net.Http;
using System.Windows;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Aerodromes;
using VFRNav.Core.Weather;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Calcul du plan de vol : relief, zones, balises, météo, puis feuille de route ----------
public partial class MainWindow
{
    private TerrainService? _terrainService;
    private SigmetService? _sigmetService;
    private ObstacleService? _obstacleService;

    private async void CalculatePlan_Click(object sender, RoutedEventArgs e)
    {
        if (_route.Count < 2) { PlanStatus.Text = Loc.Get("Plan_Il_faut_au_moins_deux"); return; }

        PlanButton.IsEnabled = false;
        using var cts = new CancellationTokenSource();
        using var busy = BeginBusy(Loc.Get("Busy_Calcul_du_plan"), () => cts.Cancel());
        PlanStatus.Text = Loc.Get("Plan_Calcul_du_plan_de_vol");
        SetBusyDetail(PlanStatus.Text);
        try
        {
            var route = _route.ToList();
            var p = ReadParams();
            var log = ComputeRouteLog(route, p);
            var samples = RouteGeometry.Sample(route, 1);

            // Relief (service en ligne) : un échec n'empêche pas la feuille, il y est signalé.
            TerrainProfile? terrain = null;
            string? terrainNote = null;
            PlanStatus.Text = Loc.Get("Plan_Calcul_du_relief_sous_la");
            SetBusyDetail(PlanStatus.Text);
            try { terrain = await (_terrainService ??= new TerrainService(_http)).ProfileAsync(route, 1, cts.Token); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                terrainNote = Loc.Get("Live_Relief_indisponible") + ex.Message;
            }
            cts.Token.ThrowIfCancellationRequested();

            // Obstacles (OpenStreetMap) : service public parfois saturé, un échec n'empêche pas la feuille.
            IReadOnlyList<RouteObstacle> obstacles = [];
            string? obstacleNote = null;
            PlanStatus.Text = Loc.Get("Plan_Recherche_des_obstacles_pres_de");
            SetBusyDetail(PlanStatus.Text);
            try
            {
                _terrainService ??= new TerrainService(_http);
                obstacles = await (_obstacleService ??= new ObstacleService(_http)).FindAsync(route, _terrainService, 2, cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                obstacleNote = Loc.Get("Plan_Obstacles_indisponibles") + ex.Message;
            }
            cts.Token.ThrowIfCancellationRequested();

            _planObstacles = obstacles;
            _planSignature = Signature(route, p.CruiseAltFt);

            var relevant = _airspaces.Where(z => z.Kind is not (AirspaceKind.Sector or AirspaceKind.Other));
            var altitudes = NewAltitudeProfile(route, p.CruiseAltFt);
            var crossings = RouteAnalysis.AirspacesAlong(samples, relevant, altitudes);
            var briefings = PointBriefings.ForRoute(route, p.CruiseAltFt, _airspaces.Where(z => z.Kind != AirspaceKind.Other).ToList(), AerodromeFrequencies);
            var navaids = RouteAnalysis.NavaidsAlong(route, _navaids, 15);

            PlanStatus.Text = Loc.Get("Plan_Recuperation_de_la_meteo");

            SetBusyDetail(PlanStatus.Text);
            var (airports, weatherNote) = await BuildAirportBriefsAsync(route);

            // SIGMET en vigueur sur la route : un échec réseau n'empêche pas la feuille, il y est signalé.
            PlanStatus.Text = Loc.Get("Plan_Recherche_des_SIGMET");
            SetBusyDetail(PlanStatus.Text);
            SigmetReport? sigmets = null;
            string? sigmetNote = null;
            try
            {
                var all = await (_sigmetService ??= new SigmetService(_http)).GetAsync();
                var now = DateTimeOffset.UtcNow;
                sigmets = new SigmetReport(SigmetAnalysis.AlongRoute(route, altitudes, all, now, TimeSpan.FromMinutes(log.TotalMinutes)), SigmetAnalysis.CountActive(all, now), now);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                sigmetNote = Loc.Get("Sigmet_Unavailable") + ex.Message;
            }

            // Graphique de l'onglet « Profil » et image pour la feuille.
            string? message = terrainNote ?? (_airspaces.Count == 0 ? Loc.Get("Plan_Zones_non_chargees_cle_OpenAIP") : null);
            ProfileView.SetData(terrain, p.CruiseAltFt, route, crossings, terrainNote, obstacles, altitudes);
            ProfileStatus.Text = message ?? Loc.F("Plan_passage_s_de_zone_point", crossings.Count, (terrain?.MaxFt ?? 0), obstacles.Count) + CruiseSummary();
            var snapshot = new ProfileChart();
            snapshot.SetData(terrain, p.CruiseAltFt, route, crossings, terrainNote, obstacles, altitudes);
            var image = terrain is null ? null : ProfileChart.Snapshot(snapshot, 700, 270);

            PlanStatus.Text = Loc.Get("Forecast_Chargement");

            SetBusyDetail(PlanStatus.Text);
            await ShowForecastAsync(route, p, cts.Token);
            log = ComputeRouteLog(route, p);   // le vent prévu vient d'arriver, s'il est demandé
            var input = new FlightSheetInput(route, p, log, terrain, terrainNote, crossings, navaids, obstacles, obstacleNote, airports, image, weatherNote, DateTimeOffset.Now, briefings, altitudes, AlternateLines(_mode), sigmets, sigmetNote, _forecastRows.Count > 0 ? _forecastRows : null, DepartureUtc(), _mode == FlightMode.Ifr ? VerticalLines() : null);
            var document = FlightSheetBuilder.Build(input);
            string title = Loc.F("Plan_Feuille_de_route", route[0].Icao ?? route[0].Name, route[^1].Icao ?? route[^1].Name);
            new FlightSheetWindow(document, title) { Owner = this }.Show();
            PlanStatus.Text = Loc.F("Plan_Plan_calcule_NM_L", log.TotalDistanceNm, NavMath.FormatTime(log.TotalMinutes), log.TotalFuelL);
            SetBusyDetail(PlanStatus.Text);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            PlanStatus.Text = Loc.Get("Plan_Calcul_arrete");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            PlanStatus.Text = Loc.Get("Plan_Calcul_interrompu") + ex.Message;
            SetBusyDetail(PlanStatus.Text);
        }
        finally { PlanButton.IsEnabled = true; }
    }

    /// <summary>Fiche et météo décodée de chaque terrain de la route ; la météo manquante n'est pas bloquante.</summary>
    private async Task<(IReadOnlyList<AirportBrief> Briefs, string? Note)> BuildAirportBriefsAsync(IReadOnlyList<Waypoint> route)
    {
        var airports = route.Where(w => w.IsAirport).DistinctBy(w => w.Icao).ToList();
        IReadOnlyList<DecodedReport> metars = [], tafs = [];
        string? note = null;
        if (airports.Count > 0)
        {
            try
            {
                var (metarRaw, tafRaw) = await (_weatherService ??= new WeatherService(_http)).GetAsync(airports.Select(a => a.Icao!));
                metars = WeatherDecoder.DecodeMetars(metarRaw);
                tafs = WeatherDecoder.DecodeTafs(tafRaw);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                note = Loc.Get("Plan_Meteo_indisponible_au_moment_du") + ex.Message;
            }
        }
        var briefs = airports.Select(a => new AirportBrief(a, _aerodromes?.Find(a.Icao!),
            metars.FirstOrDefault(m => m.Station == a.Icao), tafs.FirstOrDefault(t => t.Station == a.Icao))).ToList();
        return (briefs, note);
    }
}
