namespace VFRNav.Core.Weather;

/// <summary>
/// Cache sur disque des tuiles de nuages déjà transformées. Le satellite publie une image toutes les 10 à 15 minutes : les tuiles sont rangées
/// par « créneau » de 15 minutes, si bien qu'un retour sur une zone déjà vue, un zoom arrière ou un redémarrage dans le même créneau
/// sont instantanés au lieu de refaire des requêtes lentes au service WMS. Les créneaux plus anciens sont supprimés.
/// </summary>
public sealed class CloudTileCache(string root)
{
    public const int SlotMinutes = 15;

    public string Root { get; } = root;

    /// <summary>Numéro du créneau de 15 minutes qui contient cet instant.</summary>
    public static long SlotOf(DateTimeOffset time) => time.ToUnixTimeSeconds() / (SlotMinutes * 60L);

    private string PathOf(string layer, long slot, int z, int x, int y) =>
        Path.Combine(Root, SafeName(layer), slot.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{z}-{x}-{y}.png");

    private static string SafeName(string layer) => string.Concat(layer.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));

    public byte[]? Get(string layer, long slot, int z, int x, int y)
    {
        try
        {
            string file = PathOf(layer, slot, z, x, y);
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Put(string layer, long slot, int z, int x, int y, byte[] png)
    {
        try
        {
            string file = PathOf(layer, slot, z, x, y);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(temporary, png);
            File.Move(temporary, file, overwrite: true);   // jamais de fichier à moitié écrit pour un autre lecteur
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* le cache est facultatif */ }
    }

    /// <summary>Supprime les créneaux antérieurs à <paramref name="currentSlot"/> (la couche affichée ne les relit plus).</summary>
    public void PruneOlderThan(long currentSlot)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (string layerDir in Directory.EnumerateDirectories(Root))
                foreach (string slotDir in Directory.EnumerateDirectories(layerDir))
                    if (long.TryParse(Path.GetFileName(slotDir), out long slot) && slot < currentSlot)
                        Directory.Delete(slotDir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sans importance : retenté au prochain démarrage */ }
    }
}
