using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class LiveFeedTests
{
    private static Airspace Zone(string name, double lon, double lat, double size = 0.5) =>
        new(name, "D", "SFC", "FL65", [[(lon, lat), (lon + size, lat), (lon + size, lat + size), (lon, lat + size)]],
            AirspaceKind.Ctr, 0, 6500, 4);

    [Fact]
    public void Map_keeps_only_the_zones_and_airports_inside_the_rectangle()
    {
        var map = LiveFeed.Map(
            [Zone("IN", 2, 48), Zone("OUT", 10, 40)], true,
            [new Airport("LFPN", "Toussus", "small_airport", 48.75, 2.11, 538), new Airport("LFMN", "Nice", "large_airport", 43.6, 7.2, 12)],
            47, 1, 49, 3);

        Assert.Equal(["IN"], map.Airspaces.Select(z => z.Name));
        Assert.Equal(["LFPN"], map.Airports.Select(a => a.Ident));
        Assert.True(map.AirspacesLoaded);
        Assert.False(map.Truncated);
    }

    [Fact]
    public void Map_rings_are_longitude_then_latitude()
    {
        var zone = LiveFeed.Map([Zone("Z", 2.123456, 48.654321)], true, [], 0, 0, 90, 90).Airspaces[0];
        Assert.Equal([2.1235, 48.6543], zone.Rings[0][0]);
        Assert.Equal("ctr", zone.Kind);
        Assert.Equal("CTR", zone.Type);
    }

    [Fact]
    public void Map_is_cut_when_there_are_too_many_zones()
    {
        var zones = Enumerable.Range(0, LiveFeed.MaxAirspaces + 5).Select(i => Zone("Z" + i, 2, 48));
        var map = LiveFeed.Map(zones, true, [], 0, 0, 90, 90);
        Assert.Equal(LiveFeed.MaxAirspaces, map.Airspaces.Count);
        Assert.True(map.Truncated);
    }

    [Fact]
    public void State_reports_the_age_of_the_aircraft_position_and_hides_empty_notes()
    {
        var now = DateTimeOffset.UtcNow;
        var route = new[]
        {
            new Waypoint { Name = "LFPN", Icao = "LFPN", Lat = 48.75, Lon = 2.11, Notes = "  " },
            new Waypoint { Name = "WP1", Lat = 48, Lon = 2, Notes = " vers l'église " }
        };
        var state = LiveFeed.State(route, new FeedAircraft(48, 2, 1000, 90, 100, false), now.AddSeconds(-2), 2500, now);

        Assert.Null(state.Route[0].Notes);
        Assert.Equal("vers l'église", state.Route[1].Notes);
        Assert.Equal(2000, state.AircraftAgeMs);
        Assert.Equal(-1, LiveFeed.State(route, null, null, 2500, now).AircraftAgeMs);
    }
}
