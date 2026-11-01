using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class NavaidTests
{
    private const string Csv =
        "\"id\",\"filename\",\"ident\",\"name\",\"type\",\"frequency_khz\",\"latitude_deg\",\"longitude_deg\",\"elevation_ft\",\"iso_country\"\n" +
        "1,\"a\",\"CGN\",\"Coligny\",\"VOR-DME\",113500,46.4,5.3,800,\"FR\"\n" +
        "2,\"b\",\"MH\",\"Melun\",\"NDB\",373,48.5,2.6,300,\"FR\"\n" +
        "3,\"c\",\"XYZ\",\"Seul DME\",\"DME\",110000,48.0,2.0,100,\"FR\"\n" +
        "4,\"d\",\"LON\",\"London\",\"VOR\",113600,51.5,-0.1,100,\"GB\"\n";

    [Fact]
    public void Parses_vor_and_ndb_of_the_country_only()
    {
        var list = OurAirportsService.ParseNavaids(new StringReader(Csv), "FR");
        Assert.Equal(["CGN", "MH"], list.Select(n => n.Ident).ToArray());
        Assert.Equal(NavaidKind.Vor, list[0].Kind);
        Assert.Equal(NavaidKind.Ndb, list[1].Kind);
    }

    [Fact]
    public void Frequencies_are_shown_in_the_right_unit()
    {
        var list = OurAirportsService.ParseNavaids(new StringReader(Csv), "FR");
        Assert.Equal("113.500 MHz", list[0].FrequencyText);
        Assert.Equal("373 kHz", list[1].FrequencyText);
    }

    [Fact]
    public void Navaid_becomes_a_named_route_point()
    {
        var wp = OurAirportsService.ParseNavaids(new StringReader(Csv), "FR")[0].ToWaypoint();
        Assert.Equal("CGN", wp.Name);
        Assert.False(wp.IsAirport);
        Assert.Contains("VOR-DME", wp.FullName);
    }
}
