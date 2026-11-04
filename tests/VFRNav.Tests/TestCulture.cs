using System.Globalization;
using System.Runtime.CompilerServices;

namespace VFRNav.Tests;

/// <summary>
/// Les textes suivent la langue de l'interface. Les tests attendent le français, quelle que soit la langue de la machine
/// (le poste d'un développeur ou le Windows en anglais de la CI) ; les tests en anglais la changent le temps d'un appel.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseFrenchTexts()
    {
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("fr");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr");
    }
}
