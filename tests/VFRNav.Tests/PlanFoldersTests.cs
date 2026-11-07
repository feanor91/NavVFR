using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class PlanFoldersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vfrnav-plans-" + Guid.NewGuid().ToString("N"));

    public PlanFoldersTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void A_plain_folder_resolves_to_itself_and_a_missing_one_does_not_throw()
    {
        Assert.Equal(_root, PlanFolders.Resolve(_root));
        string missing = Path.Combine(_root, "absent");
        Assert.Equal(missing, PlanFolders.Resolve(missing));
    }

    [Fact]
    public void Plans_are_listed_newest_first_and_only_pln_files_of_the_folder()
    {
        string a = Path.Combine(_root, "ancien.pln"), b = Path.Combine(_root, "RECENT.PLN"), c = Path.Combine(_root, "notes.txt");
        File.WriteAllText(a, "<x/>"); File.WriteAllText(b, "<x/>"); File.WriteAllText(c, "x");
        File.SetLastWriteTime(a, new DateTime(2026, 1, 1));
        File.SetLastWriteTime(b, new DateTime(2026, 6, 1));
        Directory.CreateDirectory(Path.Combine(_root, "sous"));
        File.WriteAllText(Path.Combine(_root, "sous", "cache.pln"), "<x/>");

        var plans = PlanFolders.PlansIn([_root]);

        Assert.Equal(["RECENT.PLN", "ancien.pln"], plans.Select(p => p.Name));
        Assert.All(plans, p => Assert.Equal(_root, p.Folder));
    }

    [Fact]
    public void An_unreadable_folder_is_skipped_without_failing_the_others()
    {
        File.WriteAllText(Path.Combine(_root, "un.pln"), "<x/>");
        var plans = PlanFolders.PlansIn([Path.Combine(_root, "absent"), _root]);
        Assert.Single(plans);
    }

    [Fact]
    public void Msfs_folders_are_found_for_the_store_and_steam_layouts_and_only_when_they_exist()
    {
        string local = Path.Combine(_root, "Local"), roaming = Path.Combine(_root, "Roaming");
        Assert.Empty(PlanFolders.MsfsFolders(local, roaming, driveRoots: []));

        string store2024 = Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalState");
        string steam2020 = Path.Combine(roaming, "Microsoft Flight Simulator");
        Directory.CreateDirectory(store2024);
        Directory.CreateDirectory(steam2020);

        var folders = PlanFolders.MsfsFolders(local, roaming, driveRoots: []);

        Assert.Equal([store2024, steam2020], folders);   // MSFS 2024 avant MSFS 2020
    }

    [Fact]
    public void Junction_targets_lose_their_nt_prefix_and_relative_targets_become_absolute()
    {
        Assert.Equal(@"D:\WpSystem\x", PlanFolders.Clean(@"\??\D:\WpSystem\x", null));
        Assert.Equal(@"D:\WpSystem\x", PlanFolders.Clean(@"\\?\D:\WpSystem\x", null));
        Assert.Equal(@"D:\WpSystem\x", PlanFolders.Clean(@"D:\WpSystem\x", @"C:\autre"));
        Assert.Equal(Path.GetFullPath(@"C:\base\sous"), PlanFolders.Clean(@"sous", @"C:\base"));
    }

    [Fact]
    public void Store_folders_are_found_directly_under_wpsystem_without_following_any_junction()
    {
        string drive = Path.Combine(_root, "D");
        string folder = Path.Combine(drive, "WpSystem", "S-1-5-21-1", "AppData", "Local", "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalState");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(_root, "E"));   // un disque sans WpSystem

        Assert.Equal([folder], PlanFolders.WpSystemFolders([Path.Combine(_root, "E"), drive, Path.Combine(_root, "absent")]));
        Assert.Equal([folder], PlanFolders.MsfsFolders(Path.Combine(_root, "L"), Path.Combine(_root, "R"), driveRoots: [drive]));
    }

    [Fact]
    public void A_junction_resolves_to_its_target_when_one_can_be_created()
    {
        string target = Path.Combine(_root, "cible"), link = Path.Combine(_root, "lien");
        Directory.CreateDirectory(target);
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var process = System.Diagnostics.Process.Start(psi)!) process.WaitForExit();
        if (!Directory.Exists(link)) return;   // création impossible ici (système de fichiers, droits) : rien à vérifier

        try { Assert.Equal(target, PlanFolders.Resolve(link), ignoreCase: true); }
        finally { Directory.Delete(link); }   // retire la jonction seule : la suppression récursive du dossier de test ne sait pas le faire
    }
}
