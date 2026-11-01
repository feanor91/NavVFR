using System.Xml.Linq;
using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class NavTests
{
    private static readonly Waypoint Velizy = new() { Name = "LFPV", Icao = "LFPV", FullName = "Velizy", ElevationFt = 586, Lat = 48.7742, Lon = 2.2019 };
    private static readonly Waypoint Turn = new() { Name = "WP1", Lat = 48.9, Lon = 2.6 };
    private static readonly Waypoint Orly = new() { Name = "LFPO", Icao = "LFPO", FullName = "Orly", ElevationFt = 291, Lat = 48.7233, Lon = 2.3794 };

    [Fact]
    public void Distance_Paris_Marseille_is_about_660_km()
    {
        var paris = new Waypoint { Lat = 48.8566, Lon = 2.3522 };
        var marseille = new Waypoint { Lat = 43.2965, Lon = 5.3698 };
        Assert.InRange(NavMath.DistanceNm(paris, marseille) * 1.852, 655, 665);
    }

    [Fact]
    public void TrueCourse_due_east_is_90()
    {
        var a = new Waypoint { Lat = 0, Lon = 0 };
        var b = new Waypoint { Lat = 0, Lon = 1 };
        Assert.Equal(90, NavMath.TrueCourse(a, b), 3);
    }

    [Fact]
    public void WindTriangle_no_wind_keeps_course_and_speed()
    {
        var (wca, th, gs) = NavMath.WindTriangle(120, 100, 270, 0);
        Assert.Equal(0, wca, 6);
        Assert.Equal(120, th, 6);
        Assert.Equal(100, gs, 6);
    }

    [Fact]
    public void WindTriangle_direct_headwind_reduces_ground_speed()
    {
        var (_, _, gs) = NavMath.WindTriangle(90, 100, 90, 20);
        Assert.Equal(80, gs, 6);
    }

    [Fact]
    public void WindTriangle_crosswind_from_right_gives_positive_correction()
    {
        var (wca, _, _) = NavMath.WindTriangle(0, 100, 90, 20);
        Assert.True(wca > 0);
    }

    [Fact]
    public void ComputeLog_sums_legs()
    {
        var log = NavMath.ComputeLog([Velizy, Turn, Orly], new FlightParameters(110, 28, 2500, 1, 270, 10));
        Assert.Equal(2, log.Legs.Count);
        Assert.Equal(log.Legs.Sum(l => l.DistanceNm), log.TotalDistanceNm, 6);
        Assert.Equal(log.Legs.Sum(l => l.FuelL), log.TotalFuelL, 6);
        Assert.InRange(log.TotalDistanceNm, 30, 33);
    }

    [Fact]
    public void Pln_contains_airports_and_user_waypoints()
    {
        var pln = PlnWriter.Build([Velizy, Turn, Orly], 2500);
        var doc = XDocument.Parse(pln);
        var wps = doc.Descendants("ATCWaypoint").ToList();
        Assert.Equal(3, wps.Count);
        Assert.Equal("Airport", wps[0].Element("ATCWaypointType")!.Value);
        Assert.Equal("User", wps[1].Element("ATCWaypointType")!.Value);
        Assert.Equal("LFPO", wps[2].Element("ICAO")!.Element("ICAOIdent")!.Value);
        Assert.Equal("LFPV", doc.Descendants("DepartureID").Single().Value);
    }

    [Fact]
    public void Pln_position_format_matches_msfs()
    {
        Assert.Equal("N48° 46' 27.12\",E002° 12' 06.84\",+000586.00", PlnWriter.Lla(Velizy, 586));
        Assert.StartsWith("S", PlnWriter.Lla(new Waypoint { Lat = -10, Lon = -20 }, 0));
    }

    [Fact]
    public void OurAirports_parser_filters_country_and_handles_quotes()
    {
        const string csv = "id,ident,type,name,latitude_deg,longitude_deg,elevation_ft,iso_country\n" +
                           "1,LFPO,large_airport,\"Paris, Orly\",48.7233,2.3794,291,FR\n" +
                           "2,EGLL,large_airport,Heathrow,51.47,-0.45,83,GB\n" +
                           "3,LFXX,heliport,Heli,48,2,100,FR\n";
        var list = OurAirportsService.Parse(new StringReader(csv), "FR");
        var apt = Assert.Single(list);
        Assert.Equal("Paris, Orly", apt.Name);
        Assert.Equal(291, apt.ElevationFt);
    }

    [Fact]
    public void RouteFile_roundtrip()
    {
        var back = RouteFile.Deserialize(RouteFile.Serialize([Velizy, Turn]));
        Assert.Equal(2, back.Count);
        Assert.Equal("LFPV", back[0].Icao);
        Assert.False(back[1].IsAirport);
    }
}
