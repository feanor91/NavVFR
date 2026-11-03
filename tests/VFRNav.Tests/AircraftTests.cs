using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class AircraftTests
{
    private static Aircraft Valid(string name = "Test") => new()
    {
        Name = name, Vfr = true, Ifr = false, CruiseKt = 100, MaxKt = 120, BurnLph = 30, FuelCapacityL = 90, CruiseAltFt = 3000,
    };

    [Fact]
    public void Endurance_and_range_follow_fuel_burn_and_speed()
    {
        var a = Valid();   // 90 L à 30 L/h = 3 h ; à 100 kt = 300 NM
        Assert.Equal(180, a.EnduranceMinutes, 6);
        Assert.Equal(300, a.RangeNm, 6);
    }

    [Fact]
    public void Capabilities_decide_the_flight_modes()
    {
        var a = Valid();
        a.Ifr = true;
        Assert.True(a.Supports(FlightMode.Vfr));
        Assert.True(a.Supports(FlightMode.Ifr));
        a.Vfr = false;
        Assert.False(a.Supports(FlightMode.Vfr));
    }

    [Fact]
    public void Validation_reports_every_problem_in_french()
    {
        var bad = new Aircraft { Name = " ", Vfr = false, Ifr = false, CruiseKt = 0, MaxKt = -1, BurnLph = 0, FuelCapacityL = 0, CruiseAltFt = -5 };
        Assert.True(bad.Validate().Count >= 6);
        Assert.Empty(Valid().Validate());
    }

    [Fact]
    public void Max_speed_cannot_be_below_cruise_and_ceiling_cannot_be_below_cruise_altitude()
    {
        var a = Valid();
        a.MaxKt = 90;
        Assert.Contains(a.Validate(), e => e.Contains("maximale"));
        a.MaxKt = 120;
        a.ServiceCeilingFt = 2000;
        Assert.Contains(a.Validate(), e => e.Contains("plafond", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nan_values_are_rejected()
    {
        var a = Valid();
        a.CruiseKt = double.NaN;
        Assert.NotEmpty(a.Validate());
    }
}

public class AircraftCatalogTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "vfrnav-tests-" + Guid.NewGuid().ToString("N"), "aircraft.json");

    private static Aircraft Make(string name, bool vfr = true, bool ifr = false) => new()
    {
        Name = name, Vfr = vfr, Ifr = ifr, CruiseKt = 100, MaxKt = 120, BurnLph = 30, FuelCapacityL = 90, CruiseAltFt = 3000,
    };

    [Fact]
    public void First_launch_creates_the_three_default_aircraft()
    {
        var catalog = AircraftCatalog.Load(TempFile());

        Assert.Equal(3, catalog.All.Count);
        Assert.All(catalog.All, a => Assert.Empty(a.Validate()));
        Assert.Contains(catalog.All, a => a.Name.Contains("172"));
        Assert.Contains(catalog.All, a => a.Name.Contains("DR400"));
        Assert.Contains(catalog.All, a => a.Name.Contains("A320"));
    }

    [Fact]
    public void Default_aircraft_are_filed_under_the_right_modes()
    {
        var catalog = AircraftCatalog.Load(TempFile());

        var vfr = catalog.ForMode(FlightMode.Vfr).Select(a => a.Id).ToList();
        var ifr = catalog.ForMode(FlightMode.Ifr).Select(a => a.Id).ToList();

        Assert.Contains(AircraftCatalog.Cessna172Id, vfr);   // le 172 sert aux deux
        Assert.Contains(AircraftCatalog.Cessna172Id, ifr);
        Assert.Contains(AircraftCatalog.Dr400Id, vfr);
        Assert.DoesNotContain(AircraftCatalog.Dr400Id, ifr);
        Assert.Contains(AircraftCatalog.A320NeoId, ifr);
        Assert.DoesNotContain(AircraftCatalog.A320NeoId, vfr);
    }

    [Fact]
    public void Default_ranges_are_plausible()
    {
        var c = AircraftCatalog.Load(TempFile());
        Assert.InRange(c.Find(AircraftCatalog.Cessna172Id)!.RangeNm, 600, 750);
        Assert.InRange(c.Find(AircraftCatalog.Dr400Id)!.RangeNm, 330, 450);
        Assert.InRange(c.Find(AircraftCatalog.A320NeoId)!.RangeNm, 3000, 4200);
    }

    [Fact]
    public void Create_read_update_delete()
    {
        var catalog = new AircraftCatalog([]);
        var a = Make("Pilatus PC-12", vfr: true, ifr: true);

        Assert.Empty(catalog.Add(a));
        Assert.Same(catalog.All[0], catalog.All[0]);
        Assert.Equal("Pilatus PC-12", catalog.Find(a.Id)!.Name);

        var edit = catalog.Find(a.Id)!.Clone();
        edit.CruiseKt = 270;
        edit.MaxKt = 290;
        Assert.Empty(catalog.Update(edit));
        Assert.Equal(270, catalog.Find(a.Id)!.CruiseKt);

        Assert.True(catalog.Remove(a.Id));
        Assert.Null(catalog.Find(a.Id));
        Assert.False(catalog.Remove(a.Id));
    }

    [Fact]
    public void Stored_copy_is_independent_from_the_edited_one()
    {
        var catalog = new AircraftCatalog([]);
        var a = Make("A");
        catalog.Add(a);
        a.CruiseKt = 999;   // modification non enregistrée
        Assert.Equal(100, catalog.Find(a.Id)!.CruiseKt);
    }

    [Fact]
    public void Names_must_be_unique_ignoring_case_but_an_aircraft_may_keep_its_own_name()
    {
        var catalog = new AircraftCatalog([]);
        var a = Make("Cirrus SR22");
        catalog.Add(a);

        Assert.NotEmpty(catalog.Add(Make("cirrus sr22 ")));
        Assert.Empty(catalog.Update(a));   // même nom, même fiche : accepté
        var other = Make("Autre");
        catalog.Add(other);
        var clash = other.Clone();
        clash.Name = "CIRRUS SR22";
        Assert.NotEmpty(catalog.Update(clash));
    }

    [Fact]
    public void Invalid_aircraft_are_refused_and_not_stored()
    {
        var catalog = new AircraftCatalog([]);
        var bad = Make("Mauvais");
        bad.BurnLph = 0;
        Assert.NotEmpty(catalog.Add(bad));
        Assert.Empty(catalog.All);
        Assert.NotEmpty(catalog.Update(Make("Inconnu")));   // n'existe pas
    }

    [Fact]
    public void Save_and_load_round_trip_keeps_every_field()
    {
        string path = TempFile();
        var catalog = AircraftCatalog.Load(path);
        var dr = catalog.Find(AircraftCatalog.Dr400Id)!.Clone();
        dr.Ifr = true;
        dr.Notes = "équipé IFR";
        catalog.Update(dr);
        catalog.Save(path);

        var again = AircraftCatalog.Load(path);
        var back = again.Find(AircraftCatalog.Dr400Id)!;
        Assert.True(back.Ifr);
        Assert.Equal("équipé IFR", back.Notes);
        Assert.Null(back.ServiceCeilingFt);
        Assert.Equal(AircraftCategory.Piston, back.Category);
        Assert.Equal(3, again.All.Count);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Deleting_everything_is_remembered_and_defaults_are_not_recreated_on_load()
    {
        string path = TempFile();
        var catalog = AircraftCatalog.Load(path);
        foreach (var a in catalog.All.ToList()) catalog.Remove(a.Id);
        catalog.Save(path);

        Assert.Empty(AircraftCatalog.Load(path).All);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void RestoreDefaults_adds_only_missing_ones_and_keeps_edits()
    {
        var catalog = AircraftCatalog.Load(TempFile());
        var c172 = catalog.Find(AircraftCatalog.Cessna172Id)!.Clone();
        c172.CruiseKt = 110;
        catalog.Update(c172);
        catalog.Remove(AircraftCatalog.A320NeoId);

        Assert.Equal(1, catalog.RestoreDefaults());
        Assert.Equal(3, catalog.All.Count);
        Assert.Equal(110, catalog.Find(AircraftCatalog.Cessna172Id)!.CruiseKt);
        Assert.Equal(0, catalog.RestoreDefaults());
    }

    [Fact]
    public void Corrupt_file_falls_back_to_the_defaults_with_a_warning()
    {
        string path = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ pas du json");

        var catalog = AircraftCatalog.Load(path);

        Assert.Equal(3, catalog.All.Count);
        Assert.NotNull(catalog.LoadWarning);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void Hand_edited_invalid_or_duplicate_entries_are_dropped()
    {
        string path = TempFile();
        var id = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            [
              {"Id":"{{id}}","Name":"Bon","Vfr":true,"Ifr":false,"CruiseKt":100,"MaxKt":120,"BurnLph":30,"FuelCapacityL":90,"CruiseAltFt":3000},
              {"Id":"{{Guid.NewGuid()}}","Name":"BON","Vfr":true,"Ifr":false,"CruiseKt":100,"MaxKt":120,"BurnLph":30,"FuelCapacityL":90,"CruiseAltFt":3000},
              {"Id":"{{Guid.NewGuid()}}","Name":"Sans conso","Vfr":true,"Ifr":false,"CruiseKt":100,"MaxKt":120,"BurnLph":0,"FuelCapacityL":90,"CruiseAltFt":3000}
            ]
            """);

        var catalog = AircraftCatalog.Load(path);

        Assert.Single(catalog.All);
        Assert.Equal(id, catalog.All[0].Id);
        Assert.Contains("2", catalog.LoadWarning);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [Fact]
    public void ForMode_is_sorted_by_name()
    {
        var catalog = new AircraftCatalog([Make("Zlin"), Make("Alpha"), Make("Mooney")]);
        Assert.Equal(["Alpha", "Mooney", "Zlin"], catalog.ForMode(FlightMode.Vfr).Select(a => a.Name));
    }
}

public class RangeCircleTests
{
    [Fact]
    public void Destination_goes_the_requested_distance_on_the_requested_bearing()
    {
        var (lat, lon) = RouteGeometry.Destination(48, 2, 90, 60);   // 60 NM plein est
        Assert.InRange(NavMath.DistanceNm(48, 2, lat, lon), 59.99, 60.01);
        Assert.InRange(NavMath.TrueCourse(new Waypoint { Lat = 48, Lon = 2 }, new Waypoint { Lat = lat, Lon = lon }), 89.5, 90.5);

        var (nLat, nLon) = RouteGeometry.Destination(48, 2, 0, 60);  // 60 NM nord = 1° de latitude
        Assert.Equal(49, nLat, 2);
        Assert.Equal(2, nLon, 6);
    }

    [Fact]
    public void Small_circle_is_one_closed_loop_at_constant_distance()
    {
        var lines = RouteGeometry.CircleLines(48.7742, 2.2019, 100);

        var loop = Assert.Single(lines);
        Assert.Equal(loop[0].Lat, loop[^1].Lat, 9);
        Assert.Equal(loop[0].Lon, loop[^1].Lon, 9);
        Assert.All(loop, p => Assert.InRange(NavMath.DistanceNm(48.7742, 2.2019, p.Lat, p.Lon), 99.99, 100.01));
    }

    [Fact]
    public void Huge_circle_is_split_instead_of_crossing_the_pole_or_the_date_line()
    {
        // Un A320 depuis Paris : 3 700 NM, plus que la distance au pôle.
        var lines = RouteGeometry.CircleLines(49, 2.5, 3700);

        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            Assert.All(line, p => Assert.InRange(p.Lat, -84.0001, 84.0001));
            for (int i = 1; i < line.Count; i++)
                Assert.True(Math.Abs(line[i].Lon - line[i - 1].Lon) <= 180, "un arc ne doit pas traverser l'antiméridien");
        });
    }

    [Fact]
    public void Nothing_is_drawn_without_a_positive_radius()
    {
        Assert.Empty(RouteGeometry.CircleLines(48, 2, 0));
        Assert.Empty(RouteGeometry.CircleLines(48, 2, double.NaN));
        Assert.Empty(RouteGeometry.CircleLines(48, 2, -10));
    }

    [Fact]
    public void Climb_and_descent_gradients_follow_the_rates_and_the_cruise_speed()
    {
        var jet = new Aircraft { Category = AircraftCategory.Jet, CruiseKt = 450 };
        Assert.Equal(2200 / (450 * 0.8 / 60), jet.ClimbFtPerNm, 3);
        Assert.Equal(2000 / (450 * 0.85 / 60), jet.DescentFtPerNm, 3);

        var piston = new Aircraft { Category = AircraftCategory.Piston, CruiseKt = 100, ClimbFpm = 400 };
        Assert.Equal(400 / (100 * 0.8 / 60), piston.ClimbFtPerNm, 3);   // taux saisi
        Assert.Equal(500, piston.DescentRateFpm);                       // usuel de la catégorie
        Assert.Contains(new Aircraft { Name = "X", ClimbFpm = 0 }.Validate(), e => e.Contains("montée"));
    }
}
