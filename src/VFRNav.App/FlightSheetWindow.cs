using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using VFRNav.Core.Localization;

namespace VFRNav.App;

/// <summary>Aperçu de la feuille de route, avec impression (et enregistrement en PDF via l'imprimante « Microsoft Print to PDF »).</summary>
public sealed class FlightSheetWindow : Window
{
    private readonly FlowDocument _document;

    public FlightSheetWindow(FlowDocument document, string title)
    {
        _document = document;
        Title = title;
        Width = 900;
        Height = 950;
        Background = System.Windows.Media.Brushes.WhiteSmoke;

        var print = new Button { Content = Loc.Get("FlightSheetWindow_Imprimer_PDF"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8) };
        print.Click += (_, _) => Print();
        var close = new Button { Content = Loc.Get("AircraftWindow_Fermer"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 8, 8) };
        close.Click += (_, _) => Close();
        var hint = new TextBlock
        {
            Text = Loc.Get("FlightSheetWindow_Pour_un_PDF_choisis_l"),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(print);
        bar.Children.Add(close);
        bar.Children.Add(hint);

        var viewer = new FlowDocumentScrollViewer
        {
            Document = document,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Zoom = 100,
            IsToolBarVisible = false
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(viewer);
        Content = root;
    }

    private void Print()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;

        // La mise en page d'impression suit la zone imprimable de l'imprimante ; on remet celle de l'écran ensuite.
        double width = _document.PageWidth, height = _document.PageHeight, column = _document.ColumnWidth;
        var padding = _document.PagePadding;
        try
        {
            _document.PageWidth = dialog.PrintableAreaWidth;
            _document.PageHeight = dialog.PrintableAreaHeight;
            _document.PagePadding = new Thickness(36);
            _document.ColumnWidth = dialog.PrintableAreaWidth - 72;
            dialog.PrintDocument(((IDocumentPaginatorSource)_document).DocumentPaginator, Title);
        }
        finally
        {
            _document.PageWidth = width;
            _document.PageHeight = height;
            _document.PagePadding = padding;
            _document.ColumnWidth = column;
        }
    }
}
