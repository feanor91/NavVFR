using VFRNav.Core.Models;

namespace VFRNav.Tests;

public class AirspaceTests
{
    private static Airspace Space(AirspaceKind kind, double lowerFt = 0) =>
        new("X", "D", "SFC", "FL65", [], kind, lowerFt, 6500);

    [Theory]
    [InlineData(1, AirspaceKind.Restricted)]
    [InlineData(2, AirspaceKind.Restricted)]
    [InlineData(4, AirspaceKind.Ctr)]
    [InlineData(7, AirspaceKind.Tma)]
    [InlineData(26, AirspaceKind.Tma)]
    [InlineData(6, AirspaceKind.Radio)]
    [InlineData(21, AirspaceKind.Leisure)]
    [InlineData(33, AirspaceKind.Sector)]
    [InlineData(0, AirspaceKind.Other)]
    [InlineData(999, AirspaceKind.Other)]
    public void OpenAip_type_maps_to_kind(int type, AirspaceKind expected) =>
        Assert.Equal(expected, Airspace.KindFromOpenAipType(type));

    [Fact]
    public void Default_filter_hides_sectors_only()
    {
        var f = new AirspaceFilter();
        Assert.True(f.Matches(Space(AirspaceKind.Restricted)));
        Assert.True(f.Matches(Space(AirspaceKind.Ctr)));
        Assert.True(f.Matches(Space(AirspaceKind.Tma)));
        Assert.True(f.Matches(Space(AirspaceKind.Radio)));
        Assert.True(f.Matches(Space(AirspaceKind.Leisure)));
        Assert.False(f.Matches(Space(AirspaceKind.Sector)));
        Assert.False(f.Matches(Space(AirspaceKind.Other)));
    }

    [Fact]
    public void Floor_filter_hides_zones_that_start_too_high()
    {
        var f = new AirspaceFilter(MaxFloorFt: 3000);
        Assert.True(f.Matches(Space(AirspaceKind.Tma, lowerFt: 2500)));
        Assert.False(f.Matches(Space(AirspaceKind.Tma, lowerFt: 4500)));
        Assert.True(new AirspaceFilter(MaxFloorFt: null).Matches(Space(AirspaceKind.Tma, lowerFt: 20000)));
    }

    [Fact]
    public void Label_has_name_and_limits() =>
        Assert.Equal("X   SFC - FL65", Space(AirspaceKind.Tma).Label);

    [Theory]
    [InlineData(65, 6, 2, "FL65", 6500)]
    [InlineData(0, 1, 0, "SFC", 0)]
    [InlineData(2500, 1, 1, "2500ft AMSL", 2500)]
    [InlineData(500, 1, 0, "500ft GND", 500)]
    public void FormatLimit_covers_units(int value, int unit, int datum, string text, double feet)
    {
        var (t, ft) = Airspace.FormatLimit(value, unit, datum);
        Assert.Equal(text, t);
        Assert.Equal(feet, ft, 3);
    }

    private static Airspace Square() => new("TMA TEST", "D", "1500ft AMSL", "FL65",
        [new List<(double, double)> { (2, 48), (3, 48), (3, 49), (2, 49), (2, 48) }],
        AirspaceKind.Tma, 1500, 6500, 7,
        new AirspaceDetails("Contacter Paris Info", ["Sur demande"], [new AirspaceFrequency("Paris Info", "124.200")]));

    [Fact]
    public void Contains_is_true_inside_and_false_outside()
    {
        var a = Square();
        Assert.True(a.Contains(2.5, 48.5));
        Assert.False(a.Contains(3.5, 48.5));
        Assert.False(a.Contains(2.5, 49.5));
    }

    [Fact]
    public void DescribeBody_lists_type_limits_conditions_remarks_and_frequencies()
    {
        var text = string.Join("\n", Square().DescribeBody());
        Assert.Contains("TMA · classe D", text);
        Assert.Contains("Plafond : FL65", text);
        Assert.Contains("Plancher : 1500ft AMSL", text);
        Assert.Contains("Sur demande", text);
        Assert.Contains("Contacter Paris Info", text);
        Assert.Contains("Paris Info 124.200", text);
    }

    [Fact]
    public void DescribeBody_works_without_details()
    {
        var body = new Airspace("X", "", "SFC", "FL65", [], AirspaceKind.Other, 0, 6500, 99).DescribeBody();
        Assert.Equal(2, body.Count);
        Assert.Equal("Espace aérien", body[0]);
    }

    [Fact]
    public void FormatLimit_converts_metres_to_feet()
    {
        var (text, feet) = Airspace.FormatLimit(1000, 0, 1);
        Assert.Equal("1000m AMSL", text);
        Assert.Equal(3280.84, feet, 1);
    }
}
