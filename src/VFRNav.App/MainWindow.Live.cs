using System.IO;
using System.Collections.Specialized;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Weather;

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
        RefreshFeedRoute();
        RefreshRoute();
        // Un point qu'on vient d'ajouter devient le point suivi : ses infos s'affichent aussitôt.
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex >= 0) RouteGrid.SelectedIndex = e.NewStartingIndex;
    }

    /// <summary>Relance, après une courte pause, les calculs qui demandent le réseau.</summary>
    private void ScheduleLive()
    {
        _liveTimer.Stop();
        _liveTimer.Start();
    }

    // ---------- partage avec StormATC ----------

    /// <summary>Copie la route pour le serveur local, qui la lit hors du thread de l'interface.</summary>
    private void RefreshFeedRoute() =>
        _feedRoute = [.. LiveFeed.State(_route, null, null, 0, DateTimeOffset.UtcNow).Route];

    private void StartFeed()
    {
        RefreshFeedRoute();
        _feed = new LiveFeedServer(
            () =>
            {
                var a = _feedAircraft;
                string key = _settings.BaseMap, carto = _settings.CartoKey;
                return new LiveFeedState(LiveFeed.ProtocolVersion, _feedRoute, a?.Aircraft, _settings.CruiseAltFt,
                    a is null ? -1 : (long)Math.Max(0, (DateTimeOffset.UtcNow - a.Value.At).TotalMilliseconds),
                    new FeedBaseMap(BaseMapSources.Effective(key, carto), BaseMapSources.LayerCount(key, carto), BaseMapSources.MaxZoom(key, carto)));
            },
            (minLat, minLon, maxLat, maxLon) =>
                LiveFeed.Map(_shownAirspaces, _airspacesLoadedForFeed, _airports, minLat, minLon, maxLat, maxLon),
            FetchTileAsync);
        _feed.Start();
    }

    private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _tileCache = [];

    /// <summary>Tuile du fond de carte courant, téléchargée une fois puis gardée (le fond est celui de la carte de NavVFR).</summary>
    private async Task<(byte[] Bytes, string ContentType)?> FetchTileAsync(int layer, int z, int x, int y, CancellationToken ct)
    {
        string key = _settings.BaseMap, carto = _settings.CartoKey;
        string? url = BaseMapSources.TileUrl(key, carto, layer, z, x, y);
        if (url is null) return null;
        string cacheKey = $"{BaseMapSources.Effective(key, carto)}/{layer}/{z}/{x}/{y}";
        lock (_tileCache) if (_tileCache.TryGetValue(cacheKey, out var hit)) return hit;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("VFRNav (flight simulation)");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var tile = (await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType ?? "image/png");
            lock (_tileCache)
            {
                if (_tileCache.Count > 1500) _tileCache.Clear();
                _tileCache[cacheKey] = tile;
            }
            return tile;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return null; }
    }

    private Waypoint? Followed() => RouteGrid.SelectedItem as Waypoint ?? _route.LastOrDefault();

    // ---------- notes ----------

    private void OnRouteSelectionChanged()
    {
        var wp = RouteGrid.SelectedItem as Waypoint;
        _notesLoading = true;
        WpNotesBox.IsEnabled = wp is not null;
        WpNotesBox.Text = wp?.Notes ?? "";
        WpNotesLabel.Text = wp is null ? "Notes du point (sélectionne un point de la route)" : $"Notes — {wp.Name}";
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
    private void WpNotes_LostFocus(object sender, RoutedEventArgs e) => RouteGrid.Items.Refresh();

    // ---------- infos du point suivi ----------

    private IEnumerable<Airspace> RelevantZones() => _airspaces.Where(z => z.Kind is not (AirspaceKind.Sector or AirspaceKind.Other));

    /// <summary>Fréquences publiées par StormATC pour un terrain de la route.</summary>
    private IReadOnlyList<string> AerodromeFrequencies(Waypoint wp) =>
        wp.IsAirport && _stormAtc?.Find(wp.Icao!) is { } a
            ? [.. a.Frequencies.Select(f => PointBriefings.Format($"{wp.Icao} {f.Service}", f.Mhz))]
            : [];

    /// <summary>Lignes « survol et autorisations » puis « fréquences à contacter » d'un point.</summary>
    private static IEnumerable<string> BriefingLines(PointBriefing briefing)
    {
        var inside = briefing.Zones.Where(z => z.Status == VerticalStatus.Inside).ToList();
        var outside = briefing.Zones.Where(z => z.Status != VerticalStatus.Inside).ToList();
        if (inside.Count == 0 && outside.Count == 0) yield return "Survol : aucune zone connue à cette altitude";
        foreach (var z in inside.Take(6))
            yield return $"{PointBriefings.Mark(z.Severity)} {z.Zone.Name} ({z.Zone.Lower} → {z.Zone.Upper}) : {z.Verdict}. {z.Action}";
        if (inside.Count > 6) yield return $"… et {inside.Count - 6} autre(s) zone(s)";
        foreach (var z in outside.Take(2)) yield return $"ℹ {z.Zone.Name} : {z.Verdict}. {z.Action}";
        if (briefing.Frequencies.Count > 0) yield return "Fréquences à contacter : " + string.Join(" · ", briefing.Frequencies);
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
            lines.Add($"Depuis {_route[index - 1].Name} : {leg.DistanceNm:0.0} NM · Rv {leg.TrueCourse:000}° · cap {leg.MagneticHeading:000}° · {leg.GroundSpeedKt:0} kt · {leg.Time} · {leg.FuelL:0.0} L");
        }

        // Altitude du point, zones qui s'y trouvent à cette altitude, ce que chacune demande et les fréquences à contacter.
        var route = _route.ToList();
        var briefing = PointBriefings.For(route, index, p.CruiseAltFt, _airspaces.Where(z => z.Kind != AirspaceKind.Other).ToList(), AerodromeFrequencies(wp));
        lines.Add($"Altitude à ce point : {briefing.AltitudeFt:N0} ft ({briefing.AltitudeSource})");
        lines.AddRange(BriefingLines(briefing));
        if (_airspaces.Count == 0) lines.Add("Zones non chargées (clé OpenAIP) : espaces aériens non vérifiés");

        foreach (var n in WaypointInsight.NearestNavaids(wp.Lat, wp.Lon, _navaids))
            lines.Add($"{n.Navaid.Label} {n.Navaid.Ident} {n.Navaid.FrequencyText} — {WaypointInsight.Relative(n.DistanceNm, n.BearingTrue)}");
        foreach (var a in WaypointInsight.NearestAirports(wp.Lat, wp.Lon, _airports.Where(x => x.Ident != wp.Icao), 3))
            lines.Add($"Terrain {a.Airport.Ident} {a.Airport.Name} — {WaypointInsight.Relative(a.DistanceNm, a.BearingTrue)}");
        return lines;
    }

    private void ShowLiveBasics()
    {
        if (Followed() is not { } wp) { LiveInfo.Text = "Pose un point sur la carte : ses informations s'affichent ici."; return; }
        LiveInfo.Text = string.Join("\n", BasicLines(wp, ReadParams()));
    }

    private async Task RunLiveAsync()
    {
        _liveCts?.Cancel();
        var cts = _liveCts = new CancellationTokenSource();
        var ct = cts.Token;
        try { SaveSettings(); } catch (IOException) { /* non bloquant */ }

        var route = _route.ToList();
        var p = ReadParams();
        RefreshFeedRoute();
        try
        {
            if (Followed() is { } wp) await ShowPointInfoAsync(wp, p, ct);
            await ShowRouteCheckAsync(route, p, ct);
        }
        catch (OperationCanceledException) { /* un nouveau calcul a pris le relais */ }
    }

    /// <summary>Complète les infos du point avec le sol sous lui et, pour un terrain, sa météo.</summary>
    private async Task ShowPointInfoAsync(Waypoint wp, FlightParameters p, CancellationToken ct)
    {
        var lines = BasicLines(wp, p);
        LiveInfo.Text = string.Join("\n", lines) + "\nSol : calcul…";
        try
        {
            _terrainService ??= new TerrainService(_http);
            double ground = (await _terrainService.ElevationsFeetAsync([(wp.Lat, wp.Lon)], ct))[0];
            ct.ThrowIfCancellationRequested();
            double here = PointBriefings.AltitudeFor(_route.ToList(), Math.Max(0, _route.IndexOf(wp)), p.CruiseAltFt).Ft;
            lines.Insert(1, $"Sol : {ground:N0} ft — marge sous ton altitude ici : {here - ground:N0} ft");

            if (wp.IsAirport)
            {
                _weatherService ??= new WeatherService(_http);
                var (metarRaw, _) = await _weatherService.GetCachedAsync(wp.Icao!, ct);
                ct.ThrowIfCancellationRequested();
                var metar = WeatherDecoder.DecodeMetars(metarRaw).FirstOrDefault(m => m.Station == wp.Icao);
                if (metar is not null)
                {
                    var key = metar.Lines.Where(l => l.StartsWith("Vent", StringComparison.Ordinal) || l.StartsWith("Visibilité", StringComparison.Ordinal) || l.StartsWith("QNH", StringComparison.Ordinal));
                    lines.Insert(2, $"Météo : {metar.Category ?? "?"} — {string.Join(" · ", key)}");
                }
                else lines.Insert(2, "Météo : pas de METAR pour ce terrain");
            }
            LiveInfo.Text = string.Join("\n", lines);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            LiveInfo.Text = string.Join("\n", lines) + "\n(sol et météo indisponibles : " + ex.Message + ")";
        }
    }

    /// <summary>Vérification de toute la route : zones à ton altitude et relief, avec mise à jour du profil.</summary>
    private async Task ShowRouteCheckAsync(List<Waypoint> route, FlightParameters p, CancellationToken ct)
    {
        if (route.Count < 2)
        {
            LiveCheck.Text = "Pose au moins deux points pour vérifier la route.";
            ProfileView.SetData(null, p.CruiseAltFt, route, []);
            return;
        }

        var altitudes = new AltitudeProfile(route, p.CruiseAltFt);
        var samples = RouteGeometry.Sample(route, 1);
        var crossings = RouteAnalysis.AirspacesAlong(samples, RelevantZones(), altitudes);
        LiveCheck.Text = string.Join("\n", WaypointInsight.RouteCheck(route, p.CruiseAltFt, crossings, null, zonesLoaded: _airspaces.Count > 0, altitudes: altitudes)) + "\nRelief : calcul…";

        TerrainProfile? terrain = null;
        string? note = null;
        try { terrain = await (_terrainService ??= new TerrainService(_http)).ProfileAsync(route, 1, ct); }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            note = "Relief indisponible : " + ex.Message;
        }
        ct.ThrowIfCancellationRequested();

        var lines = WaypointInsight.RouteCheck(route, p.CruiseAltFt, crossings, terrain, zonesLoaded: _airspaces.Count > 0, altitudes: altitudes);
        if (note is not null) lines = [.. lines, note];
        LiveCheck.Text = string.Join("\n", lines);

        // Les obstacles du dernier calcul de plan ne valent que pour la même route à la même altitude.
        var obstacles = _planSignature == Signature(route, p.CruiseAltFt) ? _planObstacles : [];
        ProfileView.SetData(terrain, p.CruiseAltFt, route, crossings, note, obstacles, altitudes);
        ProfileStatus.Text = terrain is null ? note ?? "" : $"Profil en direct · point le plus haut {terrain.MaxFt:N0} ft · {crossings.Count} passage(s) de zone";
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
