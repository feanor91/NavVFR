using System.IO;
using System.Collections.Specialized;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Weather;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Notes par point de navigation et informations en direct pendant la création de la route ----------
public partial class MainWindow
{
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private CancellationTokenSource? _liveCts;
    private bool _notesLoading;
    private IReadOnlyList<RouteObstacle> _planObstacles = [];
    private string _planSignature = "";

    private void InitLive() => _liveTimer.Tick += (_, _) => { _liveTimer.Stop(); _ = RunLiveAsync(); };

    private static string Signature(IEnumerable<Waypoint> route, double cruiseFt) =>
        string.Join("|", route.Select(w => $"{w.Lat:0.00000},{w.Lon:0.00000}")) + $"@{cruiseFt:0}";

    /// <summary>Tout changement de la route : mise à jour de la carte, puis du point choisi.</summary>
    private void OnRouteChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshRoute();
        if (FNearRoute.IsChecked == true || e.Action != NotifyCollectionChangedAction.Move) ApplyAirportFilter();
        // Un point qu'on vient d'ajouter devient le point suivi : ses infos s'affichent aussitôt.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex >= 0) RouteGrid.SelectedIndex = e.NewStartingIndex;
    }

    /// <summary>Relance, après une courte pause, les calculs qui demandent le réseau.</summary>
    private void ScheduleLive()
    {
        _liveTimer.Stop();
        _liveTimer.Start();
    }

    private Waypoint? Followed() => RouteGrid.SelectedItem as Waypoint ?? _route.LastOrDefault();

    // ---------- notes ----------

    private void OnRouteSelectionChanged()
    {
        var wp = RouteGrid.SelectedItem as Waypoint;
        _notesLoading = true;
        WpNotesBox.IsEnabled = wp is not null;
        WpNotesBox.Text = wp?.Notes ?? "";
        WpNotesLabel.Text = wp is null ? Loc.Get("Ui_Notes_du_point_selectionne_un") : Loc.F("Live_Notes", wp.Name);
        _notesLoading = false;
        ShowLiveBasics();
        ScheduleLive();
    }

    private void WpNotes_Changed(object sender, TextChangedEventArgs e)
    {
        if (_notesLoading || RouteGrid.SelectedItem is not Waypoint wp) return;
        wp.Notes = string.IsNullOrWhiteSpace(WpNotesBox.Text) ? null : WpNotesBox.Text;
        ScheduleLive(); // l'enregistrement a lieu au prochain passage du minuteur
    }

    /// <summary>Le repère 📝 de la liste ne se met à jour qu'à la fin de la saisie, pour ne pas perturber le champ.</summary>
    private void WpNotes_LostFocus(object sender, RoutedEventArgs e) => RefreshRouteGrids();

    // ---------- infos du point suivi ----------

    /// <summary>Altitude changée à la souris dans le profil : un point précis, ou la croisière de toute la route.</summary>
    private void OnProfileAltitude(int? index, double? ft)
    {
        if (index is int i && i >= 0 && i < _route.Count)
        {
            _route[i].AltitudeFt = ft;
            RefreshRouteGrids();
            }
        else if (ft is double cruise) SetCruiseAlt(cruise);
        ScheduleLive();
    }

    /// <summary>Profil d'altitude avec les pentes de l'appareil choisi (montée et descente), sinon les pentes par défaut.</summary>
    private AltitudeProfile NewAltitudeProfile(IReadOnlyList<Waypoint> route, double cruiseFt) =>
        ActiveAircraft is { } a ? new AltitudeProfile(route, cruiseFt, a.ClimbFtPerNm, a.DescentFtPerNm) : new AltitudeProfile(route, cruiseFt);

    private IEnumerable<Airspace> RelevantZones() => _airspaces.Where(z => z.Kind is not (AirspaceKind.Sector or AirspaceKind.Other));

    /// <summary>Fréquences de la base d'aérodromes pour un terrain de la route.</summary>
    private IReadOnlyList<string> AerodromeFrequencies(Waypoint wp) =>
        wp.IsAirport && _aerodromes?.Find(wp.Icao!) is { } a
            ? [.. a.Frequencies.Select(f => PointBriefings.Format($"{wp.Icao} {f.Service}", f.Mhz))]
            : [];

    /// <summary>Lignes « survol et autorisations » puis « fréquences à contacter » d'un point.</summary>
    private static IEnumerable<string> BriefingLines(PointBriefing briefing)
    {
        var inside = briefing.Zones.Where(z => z.Status == VerticalStatus.Inside).ToList();
        var outside = briefing.Zones.Where(z => z.Status != VerticalStatus.Inside).ToList();
        if (inside.Count == 0 && outside.Count == 0) yield return Loc.Get("Live_Survol_aucune_zone_connue_a");
        foreach (var z in inside.Take(6))
            yield return $"{PointBriefings.Mark(z.Severity)} {z.Zone.Name} ({z.Zone.Lower} → {z.Zone.Upper}) : {z.Verdict}. {z.Action}";
        if (inside.Count > 6) yield return Loc.F("Live_et_autre_s_zone_s", inside.Count - 6);
        foreach (var z in outside.Take(2)) yield return $"ℹ {z.Zone.Name} : {z.Verdict}. {z.Action}";
        if (briefing.Frequencies.Count > 0) yield return Loc.Get("FlightSheetBuilder_Frequences_a_contacter") + string.Join(" · ", briefing.Frequencies);
    }

    /// <summary>Partie immédiate (sans réseau) : tronçon, zones, balises et terrains proches.</summary>
    private List<string> BasicLines(Waypoint wp, FlightParameters p)
    {
        int index = _route.IndexOf(wp);
        var lines = new List<string> { $"{index + 1}. {wp.Name}   ({wp.Lat:0.0000}, {wp.Lon:0.0000})" };
        if (wp.IsAirport && wp.FullName is not null) lines.Add(wp.FullName);

        if (index > 0)
        {
            var leg = NavMath.ComputeLog([_route[index - 1], wp], p).Legs[0];
            lines.Add(Loc.F("Live_Depuis_NM_Rv_cap_kt", _route[index - 1].Name, leg.DistanceNm, leg.TrueCourse, leg.MagneticHeading, leg.GroundSpeedKt, leg.Time, leg.FuelL));
        }

        // Altitude du point, zones qui s'y trouvent à cette altitude, ce que chacune demande et les fréquences à contacter.
        var route = _route.ToList();
        var briefing = PointBriefings.For(route, index, p.CruiseAltFt, _airspaces.Where(z => z.Kind != AirspaceKind.Other).ToList(), AerodromeFrequencies(wp));
        lines.Add(Loc.F("Live_Altitude_a_ce_point_ft", briefing.AltitudeFt, briefing.AltitudeSource));
        lines.AddRange(BriefingLines(briefing));
        if (_airspaces.Count == 0) lines.Add(Loc.Get("Live_Zones_non_chargees_cle_OpenAIP"));

        foreach (var n in WaypointInsight.NearestNavaids(wp.Lat, wp.Lon, _navaids))
            lines.Add($"{n.Navaid.Label} {n.Navaid.Ident} {n.Navaid.FrequencyText} — {WaypointInsight.Relative(n.DistanceNm, n.BearingTrue)}");
        foreach (var a in WaypointInsight.NearestAirports(wp.Lat, wp.Lon, _airports.Where(x => x.Ident != wp.Icao), 3))
            lines.Add(Loc.F("Live_Terrain", a.Airport.Ident, a.Airport.Name, WaypointInsight.Relative(a.DistanceNm, a.BearingTrue)));
        return lines;
    }

    private void ShowLiveBasics()
    {
        if (Followed() is not { } wp) { SetLive(LiveDefaultText); return; }
        SetLive(string.Join("\n", BasicLines(wp, ReadParams())));
    }

    private async Task RunLiveAsync()
    {
        _liveCts?.Cancel();
        var cts = _liveCts = new CancellationTokenSource();
        var ct = cts.Token;
        try { SaveSettings(); } catch (IOException) { /* non bloquant */ }

        var route = _route.ToList();
        var p = ReadParams();
        try
        {
            if (Followed() is { } wp) await ShowPointInfoAsync(wp, p, ct);
            await ShowRouteCheckAsync(route, p, ct);
            await ShowForecastAsync(route, p, ct);
        }
        catch (OperationCanceledException) { /* un nouveau calcul a pris le relais */ }
    }

    /// <summary>Complète les infos du point avec le sol sous lui et, pour un terrain, sa météo.</summary>
    private async Task ShowPointInfoAsync(Waypoint wp, FlightParameters p, CancellationToken ct)
    {
        var lines = BasicLines(wp, p);
        SetLive(string.Join("\n", lines) + Loc.Get("Live_Ground_computing"));
        try
        {
            _terrainService ??= new TerrainService(_http);
            double ground = (await _terrainService.ElevationsFeetAsync([(wp.Lat, wp.Lon)], ct))[0];
            ct.ThrowIfCancellationRequested();
            double here = PointBriefings.AltitudeFor(_route.ToList(), Math.Max(0, _route.IndexOf(wp)), p.CruiseAltFt).Ft;
            lines.Insert(1, Loc.F("Live_Ground_margin", ground, here - ground));

            if (wp.IsAirport)
            {
                _weatherService ??= new WeatherService(_http);
                var (metarRaw, _) = await _weatherService.GetCachedAsync(wp.Icao!, ct);
                ct.ThrowIfCancellationRequested();
                var metar = WeatherDecoder.DecodeMetars(metarRaw).FirstOrDefault(m => m.Station == wp.Icao);
                if (metar is not null)
                {
                    var key = metar.Lines.Where(l => l.StartsWith(Loc.Get("Decoded_Wind"), StringComparison.Ordinal) || l.StartsWith(Loc.Get("Decoded_Visibility"), StringComparison.Ordinal) || l.StartsWith("QNH", StringComparison.Ordinal));
                    lines.Insert(2, Loc.F("Live_Meteo", metar.Category ?? "?", string.Join(" · ", key)));
                }
                else lines.Insert(2, Loc.Get("Live_Meteo_pas_de_METAR_pour"));
            }
            SetLive(string.Join("\n", lines));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            SetLive(string.Join("\n", lines) + Loc.Get("Live_sol_et_meteo_indisponibles") + ex.Message + ")");
        }
    }

    /// <summary>Vérification de toute la route : zones à ton altitude et relief, avec mise à jour du profil.</summary>
    private async Task ShowRouteCheckAsync(List<Waypoint> route, FlightParameters p, CancellationToken ct)
    {
        if (route.Count < 2)
        {
            LiveCheck.Text = Loc.Get("Live_Pose_au_moins_deux_points");
            ProfileView.SetData(null, p.CruiseAltFt, route, []);
            return;
        }

        var altitudes = NewAltitudeProfile(route, p.CruiseAltFt);
        var samples = RouteGeometry.Sample(route, 1);
        var crossings = RouteAnalysis.AirspacesAlong(samples, RelevantZones(), altitudes);
        LiveCheck.Text = string.Join("\n", WaypointInsight.RouteCheck(route, p.CruiseAltFt, crossings, null, zonesLoaded: _airspaces.Count > 0, altitudes: altitudes)) + Loc.Get("Live_Terrain_computing");

        TerrainProfile? terrain = null;
        string? note = null;
        try { terrain = await (_terrainService ??= new TerrainService(_http)).ProfileAsync(route, 1, ct); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            note = Loc.Get("Live_Relief_indisponible") + ex.Message;
        }
        ct.ThrowIfCancellationRequested();

        var lines = WaypointInsight.RouteCheck(route, p.CruiseAltFt, crossings, terrain, zonesLoaded: _airspaces.Count > 0, altitudes: altitudes);
        if (note is not null) lines = [.. lines, note];
        if (ActiveAircraft is { } aircraft && FuelCheck.Message(aircraft, _mode, NavMath.ComputeLog(route, p)) is { } fuel) lines = [.. lines, fuel];
        lines = [.. lines, .. await SigmetCheckLinesAsync(route, altitudes, p, ct)];
        LiveCheck.Text = string.Join("\n", lines);

        // Les obstacles du dernier calcul de plan ne valent que pour la même route à la même altitude.
        var obstacles = _planSignature == Signature(route, p.CruiseAltFt) ? _planObstacles : [];
        ProfileView.SetData(terrain, p.CruiseAltFt, route, crossings, note, obstacles, altitudes);
        ProfileStatus.Text = terrain is null ? note ?? "" : Loc.F("Live_Profil_en_direct_point_le", terrain.MaxFt, crossings.Count) + CruiseSummary();
    }

    // ---------- note au survol d'un point de la route ----------

    private bool TryShowWaypointNote()
    {
        Waypoint? best = null;
        double bestDistance = AirportHoverPx;
        foreach (var w in _route.Where(w => w.HasNotes))
        {
            double d = Distance(_mouse, ToScreen(w.Lon, w.Lat));
            if (d <= bestDistance) { bestDistance = d; best = w; }
        }
        if (best is null) return false;

        _airportTip = null;
        TipText.Inlines.Clear();
        TipText.Inlines.Add(new System.Windows.Documents.Run($"{_route.IndexOf(best) + 1}. {best.Name}") { FontWeight = FontWeights.Bold });
        TipText.Inlines.Add(new System.Windows.Documents.LineBreak());
        TipText.Inlines.Add(new System.Windows.Documents.Run(best.Notes!.Trim()));
        PlaceTip(ToScreen(best.Lon, best.Lat));
        return true;
    }
}
