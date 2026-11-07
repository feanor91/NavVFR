using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VFRNav.Core.Localization;
using VFRNav.Core.Services;

namespace VFRNav.App;

/// <summary>
/// Liste des plans de vol .pln des dossiers de MSFS, pour en ouvrir un sans passer par le sélecteur de fichiers de Windows
/// (qui refuse d'entrer dans le dossier du Microsoft Store : « point de montage non approuvé »).
/// </summary>
public sealed class PlanPickerWindow : Window
{
    private readonly ListView _list = new() { SelectionMode = SelectionMode.Single };
    private readonly Button _open = new() { Content = Loc.Get("PlanPicker_Ouvrir"), IsDefault = true, IsEnabled = false, Padding = new Thickness(18, 5, 18, 5), Margin = new Thickness(0, 0, 8, 0) };

    public string? SelectedPath { get; private set; }

    public PlanPickerWindow(IReadOnlyList<PlanFile> plans, IReadOnlyList<string>? searched = null)
    {
        Title = Loc.Get("PlanPicker_Titre");
        Width = 720;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var fr = Loc.FormatCulture;
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = Loc.Get("PlanPicker_Nom"), Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(PlanFile.Name)) });
        grid.Columns.Add(new GridViewColumn
        {
            Header = Loc.Get("PlanPicker_Modifie"), Width = 140,
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(PlanFile.Modified)) { StringFormat = "g", ConverterCulture = fr },
        });
        grid.Columns.Add(new GridViewColumn { Header = Loc.Get("PlanPicker_Dossier"), Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(PlanFile.Folder)) });
        _list.View = grid;
        _list.ItemsSource = plans;
        _list.SelectionChanged += (_, _) => _open.IsEnabled = _list.SelectedItem is not null;
        _list.MouseDoubleClick += (_, _) => Accept();
        _open.Click += (_, _) => Accept();

        var cancel = new Button { Content = Loc.Get("PlanPicker_Annuler"), IsCancel = true, Padding = new Thickness(18, 5, 18, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_open);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        if (plans.Count == 0)
        {
            var none = new TextBlock { Text = Loc.Get("PlanPicker_Aucun") + (searched is { Count: > 0 } ? "\n" + Loc.Get("PlanPicker_Dossiers_examines") + "\n" + string.Join("\n", searched) : "\n" + Loc.Get("PlanPicker_Aucun_dossier")), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(none, Dock.Top);
            root.Children.Add(none);
        }
        root.Children.Add(_list);
        Content = root;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not PlanFile plan) return;
        SelectedPath = plan.Path;
        DialogResult = true;
    }
}
