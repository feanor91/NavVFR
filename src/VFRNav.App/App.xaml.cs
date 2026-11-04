using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using VFRNav.Core.Localization;
using VFRNav.Core.Services;

namespace VFRNav.App;

/// <summary>Interaction logic for App.xaml</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // La langue se fixe avant la création de la fenêtre : tous les textes sont lus une seule fois, au chargement.
        string language = Loc.Resolve(AppSettings.Load().Language, CultureInfo.CurrentUICulture);
        Loc.Apply(language);
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
        base.OnStartup(e);
    }
}
