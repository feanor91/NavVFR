using System.Text.Json;
using System.Text.Json.Serialization;
using VFRNav.Core.Models;
using VFRNav.Core.Localization;

namespace VFRNav.Core.Services;

/// <summary>
/// Liste des appareils de l'utilisateur : création, lecture, modification, suppression, et sauvegarde dans
/// <c>%AppData%\VFRNav\aircraft.json</c>. Au premier lancement, trois appareils sont créés (Cessna 172, DR400, A320neo).
/// </summary>
public sealed class AircraftCatalog
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<Aircraft> _items;

    public AircraftCatalog(IEnumerable<Aircraft> items) => _items = items.ToList();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VFRNav", "aircraft.json");

    public IReadOnlyList<Aircraft> All => _items;

    /// <summary>Message si le fichier existait mais était illisible (les appareils par défaut sont alors utilisés).</summary>
    public string? LoadWarning { get; private init; }

    public Aircraft? Find(Guid id) => _items.FirstOrDefault(a => a.Id == id);

    /// <summary>Appareils proposés pour un type de navigation, par ordre alphabétique.</summary>
    public IReadOnlyList<Aircraft> ForMode(FlightMode mode) =>
        _items.Where(a => a.Supports(mode)).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // ---------- CRUD : chaque opération renvoie la liste des erreurs, vide si elle a réussi ----------

    public IReadOnlyList<string> Add(Aircraft aircraft)
    {
        var errors = Check(aircraft, ignoreId: null);
        if (errors.Count == 0) _items.Add(aircraft.Clone());
        return errors;
    }

    public IReadOnlyList<string> Update(Aircraft aircraft)
    {
        int index = _items.FindIndex(a => a.Id == aircraft.Id);
        if (index < 0) return [Loc.Get("Aircraft_Gone")];
        var errors = Check(aircraft, ignoreId: aircraft.Id);
        if (errors.Count == 0) _items[index] = aircraft.Clone();
        return errors;
    }

    public bool Remove(Guid id) => _items.RemoveAll(a => a.Id == id) > 0;

    /// <summary>Remet les appareils par défaut qui manquent. Ceux qui existent (même modifiés) ne sont pas touchés.</summary>
    public int RestoreDefaults()
    {
        int added = 0;
        foreach (var d in Defaults())
        {
            if (_items.Any(a => a.Id == d.Id)) continue;
            // Un nom déjà pris par une autre fiche est complété pour rester unique.
            if (_items.Any(a => SameName(a.Name, d.Name))) d.Name += Loc.Get("AircraftCatalog_defaut");
            _items.Add(d);
            added++;
        }
        return added;
    }

    private IReadOnlyList<string> Check(Aircraft aircraft, Guid? ignoreId)
    {
        var errors = aircraft.Validate().ToList();
        if (!string.IsNullOrWhiteSpace(aircraft.Name) && _items.Any(a => a.Id != ignoreId && SameName(a.Name, aircraft.Name)))
            errors.Add(Loc.Get("AircraftCatalog_Un_appareil_porte_deja_ce"));
        return errors;
    }

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // ---------- fichier ----------

    public static AircraftCatalog Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AircraftCatalog(Defaults());
        try
        {
            var items = JsonSerializer.Deserialize<List<Aircraft>>(File.ReadAllText(path), Json) ?? [];
            // Une fiche invalide ou en double (fichier édité à la main) est écartée plutôt que de bloquer le programme.
            var valid = new List<Aircraft>();
            foreach (var a in items)
                if (a.Validate().Count == 0 && valid.All(v => v.Id != a.Id && !SameName(v.Name, a.Name))) valid.Add(a);
            return new AircraftCatalog(valid)
            {
                LoadWarning = valid.Count < items.Count ? Loc.F("AircraftCatalog_fiche_s_d_appareil_invalide", items.Count - valid.Count) : null,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AircraftCatalog(Defaults()) { LoadWarning = Loc.Get("AircraftCatalog_Liste_d_appareils_illisible_appareils") + ex.Message };
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(_items, Json));
    }

    // ---------- appareils par défaut ----------

    public static readonly Guid Cessna172Id = new("a1720000-0000-4000-8000-000000000172");
    public static readonly Guid Dr400Id = new("d4000000-0000-4000-8000-000000000400");
    public static readonly Guid A320NeoId = new("a3200000-0000-4000-8000-00000000320e");

    /// <summary>
    /// Valeurs relevées sur le web, pour la planification d'un vol simulé ; elles ne remplacent pas le manuel de vol.
    /// 1 gal US = 3,785 L ; kérosène à 0,8 kg/L.
    /// </summary>
    public static IReadOnlyList<Aircraft> Defaults() =>
    [
        new()
        {
            Id = Cessna172Id,
            Name = "Cessna 172 Skyhawk (172S)",
            Category = AircraftCategory.Piston,
            Vfr = true, Ifr = true,
            CruiseKt = 124, MaxKt = 163, BurnLph = 37.5, FuelCapacityL = 200,
            ServiceCeilingFt = 14000, CruiseAltFt = 4500,
            Notes = Loc.Get("AircraftCatalog_Lycoming_IO_360_L2A_180") +
                    Loc.Get("AircraftCatalog_Melange_pauvre_8_a_9"),
        },
        new()
        {
            Id = Dr400Id,
            Name = "Robin DR400-140B",
            Category = AircraftCategory.Piston,
            Vfr = true, Ifr = false,
            CruiseKt = 116, MaxKt = 130, BurnLph = 30, FuelCapacityL = 103,
            ServiceCeilingFt = null, CruiseAltFt = 3500,
            Notes = Loc.Get("AircraftCatalog_Lycoming_O_320_D2A_160") +
                    Loc.Get("AircraftCatalog_Consommation_30_L_h_d") +
                    Loc.Get("AircraftCatalog_Reservoir_principal_110_L_dont"),
        },
        new()
        {
            Id = A320NeoId,
            Name = "Airbus A320neo",
            Category = AircraftCategory.Jet,
            Vfr = false, Ifr = true,
            CruiseKt = 447, MaxKt = 473, BurnLph = 2875, FuelCapacityL = 23740,
            ServiceCeilingFt = 39800, CruiseAltFt = 35000,
            Notes = Loc.Get("AircraftCatalog_Croisiere_Mach_0_78_447") +
                    Loc.Get("AircraftCatalog_Consommation_2_200_a_2"),
        },
    ];
}
