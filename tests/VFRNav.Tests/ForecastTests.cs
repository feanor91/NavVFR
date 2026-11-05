using System.Text.Json;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Weather;

namespace VFRNav.Tests;

public class ForecastTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 9, 15, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-11-06 14:30", 2026, 11, 6, 14, 30)]
    [InlineData("06/11/2026 14:30", 2026, 11, 6, 14, 30)]
    [InlineData("06/11 1430", 2026, 11, 6, 14, 30)]
    [InlineData("14:30", 2026, 11, 5, 14, 30)]
    [InlineData("1430Z", 2026, 11, 5, 14, 30)]
    [InlineData(" 2026-11-06T08:05 UTC ", 2026, 11, 6, 8, 5)]
    public void Departure_time_is_read_in_utc_in_several_formats(string text, int y, int mo, int d, int h, int mi)
    {
        Assert.True(DepartureTime.TryParse(text, Now, out var t));
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero), t);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("demain matin")]
    [InlineData("25:99")]
    public void An_empty_or_unreadable_departure_time_is_refused(string text) => Assert.False(DepartureTime.TryParse(text, Now, out _));

    [Fact]
    public void Etas_add_up_the_leg_times_from_the_departure()
    {
        var log = new NavLog([
            new LegResult("A", "B", 10, 0, 0, 0, 0, 100, 6, 1),
            new LegResult("B", "C", 20, 0, 0, 0, 0, 100, 12, 2)], 30, 18, 3);
        var etas = DepartureTime.Etas(Now, log);
        Assert.Equal([Now, Now.AddMinutes(6), Now.AddMinutes(18)], etas);
        Assert.Equal("09:15", DepartureTime.ClockText(Now));
        Assert.Equal("2026-11-05 09:15", DepartureTime.Format(Now));
    }

    private static PointForecast Forecast(double surfaceDir = 270, double surfaceKt = 10, double temp = 5, double precip = 0, int low = 0, int mid = 0,
        double? vis = 20000, double? freezing = 8000, double? cape = 0, int code = 0, double gust = 12, params ForecastLevel[] levels) =>
        new(Now, 100, surfaceDir, surfaceKt, gust, temp, precip, low, mid, 0, vis, freezing, cape, code, levels);

    [Fact]
    public void Wind_is_interpolated_between_pressure_levels_by_components()
    {
        var f = Forecast(levels: [new(850, 5000, 270, 20, 0), new(700, 10000, 270, 40, -10)]);
        var mid = f.At(7500);
        Assert.Equal(270, mid.WindDir, 1);
        Assert.Equal(30, mid.WindKt, 1);
        Assert.Equal(-5, mid.TempC, 1);

        Assert.Equal(40, f.At(30000).WindKt, 1);                  // au-dessus du plus haut niveau : celui-ci
        Assert.Equal(10, f.At(0).WindKt, 1);                      // sous la surface : vent de surface

        // Un vent de 350° et un de 010° de même force donnent 360°, pas 180° (interpolation par composantes).
        var turning = Forecast(levels: [new(850, 5000, 350, 20, 0), new(700, 10000, 10, 20, 0)]).At(7500);
        Assert.True(turning.WindDir is > 359 or < 1, turning.WindDir.ToString());
    }

    [Fact]
    public void Advisories_flag_thunderstorms_icing_precipitation_visibility_fog_and_gusts()
    {
        Assert.Empty(ForecastAssessment.Advisories(Forecast(), 5000));
        Assert.Contains(ForecastAssessment.Advisories(Forecast(cape: 1500), 5000), a => a.Kind == ForecastAdvisoryKind.Thunderstorm);
        Assert.Contains(ForecastAssessment.Advisories(Forecast(code: 96), 5000), a => a.Kind == ForecastAdvisoryKind.Thunderstorm);
        Assert.Contains(ForecastAssessment.Advisories(Forecast(precip: 4), 5000), a => a.Kind == ForecastAdvisoryKind.HeavyPrecipitation);
        Assert.Contains(ForecastAssessment.Advisories(Forecast(vis: 3000), 5000), a => a.Kind == ForecastAdvisoryKind.LowVisibility);
        Assert.Contains(ForecastAssessment.Advisories(Forecast(code: 45), 5000), a => a.Kind == ForecastAdvisoryKind.Fog);
        Assert.Contains(ForecastAssessment.Advisories(Forecast(gust: 40), 5000), a => a.Kind == ForecastAdvisoryKind.StrongWind);

        var icy = Forecast(temp: -4, mid: 80, levels: [new(700, 10000, 270, 20, -6)]);
        Assert.Contains(ForecastAssessment.Advisories(icy, 9000), a => a.Kind == ForecastAdvisoryKind.Icing);
        Assert.DoesNotContain(ForecastAssessment.Advisories(Forecast(temp: -4, mid: 0, precip: 0, low: 0, levels: [new(700, 10000, 270, 20, -6)]), 9000), a => a.Kind == ForecastAdvisoryKind.Icing); // pas de nuage
        Assert.DoesNotContain(ForecastAssessment.Advisories(Forecast(temp: 20, mid: 80, freezing: 15000, levels: [new(700, 10000, 270, 20, 12)]), 3000), a => a.Kind == ForecastAdvisoryKind.Icing); // trop chaud
    }

    private static string Json(int count, long firstHour)
    {
        var entries = Enumerable.Range(0, count).Select(i => new Dictionary<string, object>
        {
            ["elevation"] = 100.0 + i,
            ["hourly"] = new Dictionary<string, object>
            {
                ["time"] = new[] { firstHour, firstHour + 3600, firstHour + 7200 },
                ["temperature_2m"] = new[] { 4.0, 5.0, 6.0 },
                ["precipitation"] = new[] { 0.0, 1.5, 0.0 },
                ["weather_code"] = new[] { 0, 61, 3 },
                ["cloud_cover_low"] = new[] { 0, 90, 10 },
                ["cloud_cover_mid"] = new[] { 0, 20, 10 },
                ["cloud_cover_high"] = new[] { 0, 0, 10 },
                ["visibility"] = new[] { 24000.0, 6000.0, 24000.0 },
                ["wind_speed_10m"] = new[] { 8.0, 12.0, 9.0 },
                ["wind_direction_10m"] = new[] { 240.0, 250.0, 260.0 },
                ["wind_gusts_10m"] = new[] { 12.0, 20.0, 14.0 },
                ["freezing_level_height"] = new[] { 2000.0, 1800.0, 2200.0 },
                ["cape"] = new[] { 0.0, 100.0, 0.0 },
                ["wind_speed_850hPa"] = new[] { 20.0, 25.0, 22.0 },
                ["wind_direction_850hPa"] = new[] { 270.0, 270.0, 270.0 },
                ["temperature_850hPa"] = new[] { 2.0, 1.0, 0.0 },
                ["geopotential_height_850hPa"] = new[] { 1500.0, 1500.0, 1500.0 },
            },
        });
        return JsonSerializer.Serialize(count == 1 ? (object)entries.First() : entries.ToList());
    }

    [Fact]
    public void Open_meteo_response_is_read_at_the_hour_nearest_to_each_eta()
    {
        long first = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var requests = new List<ForecastRequest>
        {
            new(48.0, 2.0, new DateTimeOffset(2026, 11, 5, 9, 10, 0, TimeSpan.Zero)),
            new(47.0, 3.0, new DateTimeOffset(2026, 11, 5, 10, 40, 0, TimeSpan.Zero)),
        };

        var result = OpenMeteoForecast.Parse(Json(2, first), requests);

        Assert.Equal(2, result.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero), result[0]!.Time);
        Assert.Equal(4, result[0]!.SurfaceTempC);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 11, 0, 0, TimeSpan.Zero), result[1]!.Time);   // 10 h 40 : l'heure pleine la plus proche est 11 h
        Assert.Equal(6, result[1]!.SurfaceTempC);
        Assert.Equal(101 * 3.28084, result[1]!.ElevationFt, 1);
        Assert.Single(result[0]!.Levels);
        Assert.Equal(1500 * 3.28084, result[0]!.Levels[0].HeightFt, 1);
        Assert.Equal(2000 * 3.28084, result[0]!.FreezingLevelFt!.Value, 1);
    }

    [Fact]
    public void A_single_point_response_is_an_object_and_an_eta_outside_the_response_gives_no_forecast()
    {
        long first = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var inside = OpenMeteoForecast.Parse(Json(1, first), [new(48, 2, new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero))]);
        var outside = OpenMeteoForecast.Parse(Json(1, first), [new(48, 2, new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero))]);
        Assert.NotNull(inside[0]);
        Assert.Null(outside[0]);
    }

    [Fact]
    public void Wrong_point_counts_and_service_errors_raise_a_clear_error()
    {
        long first = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Throws<InvalidDataException>(() => OpenMeteoForecast.Parse(Json(2, first), [new(48, 2, Now)]));
        var ex = Assert.Throws<InvalidDataException>(() => OpenMeteoForecast.Parse("{\"error\":true,\"reason\":\"Date out of range\"}", [new(48, 2, Now)]));
        Assert.Contains("Date out of range", ex.Message);
        Assert.Throws<InvalidDataException>(() => OpenMeteoForecast.Parse("pas du json", [new(48, 2, Now)]));
    }

    [Fact]
    public void The_url_lists_the_points_the_levels_and_the_utc_dates()
    {
        string url = OpenMeteoForecast.Url([new(48.5, 2.25, Now), new(43.6, 7.2, Now.AddDays(1))]);
        Assert.Contains("latitude=48.5,43.6", url);
        Assert.Contains("longitude=2.25,7.2", url);
        Assert.Contains("wind_speed_850hPa", url);
        Assert.Contains("geopotential_height_200hPa", url);
        Assert.Contains("start_date=2026-11-05&end_date=2026-11-06", url);
        Assert.Contains("wind_speed_unit=kn", url);
    }

    [Fact]
    public void Leg_winds_average_the_two_ends_and_feed_the_navigation_log()
    {
        var route = new List<Waypoint>
        {
            new() { Name = "A", Lat = 48, Lon = 2 }, new() { Name = "B", Lat = 48, Lon = 3 }, new() { Name = "C", Lat = 48, Lon = 4 },
        };
        var altitudes = new AltitudeProfile(route, 5000);
        var tailwind = Forecast(surfaceDir: 270, surfaceKt: 40, levels: [new(850, 5000, 270, 40, 0), new(700, 10000, 270, 40, -5)]);  // vent d'ouest : portant vers l'est
        var winds = ForecastTable.LegWinds(altitudes, [tailwind, tailwind, null]);

        Assert.Equal(2, winds.Count);
        Assert.Equal(270, winds[0]!.Value.Dir, 1);
        Assert.Equal(40, winds[0]!.Value.Kt, 1);
        Assert.Null(winds[1]);                                   // pas de prévision au point C : vent supposé

        var p = new FlightParameters(120, 30, 5000, 0, 0, 0);
        var calm = NavMath.ComputeLog(route, p);
        var windy = NavMath.ComputeLog(route, p, winds);
        Assert.True(windy.Legs[0].GroundSpeedKt > 150);          // 120 + 40 de vent arrière sur un cap est
        Assert.Equal(calm.Legs[1].GroundSpeedKt, windy.Legs[1].GroundSpeedKt, 3); // tronçon sans prévision : vent supposé
        Assert.True(windy.Legs[0].Minutes < calm.Legs[0].Minutes);
    }

    [Fact]
    public void Rows_show_the_wind_at_flight_altitude_and_the_advisories_or_a_missing_forecast()
    {
        var route = new List<Waypoint> { new() { Name = "A", Lat = 48, Lon = 2 }, new() { Name = "B", Lat = 48, Lon = 3 } };
        var altitudes = new AltitudeProfile(route, 5000);
        var stormy = Forecast(cape: 2000, levels: [new(850, 5000, 270, 25, 0)]);
        var rows = ForecastTable.Rows(route, [Now, Now.AddMinutes(30)], altitudes, [stormy, null]);

        Assert.Equal("09:15", rows[0].Eta);
        Assert.Contains("270°/25 kt", rows[0].Wind);
        Assert.True(rows[0].HasAdvisory);
        Assert.False(rows[1].HasAdvisory);
        Assert.Equal("09:45", rows[1].Eta);
    }
}
