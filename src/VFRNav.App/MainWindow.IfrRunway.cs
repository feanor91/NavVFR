using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VFRNav.Core.Ifr;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Piste conseillée d'après le vent : la piste décide des SID et des STAR utilisables ----------
public partial class MainWindow
{
    /// <summary>Vent retenu pour un terrain, d'où il vient, et les pistes classées. Null quand le terrain n'a aucune piste dans ses procédures.</summary>
    private sealed record RunwayAdviceState(WindInfo Wind, string Source, bool VariationKnown, IReadOnlyList<RunwayChoice> Ranking);

    private readonly DispatcherTimer _adviceTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _adviceHooked;
    private CancellationTokenSource? _adviceCts;
    private RunwayAdviceState? _depAdvice, _arrAdvice;

    /// <summary>Vrai tant que le programme (et non l'utilisateur) change une sélection, pour ne pas la prendre pour un choix.</summary>
    private bool _programmatic;

    /// <summary>La piste a été choisie à la main : le conseil du vent ne la change plus (jusqu'à ce que le terrain change).</summary>
    private bool _depRunwayPinned, _arrRunwayPinned;

    private void ScheduleRunwayAdvice()
    {
        if (!_adviceHooked)
        {
            _adviceTimer.Tick += async (_, _) => { _adviceTimer.Stop(); await RefreshRunwayAdviceAsync(); };
            _adviceHooked = true;
        }
        _adviceTimer.Stop();
        _adviceTimer.Start();
    }

    /// <summary>Un terrain change : sa piste redevient libre et le conseil est refait.</summary>
    private void IfrAirport_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (ReferenceEquals(sender, IfrDepBox)) { _depRunwayPinned = false; _depAdvice = null; }
        else if (ReferenceEquals(sender, IfrArrBox)) { _arrRunwayPinned = false; _arrAdvice = null; }
        RefreshIfrChoices();
    }

    /// <summary>La piste choisie change : la liste des SID (ou des STAR) ne garde que celles qui la desservent.</summary>
    private void IfrRunway_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _programmatic) return;
        bool departure = ReferenceEquals(sender, IfrDepRunwayBox);
        if (departure) _depRunwayPinned = true; else _arrRunwayPinned = true;
        FillProcedures(departure ? IfrSidBox : IfrStarBox, departure ? IfrDepRunwayBox : IfrArrRunwayBox,
            departure ? IfrDepBox.Text : IfrArrBox.Text, departure ? ProcedureKind.Sid : ProcedureKind.Star, keepRunways: true);
        RefreshProcedureMap();
    }

    // ---------- conseil ----------

    private async Task RefreshRunwayAdviceAsync()
    {
        _adviceCts?.Cancel();
        var cts = _adviceCts = new CancellationTokenSource();
        var ct = cts.Token;
        string dep = IfrDepBox.Text.Trim().ToUpperInvariant(), arr = IfrArrBox.Text.Trim().ToUpperInvariant();
        try
        {
            var depAdvice = await AdviceForAsync(dep, ProcedureKind.Sid, ct);
            var arrAdvice = await AdviceForAsync(arr, ProcedureKind.Star, ct);
            ct.ThrowIfCancellationRequested();
            // Les terrains ont pu changer pendant la lecture du METAR : on ne garde que ce qui correspond encore.
            if (dep != IfrDepBox.Text.Trim().ToUpperInvariant() || arr != IfrArrBox.Text.Trim().ToUpperInvariant()) return;
            _depAdvice = depAdvice;
            _arrAdvice = arrAdvice;
            ApplyAdvice();
            IfrAirportInfo.Text = BuildAirportInfo();
        }
        catch (OperationCanceledException) { /* un nouveau conseil a pris le relais */ }
    }

    private async Task<RunwayAdviceState?> AdviceForAsync(string icao, ProcedureKind kind, CancellationToken ct)
    {
        if (icao.Length != 4) return null;
        var runways = RunwayAdvice.RunwaysOf(_ifrData.ProceduresFor(icao, kind));
        if (runways.Count == 0) return null;

        WindInfo? wind = null;
        string source = "";
        try
        {
            _weatherService ??= new WeatherService(_http);
            var (metar, _) = await _weatherService.GetCachedAsync(icao, ct);
            wind = RunwayAdvice.ParseMetar(metar, icao);
            if (wind is not null) source = "METAR";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested(); // une annulation voulue remonte ; un METAR injoignable laisse le vent du formulaire
        }

        if (wind is null)
        {
            // Pas de METAR (hors ligne, terrain sans station) : le vent supposé du formulaire sert de repli.
            wind = new WindInfo((int)Math.Round(Parse(WdirBox, 0)), Parse(WspdBox, 0));
            source = Loc.Get("IfrRunway_vent_suppose_du_formulaire");
        }

        bool known = _aerodromes?.Find(icao)?.MagneticVariation is not null;
        double variation = _aerodromes?.Find(icao)?.MagneticVariation ?? 0;
        return new RunwayAdviceState(wind, source, known, RunwayAdvice.Rank(runways, wind, variation));
    }

    /// <summary>Choisit dans la liste la piste conseillée, sauf si l'utilisateur en a choisi une.</summary>
    private void ApplyAdvice()
    {
        Apply(IfrDepRunwayBox, _depAdvice, _depRunwayPinned, IfrSidBox, IfrDepBox.Text, ProcedureKind.Sid);
        Apply(IfrArrRunwayBox, _arrAdvice, _arrRunwayPinned, IfrStarBox, IfrArrBox.Text, ProcedureKind.Star);

        void Apply(ComboBox runways, RunwayAdviceState? advice, bool pinned, ComboBox procedures, string icao, ProcedureKind kind)
        {
            if (pinned || advice is null || advice.Ranking.Count == 0) return;
            string best = advice.Ranking[0].Runway;
            if (runways.ItemsSource is not IEnumerable<string> items || !items.Contains(best)) return;
            _programmatic = true;
            try { runways.SelectedItem = best; }
            finally { _programmatic = false; }
            FillProcedures(procedures, runways, icao, kind, keepRunways: true);
        }
    }

    private static string AdviceText(RunwayAdviceState? a)
    {
        if (a is null) return "";
        var w = a.Wind;
        string wind = w.DirectionTrueDeg is { } d ? $"{d:000}° {w.SpeedKt:0} kt" : $"variable {w.SpeedKt:0} kt";
        if (w.GustKt is { } g) wind += Loc.F("Runway_Gust", g);
        if (a.Ranking.Count == 0) return Loc.F("IfrRunway_Vent_pas_de_piste_conseillee", wind, a.Source);
        var best = a.Ranking[0];
        string note = a.VariationKnown ? "" : Loc.Get("IfrRunway_Variation_inconnue_vent_pris_en");
        return Loc.F("IfrRunway_Vent_piste_conseillee", wind, a.Source, best.Runway, best.Describe(), note);
    }
}
