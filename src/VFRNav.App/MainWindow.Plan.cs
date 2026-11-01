using System.Net.Http;
using System.Windows;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.StormAtc;
using VFRNav.Core.Weather;

namespace VFRNav.App;

// ---------- Calcul du plan de vol : relief, zones, balises, météo, puis feuille de route ----------
public partial class MainWindow
{
    private TerrainService? _terrainService;
    private ObstacleService? _obstacleService;

    private async void CalculatePlan_Click(object sender, RoutedEventArgs e)
    {
        if (_route.Count < 2) { PlanStatus.Text = "Il faut au moins deux points pour calculer le plan de vol."; return; }

        PlanButton.IsEnabled = false;
        PlanStatus.Text = "Calcul du plan de vol...";
        try
        {
            var route = _route.ToList();
            var p = ReadParams();
            var log = NavMath.ComputeLog(route, p);
            var samples = RouteGeometry.Sample(route, 1);

            // Relief (service en ligne) : un échec n'empêche pas la feuille, il y est signalé.
            TerrainProfile? terrain = null;
            string? terrainNote = null;
            PlanStatus.Text = "Calcul du relief sous la route...";
            try { terrain = await (_terrainService ??= new TerrainService(_http)).ProfileAsync(route); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                terrainNote = "Relief indisponible : " + ex.Message;
            }

            // Obstacles (OpenStreetMap) : service public parfois saturé, un échec n'empêche pas la feuille.
            IReadOnlyList<RouteObstacle> obstacles = [];
            string? obstacleNote = null;
            PlanStatus.Text = "Recherche des obstacles près de la route...";
            try
            {
                _terrainService ??= new TerrainService(_http);
                obstacles = await (_obstacleService ??= new ObstacleService(_http)).FindAsync(route, _terrainService, 2);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                obstacleNote = "Obstacles indisponibles : " + ex.Message;
            }

            var relevant = _airspaces.Where(z => z.Kind is not (AirspaceKind.Sector or AirspaceKind.Other));
            var crossings = RouteAnalysis.AirspacesAlong(samples, relevant, p.CruiseAltFt);
            var navaids = RouteAnalysis.NavaidsAlong(route, _navaids, 15);

            PlanStatus.Text = "Récupération de la météo...";
            var (airports, weatherNote) = await BuildAirportBriefsAsync(route);

            // Graphique de l'onglet « Profil » et image pour la feuille.
            string? message = terrainNote ?? (_airspaces.Count == 0 ? "Zones non chargées (clé OpenAIP) : seul le relief est tracé." : null);
            ProfileView.SetData(terrain, p.CruiseAltFt, route, crossings, terrainNote, obstacles);
            ProfileStatus.Text = message ?? $"{crossings.Count} passage(s) de zone · point le plus haut {(terrain?.MaxFt ?? 0):N0} ft · {obstacles.Count} obstacle(s) répertorié(s)";
            var snapshot = new ProfileChart();
            snapshot.SetData(terrain, p.CruiseAltFt, route, crossings, terrainNote, obstacles);
            var image = terrain is null ? null : ProfileChart.Snapshot(snapshot, 700, 270);

            var input = new FlightSheetInput(route, p, log, terrain, terrainNote, crossings, navaids, obstacles, obstacleNote, airports, image, weatherNote, DateTimeOffset.Now);
            var document = FlightSheetBuilder.Build(input);
            string title = $"Feuille de route {route[0].Icao ?? route[0].Name} - {route[^1].Icao ?? route[^1].Name}";
            new FlightSheetWindow(document, title) { Owner = this }.Show();
            PlanStatus.Text = $"Plan calculé : {log.TotalDistanceNm:0.#} NM, {NavMath.FormatTime(log.TotalMinutes)}, {log.TotalFuelL:0.#} L.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            PlanStatus.Text = "Calcul interrompu : " + ex.Message;
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
                note = "Météo indisponible au moment du calcul : " + ex.Message;
            }
        }
        var briefs = airports.Select(a => new AirportBrief(a, _stormAtc?.Find(a.Icao!),
            metars.FirstOrDefault(m => m.Station == a.Icao), tafs.FirstOrDefault(t => t.Station == a.Icao))).ToList();
        return (briefs, note);
    }
}
