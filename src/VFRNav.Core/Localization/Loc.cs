using System.Globalization;
using System.Resources;

namespace VFRNav.Core.Localization;

/// <summary>
/// Textes de l'application, lus dans les fichiers de ressources <c>Strings.resx</c> (français, langue neutre)
/// et <c>Strings.en.resx</c> (anglais). La langue est celle de <see cref="CultureInfo.CurrentUICulture"/> ;
/// une clé absente d'une langue retombe sur le français.
/// </summary>
public static class Loc
{
    private static readonly ResourceManager Manager = new("VFRNav.Core.Localization.Strings", typeof(Loc).Assembly);

    /// <summary>Langues proposées : code de culture, nom affiché dans sa propre langue.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = [("fr", "Français"), ("en", "English")];

    /// <summary>Texte de la clé dans la langue courante.</summary>
    public static string Get(string key) => Manager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Texte de la clé avec ses paramètres {0}, {1}... mis en forme avec la culture courante.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Comme <see cref="F(string, object?[])"/> avec une culture de mise en forme imposée (nombres, dates).</summary>
    public static string F(IFormatProvider provider, string key, params object?[] args) => string.Format(provider, Get(key), args);

    /// <summary>Culture des nombres et des dates affichés : celle de la langue de l'application, indépendante de celle de Windows.</summary>
    public static CultureInfo FormatCulture =>
        CultureInfo.GetCultureInfo(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en-GB" : "fr-FR");

    /// <summary>Langue à utiliser : le réglage (« fr », « en »), sinon celle de Windows si on la propose, sinon le français.</summary>
    public static string Resolve(string? setting, CultureInfo windows)
    {
        string wanted = string.IsNullOrWhiteSpace(setting) ? windows.TwoLetterISOLanguageName : setting.Trim().ToLowerInvariant();
        return Languages.Any(l => l.Code == wanted) ? wanted : "fr";
    }

    /// <summary>Applique la langue à toute l'application (textes, dates et nombres : l'anglais suit les usages britanniques).</summary>
    public static void Apply(string language)
    {
        var ui = CultureInfo.GetCultureInfo(language);
        var format = language == "en" ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.DefaultThreadCurrentCulture = format;
        CultureInfo.CurrentUICulture = ui;
        CultureInfo.CurrentCulture = format;
    }
}

/// <summary>
/// Valeur construite avec des textes traduits (table, liste) : elle est refaite pour chaque langue, au lieu de garder
/// la langue du premier appel.
/// </summary>
public sealed class Localized<T>(Func<T> build) where T : class
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, T> _byLanguage = new();

    public T Value => _byLanguage.GetOrAdd(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, _ => build());
}
