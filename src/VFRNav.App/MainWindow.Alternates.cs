using System.Globalization;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.App;

// ---------- Aéroports de dégagement (détournement), en VFR comme en IFR ----------
public partial class MainWindow
{
    private void Altn_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded || _loadingParams) return;
        RefreshAlternates();
        ScheduleLive();
    }

    /// <summary>Paramètres de vol avec la vitesse et la consommation de l'appareil du mode donné (le vent et la déclinaison sont communs).</summary>
    private FlightParameters ReadParamsFor(FlightMode mode) =>
        AircraftFor(mode) is { } a ? ReadParams() with { TasKt = a.CruiseKt, BurnLph = a.BurnLph } : ReadParams();

    private System.Windows.Controls.TextBox AltnBox(FlightMode mode) => mode == FlightMode.Ifr ? IfrAltnBox : VfrAltnBox;

    /// <summary>Lignes de chaque terrain de dégagement du mode donné, vues depuis le dernier point de la route.</summary>
    private IReadOnlyList<string> AlternateLines(FlightMode mode)
    {
        var codes = Alternates.Parse(AltnBox(mode).Text);
        if (codes.Count == 0 || _route.Count == 0) return [];
        var info = Alternates.For(_route[^1], codes, _airports, ReadParamsFor(mode));
        var lines = info.Select(i => i.Line).ToList();

        // Carburant : route puis le dégagement le plus gourmand, comparé à la capacité de l'appareil du mode.
        if (_route.Count >= 2 && info.Any(i => i.Known) && AircraftFor(mode) is { } a)
        {
            double total = NavMath.ComputeLog(_route, ReadParamsFor(mode)).TotalFuelL + info.Where(i => i.Known).Max(i => i.FuelL);
            lines.Add(Loc.F("Alternates_Carburant_route_degagement_le_plus", total.ToString("0.#", VFRNav.Core.Localization.Loc.FormatCulture))
                + (total > a.FuelCapacityL ? Loc.F("Alternates_plus_que_les_L_de", a.FuelCapacityL, a.Name) : ""));
        }
        return lines;
    }

    private void RefreshAlternates()
    {
        if (!IsLoaded) return;
        VfrAltnInfo.Text = string.Join("\n", AlternateLines(FlightMode.Vfr));
        IfrAltnInfo.Text = string.Join("\n", AlternateLines(FlightMode.Ifr));
    }
}
