using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VFRNav.App;

/// <summary>Informations en direct du point suivi (altitude du sol, météo, zones...). Reste ouverte à côté de la carte.</summary>
public sealed class LiveInfoWindow : Window
{
    private readonly TextBox _text = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        FontSize = 13.5,
        Padding = new Thickness(14, 12, 14, 12),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    public LiveInfoWindow(string text)
    {
        Title = "Infos en direct";
        Width = 430;
        Height = 480;
        MinWidth = 300;
        MinHeight = 200;
        Background = Brushes.White;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/VFRNav.ico"));
        Content = _text;
        SetText(text);
    }

    /// <summary>Remplace le texte affiché sans toucher à la position de défilement ni à la sélection en cours si rien n'a changé.</summary>
    public void SetText(string text)
    {
        if (_text.Text != text) _text.Text = text;
    }

    /// <summary>Ramène la fenêtre devant, même si elle était réduite.</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }
}
