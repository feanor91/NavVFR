namespace VFRNav.Core.Ifr;

/// <summary>
/// Clé d'un point de navigation. Les noms de cinq lettres se répètent d'un pays à l'autre : hors de France, la clé est « NOM/RÉGION »
/// (ex. « ABCDE/EG »). En France (région LF) et quand la région est inconnue, c'est le nom seul, comme dans l'export du SIA.
/// </summary>
public static class FixKey
{
    /// <summary>Région OACI de la France métropolitaine, couverte par l'export du SIA.</summary>
    public const string France = "LF";

    public static string Make(string ident, string? region)
    {
        ident = ident.Trim();
        region = region?.Trim().ToUpperInvariant() ?? "";
        return region.Length == 0 || region == France ? ident : ident + "/" + region;
    }

    /// <summary>Nom à afficher : la clé sans sa région.</summary>
    public static string Display(string key)
    {
        int slash = key.IndexOf('/');
        return slash < 0 ? key : key[..slash];
    }

    /// <summary>Région portée par la clé : « LF » pour un nom seul.</summary>
    public static string Region(string key)
    {
        int slash = key.IndexOf('/');
        return slash < 0 ? France : key[(slash + 1)..];
    }
}
