using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VFRNav.Core.Localization;
using VFRNav.Core.Models;

namespace VFRNav.App;

/// <summary>Texte d'un terrain dans une liste de résultats : « LFPO/ORY · Paris-Orly, Paris (FR) », avec son code MSFS s'il diffère.</summary>
internal static class AirportText
{
    public static string Line(Airport a) =>
        $"{a.Ident}{(a.Iata is null ? "" : "/" + a.Iata)}{(a.SimIdent is null ? "" : " [" + Loc.F("AirportSearch_Code_MSFS", a.SimIdent) + "]")} · {a.Name}{(a.City is null ? "" : ", " + a.City)} ({a.Country})";

    /// <summary>Code à utiliser pour ce terrain dans l'IFR : celui de MSFS quand il diffère, sinon le code habituel.</summary>
    public static string IfrCode(Airport a) => a.SimIdent ?? a.Ident;
}

/// <summary>
/// Recherche d'un terrain par code OACI ou IATA, nom ou ville : saisie, liste de résultats et bouton d'ajout à la route.
/// Présente dans les panneaux VFR et IFR ; la recherche elle-même est fournie par <see cref="Finder"/>.
/// </summary>
public sealed class AirportSearchPanel : StackPanel
{
    private readonly TextBox _box = new();
    private readonly ListBox _list = new() { MaxHeight = 170, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _add = new() { Content = Loc.Get("Ui_Ajouter_a_la_route"), Margin = new Thickness(0, 4, 0, 0), IsEnabled = false };
    private IReadOnlyList<Airport> _results = [];

    /// <summary>Terrains correspondant à une saisie (fournie par l'application).</summary>
    public Func<string, IReadOnlyList<Airport>> Finder { get; set; } = _ => [];

    /// <summary>Un résultat est choisi dans la liste (null : plus de choix).</summary>
    public event Action<Airport?>? AirportSelected;

    /// <summary>Le bouton « Ajouter à la route » est cliqué pour un résultat.</summary>
    public event Action<Airport>? AddRequested;

    public AirportSearchPanel()
    {
        Children.Add(new TextBlock
        {
            Text = Loc.Get("Ui_RECHERCHE_DE_TERRAIN"), Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xB4, 0xE8)),
            FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4),
        });
        Children.Add(new TextBlock
        {
            Text = Loc.Get("Ui_Code_OACI_IATA_nom_ou"), Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xC4, 0xEE)),
            FontSize = 11, Margin = new Thickness(0, 6, 0, 2),
        });
        AutomationProperties.SetAutomationId(_box, "AirportSearchBox");
        AutomationProperties.SetAutomationId(_list, "AirportSearchList");
        Children.Add(_box);
        Children.Add(_list);
        Children.Add(_add);

        _box.TextChanged += (_, _) => Refresh();
        _list.SelectionChanged += (_, _) =>
        {
            var selected = Selected;
            _add.IsEnabled = selected is not null;
            AirportSelected?.Invoke(selected);
        };
        _add.Click += (_, _) => { if (Selected is { } airport) AddRequested?.Invoke(airport); };
    }

    private Airport? Selected => _list.SelectedIndex is >= 0 and var i && i < _results.Count ? _results[i] : null;

    /// <summary>Refait la recherche (par exemple quand la liste des terrains vient de changer).</summary>
    public void Refresh()
    {
        _results = Finder(_box.Text);
        _list.ItemsSource = _results.Select(AirportText.Line).ToList();
        _add.IsEnabled = false;
    }
}

/// <summary>
/// Complétion d'une zone de saisie de terrain : en tapant un code, une ville ou un nom, une liste propose les terrains ; on choisit à la souris
/// ou avec les flèches et Entrée, et la zone reçoit le code du terrain.
/// </summary>
public sealed class AirportAutocomplete
{
    private readonly TextBox _box;
    private readonly Func<string, IReadOnlyList<Airport>> _finder;
    private readonly Func<Airport, string> _codeOf;
    private readonly Popup _popup;
    private readonly ListBox _list = new() { MaxHeight = 240, MinWidth = 300, BorderThickness = new Thickness(0) };
    private IReadOnlyList<Airport> _results = [];
    private bool _suppress;

    private AirportAutocomplete(TextBox box, Func<string, IReadOnlyList<Airport>> finder, Func<Airport, string> codeOf)
    {
        _box = box;
        _finder = finder;
        _codeOf = codeOf;
        _popup = new Popup
        {
            PlacementTarget = box, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border
            {
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x4A, 0x8A)), BorderThickness = new Thickness(1), Child = _list,
            },
        };
        box.TextChanged += (_, _) => Update();
        box.PreviewKeyDown += OnKeyDown;
        box.LostKeyboardFocus += (_, _) => { if (!_list.IsKeyboardFocusWithin && !_list.IsMouseOver) _popup.IsOpen = false; };
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemUnder(e) is { } index) Accept(index);
        };
    }

    public static AirportAutocomplete Attach(TextBox box, Func<string, IReadOnlyList<Airport>> finder, Func<Airport, string> codeOf) => new(box, finder, codeOf);

    private int? ItemUnder(MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element is not null and not ListBoxItem) element = VisualTreeHelper.GetParent(element);
        return element is ListBoxItem item && _list.ItemContainerGenerator.IndexFromContainer(item) is >= 0 and var i ? i : null;
    }

    private void Update()
    {
        if (_suppress || !_box.IsKeyboardFocusWithin) return;
        _results = _finder(_box.Text);
        _list.ItemsSource = _results.Select(AirportText.Line).ToList();
        if (_results.Count == 0) { _popup.IsOpen = false; return; }
        _list.SelectedIndex = -1;
        _popup.IsOpen = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _results.Count - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab when _list.SelectedIndex >= 0:
                Accept(_list.SelectedIndex);
                e.Handled = e.Key == Key.Enter;
                break;
            case Key.Escape:
                _popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void Accept(int index)
    {
        if (index < 0 || index >= _results.Count) return;
        _suppress = true;
        try { _box.Text = _codeOf(_results[index]); _box.CaretIndex = _box.Text.Length; }
        finally { _suppress = false; }
        _popup.IsOpen = false;
    }
}
