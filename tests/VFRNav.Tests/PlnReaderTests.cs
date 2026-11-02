using VFRNav.Core.Models;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class PlnReaderTests
{
    private static List<Waypoint> Route() =>
    [
        new() { Name = "LFPL", Icao = "LFPL", FullName = "Lognes-Émerainville", Lat = 48.8219, Lon = 2.6228, ElevationFt = 351, Notes = "Rejoindre par le point E — écouter 120,600" },
        new() { Name = "WP1", Lat = 48.7738, Lon = 2.612, AltitudeFt = 1400 },
        new() { Name = "WP2", Lat = 48.6936, Lon = 2.5948 },
        new() { Name = "LFPN", Icao = "LFPN", Lat = 48.7497, Lon = -2.1111, ElevationFt = 538 }
    ];

    [Fact]
    public void A_written_plan_is_read_back_identically_including_notes_and_altitudes()
    {
        var plan = PlnReader.Parse(PlnWriter.Build(Route(), 2500));
        Assert.Equal(2500, plan.CruiseAltFt);
        Assert.Equal(4, plan.Route.Count);

        var a = plan.Route[0];
        Assert.Equal("LFPL", a.Icao);
        Assert.Equal(48.8219, a.Lat, 4);
        Assert.Equal(2.6228, a.Lon, 4);
        Assert.Equal(351, a.ElevationFt, 1);
        Assert.Equal("Rejoindre par le point E — écouter 120,600", a.Notes);
        Assert.Equal("Lognes-Émerainville", a.FullName);

        Assert.Equal("WP1", plan.Route[1].Name);
        Assert.Null(plan.Route[1].Icao);
        Assert.Equal(1400, plan.Route[1].AltitudeFt);
        Assert.Null(plan.Route[2].AltitudeFt);           // à la croisière : pas d'altitude propre
        Assert.Equal(-2.1111, plan.Route[3].Lon, 4);      // longitude ouest
    }

    [Fact]
    public void An_airport_keeps_its_elevation_in_the_file_even_with_an_entered_altitude()
    {
        var route = Route();
        route[0].AltitudeFt = 1500;
        string pln = PlnWriter.Build(route, 2500);
        Assert.Contains("+000351.00", pln);                // altitude du terrain, pas celle saisie
        Assert.Equal(1500, PlnReader.Parse(pln).Route[0].AltitudeFt); // mais la saisie est conservée
    }

    [Fact]
    public void The_file_stays_valid_xml_whatever_the_notes_contain()
    {
        var route = Route();
        route[1].Notes = "tiret -- double <balise> & \"guillemets\" --> fin";
        var back = PlnReader.Parse(PlnWriter.Build(route, 2500)).Route[1];
        Assert.Equal("tiret -- double <balise> & \"guillemets\" --> fin", back.Notes);
    }

    [Fact]
    public void A_plan_from_another_tool_opens_without_the_vfrnav_extras()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <SimBase.Document Type="AceXML" version="1,0">
              <Descr>AceXML Document</Descr>
              <FlightPlan.FlightPlan>
                <Title>LFPN to LFFL</Title>
                <FPType>VFR</FPType>
                <CruisingAlt>3500.000</CruisingAlt>
                <ATCWaypoint id="LFPN">
                  <ATCWaypointType>Airport</ATCWaypointType>
                  <WorldPosition>N48° 45' 0.00",E2° 6' 40.00",+000538.00</WorldPosition>
                  <ICAO><ICAOIdent>LFPN</ICAOIdent></ICAO>
                </ATCWaypoint>
                <ATCWaypoint id="TIMMY">
                  <ATCWaypointType>Intersection</ATCWaypointType>
                  <WorldPosition>N48° 50' 30.50",E2° 20' 15.25",+003500.00</WorldPosition>
                </ATCWaypoint>
                <ATCWaypoint id="LFFL">
                  <ATCWaypointType>Airport</ATCWaypointType>
                  <WorldPosition>S33° 0' 0.00",W70° 30' 0.00",+000100.00</WorldPosition>
                </ATCWaypoint>
              </FlightPlan.FlightPlan>
            </SimBase.Document>
            """;
        var plan = PlnReader.Parse(xml);
        Assert.Equal(3500, plan.CruiseAltFt);
        Assert.Equal("LFPN to LFFL", plan.Title);
        Assert.Equal(3, plan.Route.Count);
        Assert.Equal(48.75, plan.Route[0].Lat, 4);
        Assert.Equal(2.1111, plan.Route[0].Lon, 3);
        Assert.Equal("TIMMY", plan.Route[1].Name);
        Assert.Equal(48.84181, plan.Route[1].Lat, 4);
        Assert.Null(plan.Route[1].AltitudeFt);             // à la croisière du plan
        Assert.Equal("LFFL", plan.Route[2].Icao);          // aéroport sans ICAOIdent : l'identifiant du point
        Assert.Equal(-33, plan.Route[2].Lat, 4);
        Assert.Equal(-70.5, plan.Route[2].Lon, 4);
    }

    [Fact]
    public void A_waypoint_away_from_the_cruise_altitude_of_a_foreign_plan_gets_its_own_altitude()
    {
        const string xml = """
            <SimBase.Document><FlightPlan.FlightPlan><CruisingAlt>3000</CruisingAlt>
              <ATCWaypoint id="A"><WorldPosition>N48° 0' 0",E2° 0' 0",+003000.00</WorldPosition></ATCWaypoint>
              <ATCWaypoint id="B"><WorldPosition>N48° 10' 0",E2° 0' 0",+001500.00</WorldPosition></ATCWaypoint>
            </FlightPlan.FlightPlan></SimBase.Document>
            """;
        var route = PlnReader.Parse(xml).Route;
        Assert.Null(route[0].AltitudeFt);
        Assert.Equal(1500, route[1].AltitudeFt);
    }

    [Theory]
    [InlineData("pas du xml du tout")]
    [InlineData("<SimBase.Document/>")]
    [InlineData("<SimBase.Document><FlightPlan.FlightPlan><CruisingAlt>2500</CruisingAlt></FlightPlan.FlightPlan></SimBase.Document>")]
    [InlineData("<SimBase.Document><FlightPlan.FlightPlan><ATCWaypoint id=\"A\"><WorldPosition>illisible</WorldPosition></ATCWaypoint></FlightPlan.FlightPlan></SimBase.Document>")]
    public void An_unusable_file_is_refused_with_a_format_error(string xml)
    {
        Assert.Throws<FormatException>(() => PlnReader.Parse(xml));
    }

    [Fact]
    public void A_damaged_extras_comment_does_not_prevent_opening_the_plan()
    {
        string pln = PlnWriter.Build(Route(), 2500);
        int i = pln.IndexOf("<!-- VFRNav:", StringComparison.Ordinal);
        Assert.True(i > 0);
        string damaged = pln.Insert(i + 12, "###");
        var plan = PlnReader.Parse(damaged);
        Assert.Equal(4, plan.Route.Count);
        Assert.Null(plan.Route[0].Notes);
    }
}
