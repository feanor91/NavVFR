using System.Net;
using VFRNav.Core.Models;
using VFRNav.Core.Services;
using VFRNav.Core.Weather;

namespace VFRNav.Tests;

public class GuideAndWeatherBoxTests
{
    private static Airspace Zone(int type, string cls, string lower, string upper, double lowerFt, double upperFt, AirspaceDetails? details = null) =>
        new("TMA PARIS 2", cls, lower, upper, [], AirspaceKind.Tma, lowerFt, upperFt, type, details);

    [Theory]
    [InlineData("SFC", "depuis le sol")]
    [InlineData("FL65", "niveau de vol 65")]
    [InlineData("1500ft AMSL", "d'altitude")]
    [InlineData("500ft GND", "au-dessus du sol")]
    public void ExplainLimit_translates_common_limits(string limit, string expectedPart) =>
        Assert.Contains(expectedPart, AirspaceGuide.ExplainLimit(limit));

    [Fact]
    public void ExplainLimit_returns_empty_for_unknown_text() =>
        Assert.Equal("", AirspaceGuide.ExplainLimit("bizarre"));

    [Fact]
    public void Explain_covers_type_class_limits_and_practice()
    {
        var sections = AirspaceGuide.Explain(Zone(7, "D", "1500ft AMSL", "FL65", 1500, 6500));
        var text = string.Join("\n", sections.Select(s => s.Heading + " " + s.Text));
        Assert.Contains("TMA", text);
        Assert.Contains("Classe D", text);
        Assert.Contains("Plancher : 1500ft AMSL", text);
        Assert.Contains("niveau de vol 65", text);
        Assert.Contains("En pratique en vol à vue", text);
    }

    [Theory]
    [InlineData(800, "sous le plancher")]
    [InlineData(3000, "dans la zone")]
    [InlineData(9000, "au-dessus du plafond")]
    public void Explain_places_the_flight_altitude_against_the_zone(double altitude, string expected)
    {
        var sections = AirspaceGuide.Explain(Zone(7, "D", "1500ft AMSL", "FL65", 1500, 6500), altitude);
        Assert.Contains(expected, sections.Single(s => s.Heading.StartsWith("À ton altitude", StringComparison.Ordinal)).Text);
    }

    [Fact]
    public void Explain_includes_published_remarks_conditions_and_frequencies()
    {
        var details = new AirspaceDetails("Active en semaine", ["Sur demande"], [new AirspaceFrequency("Paris Info", "124.200")]);
        var text = string.Join("\n", AirspaceGuide.Explain(Zone(1, "", "SFC", "2000ft AMSL", 0, 2000, details)).Select(s => s.Heading + " " + s.Text));
        Assert.Contains("Active en semaine", text);
        Assert.Contains("Sur demande", text);
        Assert.Contains("Paris Info 124.200", text);
    }

    [Fact]
    public void Prohibited_zone_says_do_not_enter()
    {
        var practice = AirspaceGuide.Explain(Zone(3, "", "SFC", "FL50", 0, 5000)).Single(s => s.Heading.StartsWith("En pratique", StringComparison.Ordinal));
        Assert.Contains("N'entre pas", practice.Text);
    }

    [Fact]
    public void Every_known_openaip_type_has_an_explanation()
    {
        for (int type = 1; type <= 36; type++)
        {
            var text = AirspaceGuide.Explain(Zone(type, "", "SFC", "FL50", 0, 5000))[0].Text;
            Assert.DoesNotContain("n'est pas précisé", text);
        }
    }

    [Theory]
    [InlineData(10000, "> 10 km")]
    [InlineData(4500, "4,5 km")]
    [InlineData(800, "800 m")]
    public void ShortVisibility(double meters, string expected) => Assert.Equal(expected, WeatherDecoder.ShortVisibility(meters));

    [Fact]
    public void Decoded_metar_exposes_visibility_and_ceiling()
    {
        var r = WeatherDecoder.DecodeMetar("LFPO 311030Z 27010KT 4500 BKN012 10/08 Q1013");
        Assert.Equal(4500, r.VisibilityM);
        Assert.Equal(1200, r.CeilingFt);
        var clear = WeatherDecoder.DecodeMetar("LFPO 311030Z 27010KT CAVOK 10/08 Q1013");
        Assert.Equal(10000, clear.VisibilityM);
        Assert.Null(clear.CeilingFt);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("METAR LFPO 311030Z CAVOK\n") });
        }
    }

    [Fact]
    public async Task Box_request_rounds_the_region_and_is_cached()
    {
        var handler = new CountingHandler();
        var service = new WeatherService(new HttpClient(handler));

        await service.GetMetarsInBoxAsync(48.2, 1.8, 49.1, 3.1);
        Assert.Contains("bbox=48,1,50,4", handler.LastUrl);
        await service.GetMetarsInBoxAsync(48.3, 1.9, 49.05, 3.05); // même région arrondie
        Assert.Equal(1, handler.Calls);
        await service.GetMetarsInBoxAsync(44.0, 1.0, 45.0, 2.0);
        Assert.Equal(2, handler.Calls);
    }
}
