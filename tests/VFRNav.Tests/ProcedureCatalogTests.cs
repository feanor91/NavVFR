using VFRNav.Core.Ifr;

namespace VFRNav.Tests;

public sealed class ProcedureCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vfrnav-cat-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T1 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), T2 = T1.AddDays(30);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    private ProcedureCatalog Catalog(string name = "a") => new(Path.Combine(_dir, name));

    private static SimProcedureImport Import(string airport, string ident, int maxAlt = 5000, ProcedureKind kind = ProcedureKind.Sid) =>
        new([new IfrProcedure(kind, airport, ident,
                [new ProcedureVariant(["26L", "26R"], [new ProcedureLeg("ABCDE", 3000, maxAlt, 220), new ProcedureLeg("FGHIJ")])])],
            [new IfrFix("ABCDE", 48.5, 2.25), new IfrFix("FGHIJ", 48.7, 2.5, FixKind.Vor)]);

    [Fact]
    public void A_written_airport_is_read_back_with_every_detail_after_reopening_the_catalog()
    {
        var c = Catalog();
        Assert.Equal(CatalogChange.New, c.Write("lfpg", Import("LFPG", "AGOP6A"), T1));
        c.SaveIndex("MSFS 1.2.3.4", T1);

        var reopened = Catalog();
        Assert.True(reopened.Has("LFPG"));
        Assert.Equal("MSFS 1.2.3.4", reopened.Index!.SimVersion);
        var back = reopened.Read("LFPG")!;
        var p = back.Procedures.Single();
        Assert.Equal((ProcedureKind.Sid, "LFPG", "AGOP6A"), (p.Kind, p.Airport, p.Ident));
        Assert.Equal(["26L", "26R"], p.Variants[0].Runways);
        var leg = p.Variants[0].Legs[0];
        Assert.Equal(("ABCDE", 3000, 5000, 220), (leg.Fix, leg.MinAltFt, leg.MaxAltFt, leg.SpeedKt));
        Assert.Equal(FixKind.Vor, back.Fixes[1].Kind);
        Assert.Equal(1, reopened.Index.Airports["LFPG"].Procedures);
    }

    [Fact]
    public void Each_airport_is_a_compressed_file_so_only_the_needed_ones_are_read()
    {
        var c = Catalog();
        c.Write("LFPG", Import("LFPG", "A1"), T1);
        c.Write("LFMN", Import("LFMN", "B2"), T1);
        var files = Directory.GetFiles(c.Directory, "*.json.gz").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["LFMN.json.gz", "LFPG.json.gz"], files);
        Assert.Null(c.Read("LFBO")); // terrain absent : pas d'erreur
    }

    [Fact]
    public void An_update_tells_new_updated_and_unchanged_airports_and_rewrites_only_what_changed()
    {
        var c = Catalog();
        c.Write("LFPG", Import("LFPG", "A1"), T1);
        c.Write("LFMN", Import("LFMN", "B2"), T1);

        var path = Path.Combine(c.Directory, "LFMN.json.gz");
        var writtenBefore = File.GetLastWriteTimeUtc(path);
        Thread.Sleep(30);

        Assert.Equal(CatalogChange.Unchanged, c.Write("LFMN", Import("LFMN", "B2"), T2));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(path));         // fichier non réécrit
        Assert.Equal(T1, c.Index!.Airports["LFMN"].ReadAt);                    // date de lecture gardée
        Assert.Equal(CatalogChange.Updated, c.Write("LFPG", Import("LFPG", "A1", maxAlt: 6000), T2)); // une contrainte a changé
        Assert.Equal(T2, c.Index.Airports["LFPG"].ReadAt);
        Assert.Equal(CatalogChange.New, c.Write("LFBO", Import("LFBO", "C3"), T2));
    }

    [Fact]
    public void An_airport_without_procedures_any_more_can_be_removed()
    {
        var c = Catalog();
        c.Write("LFPG", Import("LFPG", "A1"), T1);
        Assert.True(c.Remove("LFPG"));
        Assert.False(c.Has("LFPG"));
        Assert.Empty(Directory.GetFiles(c.Directory, "LFPG*"));
        Assert.False(c.Remove("LFPG"));
    }

    [Fact]
    public void The_hash_changes_with_any_procedure_point_or_constraint_and_ignores_order()
    {
        string h = ProcedureCatalog.Hash(Import("LFPG", "A1"));
        Assert.Equal(h, ProcedureCatalog.Hash(Import("LFPG", "A1")));
        Assert.NotEqual(h, ProcedureCatalog.Hash(Import("LFPG", "A2")));
        Assert.NotEqual(h, ProcedureCatalog.Hash(Import("LFPG", "A1", maxAlt: 4000)));
        var a = new SimProcedureImport(Import("X", "P1").Procedures.Concat(Import("X", "P2").Procedures).ToList(), Import("X", "P1").Fixes);
        var b = new SimProcedureImport(Import("X", "P2").Procedures.Concat(Import("X", "P1").Procedures).ToList(), Import("X", "P1").Fixes);
        Assert.Equal(ProcedureCatalog.Hash(a), ProcedureCatalog.Hash(b));
    }

    [Fact]
    public void A_damaged_index_or_airport_file_is_ignored_with_a_reason_instead_of_failing()
    {
        var c = Catalog();
        c.Write("LFPG", Import("LFPG", "A1"), T1);
        c.SaveIndex("MSFS 1", T1);
        File.WriteAllText(Path.Combine(c.Directory, "LFPG.json.gz"), "pas du gzip");
        Assert.Null(Catalog().Read("LFPG"));                                 // fichier du terrain abîmé

        File.WriteAllText(Path.Combine(c.Directory, ProcedureCatalog.IndexName), "{ pas du json");
        var broken = Catalog();
        Assert.Null(broken.Index);
        Assert.False(string.IsNullOrEmpty(broken.Problem));

        File.WriteAllText(Path.Combine(c.Directory, ProcedureCatalog.IndexName), """{"version":99,"source":"x","simVersion":"","readAt":"2026-01-01T00:00:00Z","airports":{}}""");
        Assert.Contains("99", Catalog().Problem);
    }

    [Fact]
    public void An_invalid_airport_code_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Catalog().Write("../x", Import("X", "A1"), T1));
        Assert.Throws<ArgumentException>(() => Catalog().Write("", Import("X", "A1"), T1));
    }

    [Fact]
    public void The_library_reads_the_user_catalog_first_then_the_bundled_one()
    {
        var bundled = Catalog("bundled");
        bundled.Write("LFPG", Import("LFPG", "OLD1A"), T1);
        bundled.Write("LFMN", Import("LFMN", "BASE1A"), T1);
        bundled.SaveIndex("MSFS 1", T1);
        var user = Catalog("user");
        user.Write("LFPG", Import("LFPG", "NEW1A"), T2);
        user.SaveIndex("MSFS 2", T2);

        var library = new ProcedureLibrary(Catalog("bundled"), Catalog("user"));
        Assert.Equal("NEW1A", library.Read("LFPG")!.Procedures.Single().Ident);   // celui de l'utilisateur l'emporte
        Assert.Equal("BASE1A", library.Read("LFMN")!.Procedures.Single().Ident);
        Assert.Null(library.Read("LFBO"));
        Assert.Equal(2, library.AirportCount);                                    // LFPG compté une fois
        Assert.Equal(2, library.ProcedureCount);
        Assert.Equal(("MSFS 2", T2), library.Origin);                              // le plus récent
    }
}
