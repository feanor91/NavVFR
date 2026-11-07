using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Localization;

namespace VFRNav.App;

/// <summary>
/// Gestion des appareils : liste du type de navigation concerné à gauche, fiche à droite
/// (créer, dupliquer, enregistrer, supprimer, remettre les appareils par défaut).
/// Chaque enregistrement est écrit tout de suite dans le fichier des appareils.
/// </summary>
public sealed class AircraftWindow : Window
{
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(95, 105, 120));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(180, 30, 30));

    private readonly AircraftCatalog _catalog;
    private readonly FlightMode _mode;

    private readonly ListBox _list = new() { DisplayMemberPath = "Name", MinWidth = 210 };
    private readonly CheckBox _showAll = new() { Content = Loc.Get("AircraftWindow_Voir_aussi_l_autre_type"), Margin = new Thickness(0, 6, 0, 0) };

    private readonly TextBox _name = new();
    private readonly ComboBox _category = new() { ItemsSource = new[] { Loc.Get("AircraftWindow_Piston"), Loc.Get("AircraftWindow_Turbopropulseur"), "Jet" } };
    private readonly CheckBox _vfr = new() { Content = "VFR" };
    private readonly CheckBox _ifr = new() { Content = "IFR" };
    private readonly TextBox _cruise = new(), _max = new(), _burn = new(), _fuel = new(), _ceiling = new(), _cruiseAlt = new(), _climb = new(), _descent = new(), _reserve = new(), _v1 = new(), _vr = new(), _v2 = new();
    private readonly TextBox _notes = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _derived = new() { Foreground = Grey, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { Foreground = Red, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _save = new() { Content = Loc.Get("AircraftWindow_Enregistrer"), IsDefault = true };
    private readonly Button _delete = new() { Content = Loc.Get("Ui_Supprimer") };
    private readonly Button _duplicate = new() { Content = Loc.Get("AircraftWindow_Dupliquer") };

    private Aircraft? _editing;
    private bool _isNew;
    private bool _loading;

    /// <summary>Vrai si au moins une fiche a été créée, modifiée ou supprimée.</summary>
    public bool Changed { get; private set; }

    public AircraftWindow(AircraftCatalog catalog, FlightMode mode, Guid? select)
    {
        _catalog = catalog;
        _mode = mode;
        Title = mode == FlightMode.Vfr ? Loc.Get("AircraftWindow_Appareils_VFR") : Loc.Get("AircraftWindow_Appareils_IFR");
        Width = 760;
        Height = 640;
        MinHeight = 560;
        Background = Brushes.White;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/VFRNav.ico"));

        var root = new Grid { Margin = new Thickness(12) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // --- liste à gauche
        var left = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        var newButton = new Button { Content = Loc.Get("AircraftWindow_Nouvel_appareil"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 6) };
        newButton.Click += (_, _) => StartNew();
        DockPanel.SetDock(newButton, Dock.Top);
        left.Children.Add(newButton);
        var restore = new Button { Content = Loc.Get("AircraftWindow_Remettre_les_appareils_par_defaut"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 6, 0, 0) };
        restore.Click += Restore_Click;
        DockPanel.SetDock(restore, Dock.Bottom);
        left.Children.Add(restore);
        DockPanel.SetDock(_showAll, Dock.Bottom);
        left.Children.Add(_showAll);
        left.Children.Add(_list);
        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        // --- fiche à droite
        var form = new StackPanel();
        form.Children.Add(Field("Nom", _name));
        form.Children.Add(Field(Loc.Get("AircraftWindow_Categorie"), _category));

        var caps = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        caps.Children.Add(new TextBlock { Text = Loc.Get("AircraftWindow_Capacites"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        _vfr.Margin = new Thickness(0, 0, 14, 0);
        caps.Children.Add(_vfr);
        caps.Children.Add(_ifr);
        form.Children.Add(caps);
        form.Children.Add(new TextBlock { Text = Loc.Get("AircraftWindow_Un_appareil_VFR_et_IFR"), Foreground = Grey, FontSize = 11 });

        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 4, 0, 0) };
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Vitesse_de_croisiere_kt"), _cruise, Loc.Get("AircraftWindow_Vitesse_propre_en_croisiere_utilisee")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Vitesse_maximale_kt"), _max, Loc.Get("AircraftWindow_Vne_ou_vitesse_maximale_en")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Consommation_L_h"), _burn, Loc.Get("AircraftWindow_En_croisiere")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Carburant_utilisable_L"), _fuel, Loc.Get("AircraftWindow_Sert_a_calculer_l_autonomie")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Croisiere_par_defaut_ft"), _cruiseAlt, Loc.Get("AircraftWindow_35000_pour_FL350")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Plafond_ft_facultatif"), _ceiling));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Reserve_min_facultatif"), _reserve, Loc.Get("AircraftWindow_Carburant_garde_en_reserve_en")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Montee_ft_min_facultatif"), _climb, Loc.Get("AircraftWindow_Sert_au_profil_les_points")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_V1_kt_facultatif"), _v1, Loc.Get("AircraftWindow_Vitesses_decollage_tooltip")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_VR_kt_facultatif"), _vr, Loc.Get("AircraftWindow_Vitesses_decollage_tooltip")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_V2_kt_facultatif"), _v2, Loc.Get("AircraftWindow_Vitesses_decollage_tooltip")));
        grid.Children.Add(Field(Loc.Get("AircraftWindow_Descente_ft_min_facultatif"), _descent, Loc.Get("AircraftWindow_Idem_pour_la_descente_Vide")));
        form.Children.Add(grid);

        form.Children.Add(Field(Loc.Get("AircraftWindow_Notes_et_sources"), _notes));
        form.Children.Add(_derived);
        form.Children.Add(_error);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[] { _save, _duplicate, _delete })
        {
            b.Padding = new Thickness(12, 5, 12, 5);
            b.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(b);
        }
        var close = new Button { Content = Loc.Get("AircraftWindow_Fermer"), IsCancel = true, Padding = new Thickness(12, 5, 12, 5) };
        buttons.Children.Add(close);
        form.Children.Add(buttons);

        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        _list.SelectionChanged += (_, _) => { if (_list.SelectedItem is Aircraft a) Load(a, isNew: false); };
        _showAll.Click += (_, _) => Refill(_editing?.Id);
        _save.Click += Save_Click;
        _delete.Click += Delete_Click;
        _duplicate.Click += Duplicate_Click;
        foreach (var box in new[] { _cruise, _max, _burn, _fuel, _cruiseAlt, _ceiling, _climb, _descent, _reserve, _v1, _vr, _v2 })
            box.TextChanged += (_, _) => UpdateDerived();
        _vfr.Click += (_, _) => UpdateDerived();
        _ifr.Click += (_, _) => UpdateDerived();

        Refill(select);
        if (_list.SelectedItem is null) StartNew();
    }

    private static StackPanel Field(string label, Control box, string? tip = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 8, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Grey, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 2) });
        box.Padding = new Thickness(4, 3, 4, 3);
        if (tip is not null) box.ToolTip = tip;
        panel.Children.Add(box);
        return panel;
    }

    private IEnumerable<Aircraft> Visible() =>
        (_showAll.IsChecked == true ? _catalog.All : _catalog.ForMode(_mode))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>Recharge la liste et sélectionne la fiche demandée, ou la première.</summary>
    private void Refill(Guid? select)
    {
        _loading = true;
        var items = Visible().ToList();
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(a => a.Id == select) ?? items.FirstOrDefault();
        _loading = false;
        if (_list.SelectedItem is Aircraft a) Load(a, isNew: false);
        else if (_catalog.All.Count == 0 || items.Count == 0) StartNew();
    }

    private void StartNew()
    {
        var fresh = new Aircraft
        {
            Name = "", Vfr = _mode == FlightMode.Vfr, Ifr = _mode == FlightMode.Ifr,
            CruiseKt = 100, MaxKt = 120, BurnLph = 30, FuelCapacityL = 100, CruiseAltFt = _mode == FlightMode.Vfr ? 3000 : 8000,
        };
        _loading = true;
        _list.SelectedItem = null;
        _loading = false;
        Load(fresh, isNew: true);
        _name.Focus();
    }

    private void Load(Aircraft a, bool isNew)
    {
        _loading = true;
        _editing = a.Clone();
        _isNew = isNew;
        _name.Text = a.Name;
        _category.SelectedIndex = (int)a.Category;
        _vfr.IsChecked = a.Vfr;
        _ifr.IsChecked = a.Ifr;
        _cruise.Text = Num(a.CruiseKt);
        _max.Text = Num(a.MaxKt);
        _burn.Text = Num(a.BurnLph);
        _fuel.Text = Num(a.FuelCapacityL);
        _cruiseAlt.Text = Num(a.CruiseAltFt);
        _ceiling.Text = a.ServiceCeilingFt is { } c ? Num(c) : "";
        _climb.Text = a.ClimbFpm is { } cl ? Num(cl) : "";
        _v1.Text = a.V1Kt is { } v1 ? Num(v1) : "";
        _vr.Text = a.VrKt is { } vr ? Num(vr) : "";
        _v2.Text = a.V2Kt is { } v2 ? Num(v2) : "";
        _reserve.Text = a.ReserveMinutes is { } rv ? Num(rv) : "";
        _descent.Text = a.DescentFpm is { } de ? Num(de) : "";
        _notes.Text = a.Notes ?? "";
        _error.Text = "";
        _delete.IsEnabled = !isNew;
        _duplicate.IsEnabled = !isNew;
        _loading = false;
        UpdateDerived();
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.CurrentCulture);

    private static bool TryNum(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Lit la fiche telle qu'elle est saisie ; renvoie les champs numériques illisibles.</summary>
    private Aircraft Read(out List<string> problems)
    {
        problems = [];
        var a = (_editing ?? new Aircraft()).Clone();
        a.Name = _name.Text.Trim();
        a.Category = (AircraftCategory)Math.Max(_category.SelectedIndex, 0);
        a.Vfr = _vfr.IsChecked == true;
        a.Ifr = _ifr.IsChecked == true;
        a.CruiseKt = Parse(_cruise, Loc.Get("AircraftWindow_vitesse_de_croisiere"), problems);
        a.MaxKt = Parse(_max, Loc.Get("Aircraft_Label_max"), problems);
        a.BurnLph = Parse(_burn, Loc.Get("Aircraft_Label_burn"), problems);
        a.FuelCapacityL = Parse(_fuel, Loc.Get("Aircraft_Label_fuel"), problems);
        a.CruiseAltFt = Parse(_cruiseAlt, Loc.Get("AircraftWindow_altitude_de_croisiere"), problems);
        if (_ceiling.Text.Trim().Length == 0) a.ServiceCeilingFt = null;
        else a.ServiceCeilingFt = Parse(_ceiling, Loc.Get("Aircraft_Label_ceiling"), problems);
        a.ReserveMinutes = _reserve.Text.Trim().Length == 0 ? null : Parse(_reserve, Loc.Get("AircraftWindow_reserve"), problems);
        a.V1Kt = _v1.Text.Trim().Length == 0 ? null : Parse(_v1, "V1", problems);
        a.VrKt = _vr.Text.Trim().Length == 0 ? null : Parse(_vr, "VR", problems);
        a.V2Kt = _v2.Text.Trim().Length == 0 ? null : Parse(_v2, "V2", problems);
        a.ClimbFpm = _climb.Text.Trim().Length == 0 ? null : Parse(_climb, Loc.Get("AircraftWindow_taux_de_montee"), problems);
        a.DescentFpm = _descent.Text.Trim().Length == 0 ? null : Parse(_descent, Loc.Get("AircraftWindow_taux_de_descente"), problems);
        a.Notes = string.IsNullOrWhiteSpace(_notes.Text) ? null : _notes.Text.Trim();
        return a;
    }

    private static double Parse(TextBox box, string label, List<string> problems)
    {
        if (TryNum(box.Text, out double v)) return v;
        problems.Add(Loc.F("AircraftWindow_Le_champ_n_est_pas", label));
        return double.NaN;
    }

    private void UpdateDerived()
    {
        if (_loading) return;
        var a = Read(out var problems);
        if (problems.Count > 0 || a.Validate().Any(e => e.Contains("positi") || e.Contains(Loc.Get("Aircraft_Word_reserve"))))
        {
            _derived.Text = "";
            return;
        }
        string mode = _mode == FlightMode.Vfr ? "VFR" : "IFR";
        _derived.Text = Loc.F("AircraftWindow_Autonomie_portee_tout_carburant_NM", NavMath.FormatTime(a.EnduranceMinutes), a.RangeNm) +
                        Loc.F("AircraftWindow_portee_utile_NM_reserve_min", a.UsableRangeNm(_mode), a.ReserveFor(_mode), mode);
    }

    // ---------- actions ----------

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var a = Read(out var problems);
        if (problems.Count > 0) { Show(problems); return; }
        var errors = _isNew ? _catalog.Add(a) : _catalog.Update(a);
        if (errors.Count > 0) { Show(errors); return; }
        if (!Persist()) return;
        Changed = true;
        Refill(a.Id);
        // La fiche enregistrée peut sortir de la liste affichée (capacité retirée pour ce type de navigation).
        if (_list.SelectedItem is null) { StartNew(); _error.Text = Loc.Get("AircraftWindow_Enregistre_Cet_appareil_n_est"); _error.Foreground = Grey; }
        else { _error.Text = Loc.Get("AircraftWindow_Enregistre"); _error.Foreground = Grey; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || _isNew) return;
        if (MessageBox.Show(this, Loc.F("AircraftWindow_Supprimer_Cette_suppression_est_definitive", _editing.Name),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _catalog.Remove(_editing.Id);
        if (!Persist()) return;
        Changed = true;
        Refill(null);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_editing is null || _isNew) return;
        var copy = _editing.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = UniqueName(_editing.Name + Loc.Get("Aircraft_Copy_suffix"));
        _list.SelectedItem = null;
        Load(copy, isNew: true);
        _name.Focus();
        _name.SelectAll();
    }

    private string UniqueName(string wanted)
    {
        string name = wanted;
        for (int n = 2; _catalog.All.Any(a => string.Equals(a.Name, name, StringComparison.CurrentCultureIgnoreCase)); n++)
            name = $"{wanted} {n}";
        return name;
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        int added = _catalog.RestoreDefaults();
        if (added > 0 && !Persist()) return;
        Changed |= added > 0;
        Refill(_editing?.Id);
        _error.Foreground = Grey;
        _error.Text = added > 0 ? Loc.F("AircraftWindow_appareil_s_par_defaut_remis", added) : Loc.Get("AircraftWindow_Les_appareils_par_defaut_sont");
    }

    private bool Persist()
    {
        try { _catalog.Save(); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Show([Loc.Get("AircraftWindow_Enregistrement_impossible") + ex.Message]);
            return false;
        }
    }

    private void Show(IEnumerable<string> messages)
    {
        _error.Foreground = Red;
        _error.Text = string.Join("\n", messages);
    }
}
