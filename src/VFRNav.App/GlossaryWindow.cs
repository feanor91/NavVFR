using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VFRNav.Core.Models;
using VFRNav.Core.Localization;

namespace VFRNav.App;

/// <summary>Glossaire des sigles des cartes et de MSFS Rhumb, avec recherche. Reste ouverte à côté de la carte.</summary>
public sealed class GlossaryWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(11, 42, 85));
    private static readonly Brush Grey = new SolidColorBrush(Color.FromRgb(95, 105, 120));
    private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(214, 222, 234));

    private readonly TextBox _search = new() { Padding = new Thickness(8, 6, 8, 6), FontSize = 14, Margin = new Thickness(12, 12, 12, 6) };
    private readonly StackPanel _list = new() { Margin = new Thickness(12, 0, 12, 12) };
    private readonly TextBlock _count = new() { Foreground = Grey, Margin = new Thickness(14, 0, 12, 6), FontSize = 11.5 };

    public GlossaryWindow()
    {
        Title = Loc.Get("GlossaryWindow_Glossaire_sigles_des_cartes");
        Width = 560;
        Height = 720;
        Background = Brushes.White;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/VFRNav.ico"));

        var hint = new TextBlock
        {
            Text = Loc.Get("GlossaryWindow_Cherche_un_sigle_TMA_QNH"),
            Foreground = Grey,
            Margin = new Thickness(14, 10, 12, 0),
            FontSize = 12
        };
        _search.TextChanged += (_, _) => Render();

        var top = new StackPanel();
        top.Children.Add(hint);
        top.Children.Add(_search);
        top.Children.Add(_count);

        var footer = new TextBlock
        {
            Text = AirspaceGuide.Disclaimer,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = FontStyles.Italic,
            Foreground = Grey,
            FontSize = 11,
            Margin = new Thickness(14, 6, 14, 8)
        };

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(footer);
        root.Children.Add(scroll);
        Content = root;

        Loaded += (_, _) => { Render(); _search.Focus(); };
    }

    /// <summary>Met le focus sur la recherche, pré-remplie si besoin (« TMA » depuis l'aide d'une zone).</summary>
    public void ShowTerm(string? term = null)
    {
        if (term is not null) _search.Text = term;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _search.Focus();
        _search.SelectAll();
    }

    private void Render()
    {
        var results = Glossary.Search(_search.Text);
        _list.Children.Clear();
        _count.Text = results.Count == 0 ? Loc.Get("GlossaryWindow_Aucun_resultat") : Loc.F("GlossaryWindow_entree_s", results.Count);

        foreach (string category in Glossary.Categories)
        {
            var inCategory = results.Where(e => e.Category == category).ToList();
            if (inCategory.Count == 0) continue;
            _list.Children.Add(new TextBlock
            {
                Text = category.ToUpper(VFRNav.Core.Localization.Loc.FormatCulture),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = Accent,
                Margin = new Thickness(0, 14, 0, 4)
            });
            foreach (var entry in inCategory) _list.Children.Add(Row(entry));
        }
    }

    private static UIElement Row(GlossaryEntry e)
    {
        var term = new TextBlock { Text = e.Term, FontWeight = FontWeights.Bold, FontSize = 13.5, Foreground = Accent, TextWrapping = TextWrapping.Wrap };
        var meaning = new TextBlock { Text = e.Meaning, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        var explanation = new TextBlock { Text = e.Explanation, FontSize = 12.5, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(term);
        panel.Children.Add(meaning);
        panel.Children.Add(explanation);
        return new Border { Child = panel, BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6, 0, 7) };
    }
}
