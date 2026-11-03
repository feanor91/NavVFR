using System.Reflection;

namespace VFRNav.App;

/// <summary>Numéro de version de l'application : celui du tag de la release (workflow GitHub), « développement » pour une compilation locale.</summary>
public static class AppVersion
{
    /// <summary>« 0.5.0 », ou « développement » quand l'application n'est pas une version publiée.</summary>
    public static string Text { get; } = Read(typeof(AppVersion).Assembly);

    /// <summary>Titre de la fenêtre principale : « VFRNav 0.5.0 ».</summary>
    public static string Title => Text == Development ? "VFRNav (développement)" : $"VFRNav {Text}";

    private const string Development = "développement";

    internal static string Read(Assembly assembly)
    {
        string raw = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "";
        string version = raw.Split('+')[0].Trim(); // le SDK ajoute « +empreinte du commit »
        return version is "" or "0.0.0" or "1.0.0" ? Development : version;
    }
}
