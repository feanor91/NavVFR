namespace VFRNav.Core.Services;

/// <summary>Un plan de vol .pln trouvé dans un dossier.</summary>
public sealed record PlanFile(string Path, string Name, DateTime Modified, string Folder);

/// <summary>
/// Dossiers où MSFS range ses plans de vol. Celui de la version Microsoft Store (<c>...\Packages\Microsoft.Limitless_...\LocalState</c>) est une
/// jonction vers un autre disque : le sélecteur de fichiers de Windows refuse parfois de la traverser (« point de montage non approuvé »)
/// alors que le programme la lit sans problème. On l'ouvre donc par son vrai chemin, ou on liste les plans nous-mêmes.
/// </summary>
public static class PlanFolders
{
    /// <summary>
    /// Vrai chemin d'un dossier : la cible d'une jonction ou d'un lien symbolique, sinon le chemin tel quel. La cible est lue sans traverser le lien
    /// (<see cref="FileSystemInfo.LinkTarget"/>) : certains processus (protection « Redirection Guard » de Windows) n'ont pas le droit de suivre
    /// la jonction du Microsoft Store, alors qu'ils peuvent en lire la destination et ouvrir celle-ci directement.
    /// </summary>
    public static string Resolve(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is { Length: > 0 } target) return Clean(target, Path.GetDirectoryName(path));
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return path; }
    }

    /// <summary>Retire le préfixe « \\?\ » ou « \??\ » d'une cible de jonction et la rend absolue.</summary>
    public static string Clean(string target, string? baseFolder)
    {
        foreach (string prefix in new[] { @"\??\", @"\\?\" })
            if (target.StartsWith(prefix, StringComparison.Ordinal)) target = target[prefix.Length..];
        return Path.IsPathRooted(target) || baseFolder is null ? target : Path.GetFullPath(Path.Combine(baseFolder, target));
    }

    /// <summary>
    /// Dossiers de plans du Microsoft Store trouvés directement dans <c>X:\WpSystem\&lt;SID&gt;\AppData\Local\Packages\...</c> sur chaque disque : le dossier
    /// réel des jeux installés ailleurs que sur C:, sans passer par la jonction de <c>%LocalAppData%\Packages</c>.
    /// </summary>
    public static IReadOnlyList<string> WpSystemFolders(IEnumerable<string> driveRoots)
    {
        string[] packages = ["Microsoft.Limitless_8wekyb3d8bbwe", "Microsoft.FlightSimulator_8wekyb3d8bbwe"];
        var found = new List<string>();
        foreach (string root in driveRoots)
        {
            try
            {
                string wp = Path.Combine(root, "WpSystem");
                if (!Directory.Exists(wp)) continue;
                foreach (string sid in Directory.EnumerateDirectories(wp))
                    foreach (string package in packages)
                    {
                        string folder = Path.Combine(sid, "AppData", "Local", "Packages", package, "LocalState");
                        if (Directory.Exists(folder)) found.Add(folder);
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* disque illisible : on passe au suivant */ }
        }
        return found;
    }

    /// <summary>Dossiers de plans de MSFS 2024 et 2020 (Microsoft Store et Steam) qui existent sur cette machine, avec leur vrai chemin.</summary>
    public static IReadOnlyList<string> MsfsFolders(string? localAppData = null, string? appData = null, IEnumerable<string>? driveRoots = null)
    {
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] candidates =
        [
            Path.Combine(localAppData, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalState"),          // MSFS 2024, Store
            Path.Combine(appData, "Microsoft Flight Simulator 2024"),                                           // MSFS 2024, Steam
            Path.Combine(localAppData, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalState"),    // MSFS 2020, Store
            Path.Combine(appData, "Microsoft Flight Simulator"),                                                // MSFS 2020, Steam
        ];
        var found = new List<string>();
        foreach (string candidate in candidates)
        {
            string real = Resolve(candidate);
            if (Directory.Exists(real) && !found.Contains(real, StringComparer.OrdinalIgnoreCase)) found.Add(real);
        }
        // Ceux de WpSystem, trouvés sans passer par la jonction (utile quand le processus n'a pas le droit de la suivre).
        driveRoots ??= DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName);
        foreach (string folder in WpSystemFolders(driveRoots))
            if (!found.Contains(folder, StringComparer.OrdinalIgnoreCase)) found.Add(folder);
        return found;
    }

    /// <summary>Plans .pln des dossiers donnés (sans les sous-dossiers), du plus récent au plus ancien.</summary>
    public static IReadOnlyList<PlanFile> PlansIn(IEnumerable<string> folders)
    {
        var plans = new List<PlanFile>();
        foreach (string folder in folders)
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.pln"))
                    plans.Add(new PlanFile(file, Path.GetFileName(file), File.GetLastWriteTime(file), folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* dossier illisible : on passe au suivant */ }
        }
        return [.. plans.OrderByDescending(p => p.Modified).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }
}
