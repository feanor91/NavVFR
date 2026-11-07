using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VFRNav.Core.Localization;
using VFRNav.Core.Weather;

namespace VFRNav.Tests;

public class LocalizationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "VFRNav.Core"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Dépôt introuvable.");
    }

    private static Dictionary<string, string> ReadResx(string file) =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "VFRNav.Core", "Localization", file)).Root!
            .Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    private static T InEnglish<T>(Func<T> action)
    {
        var ui = CultureInfo.CurrentUICulture; var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
            return action();
        }
        finally { CultureInfo.CurrentUICulture = ui; CultureInfo.CurrentCulture = culture; }
    }

    private static readonly Regex Placeholder = new(@"\{\d+(?:,-?\d+)?(?::[^}]*)?\}");

    [Fact]
    public void English_has_every_key_with_the_same_placeholders()
    {
        var fr = ReadResx("Strings.resx");
        var en = ReadResx("Strings.en.resx");
        Assert.Equal(fr.Keys.OrderBy(k => k), en.Keys.OrderBy(k => k));
        foreach (var (key, text) in fr)
        {
            Assert.False(string.IsNullOrEmpty(en[key]), $"Traduction vide : {key}");
            Assert.Equal(Placeholder.Matches(text).Select(m => m.Value).Order(), Placeholder.Matches(en[key]).Select(m => m.Value).Order());
        }
    }

    [Fact]
    public void English_keeps_the_spaces_around_fragments()
    {
        var fr = ReadResx("Strings.resx");
        var en = ReadResx("Strings.en.resx");
        foreach (var (key, text) in fr)
        {
            // Les guillemets français « » portent leurs espaces : l'anglais n'en met pas.
            if (text.EndsWith("« ", StringComparison.Ordinal) || text.StartsWith(" »", StringComparison.Ordinal)) continue;
            Assert.True(text.EndsWith(' ') == en[key].EndsWith(' '), $"Espace final : {key}");
            Assert.True(text.StartsWith(' ') == en[key].StartsWith(' '), $"Espace initial : {key}");
            Assert.True(text.EndsWith('\n') == en[key].EndsWith('\n'), $"Retour final : {key}");
        }
    }

    [Fact]
    public void Every_key_used_in_the_code_and_the_xaml_exists()
    {
        var known = ReadResx("Strings.resx").Keys.ToHashSet();
        var used = new List<(string File, string Key)>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Loc\.(?:Get|F)\((?:[\w\.]+,\s*)?""([A-Za-z0-9_]+)"""))
                used.Add((Path.GetFileName(file), m.Groups[1].Value));
        foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), "src", "VFRNav.App", "MainWindow.xaml")), @"\{local:Tr (\w+)\}"))
            used.Add(("MainWindow.xaml", m.Groups[1].Value));

        Assert.True(used.Count > 500);
        Assert.Empty(used.Where(u => !known.Contains(u.Key)).Select(u => $"{u.File}: {u.Key}"));
    }

    [Fact]
    public void No_resource_name_differs_from_another_only_by_its_case()
    {
        // La compilation des ressources compare les noms sans tenir compte de la casse et ignore les doublons : le texte s'afficherait sous forme de clé.
        foreach (var file in new[] { "Strings.resx", "Strings.en.resx" })
        {
            var duplicates = ReadResx(file).Keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => string.Join("/", g));
            Assert.Empty(duplicates);
        }
    }

    [Fact]
    public void Language_is_resolved_from_the_setting_then_windows_then_french()
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR"); var en = CultureInfo.GetCultureInfo("en-US"); var de = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("en", Loc.Resolve("en", fr));
        Assert.Equal("fr", Loc.Resolve("fr", en));
        Assert.Equal("en", Loc.Resolve("", en));
        Assert.Equal("fr", Loc.Resolve(null, de));
        Assert.Equal("fr", Loc.Resolve("xx", en));
    }

    [Fact]
    public void Texts_follow_the_ui_language_and_fall_back_to_the_key()
    {
        Assert.Equal("Aucune", Loc.Get("Ifr_Aucune"));
        Assert.Equal("None", InEnglish(() => Loc.Get("Ifr_Aucune")));
        Assert.Equal("Cle_inexistante", Loc.Get("Cle_inexistante"));
    }

    [Fact]
    public void Formatted_texts_keep_their_numbers()
    {
        string en = InEnglish(() => Loc.F("Ifr_Route_summary", "LFPG A1 LFMN", 359.46, 7));
        Assert.Equal("LFPG A1 LFMN\n359.5 NM, 7 points.", en);
    }

    [Fact]
    public void Metar_is_decoded_in_english()
    {
        string text = InEnglish(() => string.Join("\n", WeatherDecoder.DecodeMetar(
            "METAR LFPO 311030Z AUTO 27010G22KT 240V300 9999 -RA SCT025 BKN040 12/08 Q1013 NOSIG").Lines));
        Assert.Contains("Automatic observation", text);
        Assert.Contains("Wind from 270° (west), 10 kt (19 km/h), gusting to 22 kt", text);
        Assert.Contains("Light rain", text);
        Assert.Contains("Scattered clouds (3 to 4/8) at 2500 ft", text);
        Assert.Contains("Temperature 12 °C, dew point 8 °C", text);
        Assert.DoesNotContain("Vent", text);
    }
}
