using VFRNav.Core.Models;

namespace VFRNav.Tests;

public class AirportFilterTests
{
    private static Airport Apt(string ident, string type = "small_airport", bool vac = false) =>
        new(ident, ident, type, 48, 2, 100, vac);

    [Fact]
    public void Default_filter_shows_everything()
    {
        var f = new AirportFilter();
        Assert.All([Apt("LFPL"), Apt("FR-0123"), Apt("LFPG", "large_airport")], a => Assert.True(f.Matches(a)));
    }

    [Fact]
    public void Can_hide_airports_without_code()
    {
        var f = new AirportFilter(WithoutCode: false);
        Assert.False(f.Matches(Apt("FR-0123")));
        Assert.True(f.Matches(Apt("LFPL")));
        Assert.True(f.Matches(Apt("LF3126")));
    }

    [Fact]
    public void Can_show_only_airports_without_code()
    {
        var f = new AirportFilter(WithCode: false);
        Assert.True(f.Matches(Apt("FR-0123")));
        Assert.False(f.Matches(Apt("LFPL")));
    }

    [Fact]
    public void Vac_only_and_size_filters_combine()
    {
        var f = new AirportFilter(VacOnly: true, Small: false);
        Assert.False(f.Matches(Apt("LFPL", vac: true)));
        Assert.True(f.Matches(Apt("LFPG", "large_airport", vac: true)));
        Assert.False(f.Matches(Apt("LFPO", "large_airport", vac: false)));
    }

    [Fact]
    public void Settings_roundtrip_keeps_filter()
    {
        string path = Path.Combine(Path.GetTempPath(), "vfrnav-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new VFRNav.Core.Services.AppSettings { Filter = new AirportFilter(WithoutCode: false, VacOnly: true) }.Save(path);
            var back = VFRNav.Core.Services.AppSettings.Load(path);
            Assert.False(back.Filter.WithoutCode);
            Assert.True(back.Filter.VacOnly);
            Assert.True(back.Filter.WithCode);
        }
        finally { File.Delete(path); }
    }
}
