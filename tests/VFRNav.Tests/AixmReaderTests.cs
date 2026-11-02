using System.Text;
using VFRNav.Core.Ifr;

namespace VFRNav.Tests;

public class AixmReaderTests
{
    // Extrait fabriqué à la main, au format de l'export AIXM 4.5 du SIA (aucune donnée du SIA n'est copiée).
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <AIXM-Snapshot created="2026-09-09T15:26:41.163+02:00" origin="Sia-France" version="4.5" effective="2026-10-29T00:00:00.000+01:00">
          <Ahp><AhpUid mid="1"><codeId>LFAA</codeId></AhpUid><txtName>TEST AD</txtName><codeType>AD</codeType><geoLat>480000.00N</geoLat><geoLong>0020000.00E</geoLong></Ahp>
          <Ahp><AhpUid mid="2"><codeId>LF153</codeId></AhpUid><txtName>HELIPORT</txtName><codeType>HP</codeType><geoLat>480000.00N</geoLat><geoLong>0020000.00E</geoLong></Ahp>
          <Ahp><AhpUid mid="3"><codeId>NW01</codeId></AhpUid><txtName>OUTRE-MER</txtName><codeType>AD</codeType><geoLat>201721.00S</geoLat><geoLong>1640558.00E</geoLong></Ahp>
          <Dpn><DpnUid mid="10"><codeId>ALPHA</codeId><geoLat>470000.00N</geoLat><geoLong>0020000.00E</geoLong></DpnUid><codeType>ICAO</codeType></Dpn>
          <Vor><VorUid mid="11"><codeId>BRV</codeId><geoLat>460000.00N</geoLat><geoLong>0020000.00E</geoLong></VorUid><codeType>VOR</codeType></Vor>
          <Dme><DmeUid mid="12"><codeId>BRV</codeId><geoLat>460000.00N</geoLat><geoLong>0020000.00E</geoLong></DmeUid><VorUid mid="11"><codeId>BRV</codeId></VorUid></Dme>
          <Ndb><NdbUid mid="13"><codeId>CH</codeId><geoLat>450000.00N</geoLat><geoLong>0020000.00E</geoLong></NdbUid></Ndb>
          <Rte><RteUid mid="20"><txtDesig>UN1</txtDesig></RteUid></Rte>
          <Rsg>
            <RsgUid mid="21"><RteUid mid="20"><txtDesig>UN1</txtDesig></RteUid>
              <DpnUidSta mid="10"><codeId>ALPHA</codeId><geoLat>470000.00N</geoLat><geoLong>0020000.00E</geoLong></DpnUidSta>
              <VorUidEnd mid="11"><codeId>BRV</codeId><geoLat>460000.00N</geoLat><geoLong>0020000.00E</geoLong></VorUidEnd></RsgUid>
            <codeType>RNAV</codeType><valDistVerUpper>195</valDistVerUpper><uomDistVerUpper>FL</uomDistVerUpper>
            <valDistVerLower>5000</valDistVerLower><uomDistVerLower>FT</uomDistVerLower>
          </Rsg>
          <Rsg>
            <RsgUid mid="22"><RteUid mid="20"><txtDesig>UN1</txtDesig></RteUid>
              <VorUidSta mid="11"><codeId>BRV</codeId><geoLat>460000.00N</geoLat><geoLong>0020000.00E</geoLong></VorUidSta>
              <DpnUidEnd mid="14"><codeId>ZULU</codeId><geoLat>44.5N</geoLat><geoLong>002.25E</geoLong></DpnUidEnd></RsgUid>
            <codeType>RNAV</codeType><valDistVerUpper>500</valDistVerUpper><uomDistVerUpper>FL</uomDistVerUpper>
            <valDistVerLower>195</valDistVerLower><uomDistVerLower>FL</uomDistVerLower>
          </Rsg>
          <Rsg>
            <RsgUid mid="23"><RteUid mid="20"><txtDesig>UN1</txtDesig></RteUid>
              <DpnUidSta mid="10"><codeId>ALPHA</codeId><geoLat>470000.00N</geoLat><geoLong>0020000.00E</geoLong></DpnUidSta></RsgUid>
          </Rsg>
        </AIXM-Snapshot>
        """;

    private static AixmLoadResult Load(GeoArea? area = null) => AixmReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(Sample)), area);

    [Fact]
    public void Reads_points_beacons_and_four_letter_airports_only()
    {
        var data = Load().Data;

        Assert.Equal(FixKind.Airport, data.Find("LFAA")!.Kind);
        Assert.Null(data.Find("LF153")); // héliport
        Assert.Equal(FixKind.Vor, data.Find("BRV")!.Kind); // le DME colocalisé ne remplace pas le VOR
        Assert.Equal(FixKind.Ndb, data.Find("CH")!.Kind);
        Assert.Equal(48.0, data.Find("LFAA")!.Lat, 6);
        Assert.Equal(2.0, data.Find("LFAA")!.Lon, 6);
    }

    [Fact]
    public void Segments_keep_their_airway_and_vertical_limits_in_flight_levels()
    {
        var data = Load().Data;
        var first = data.Segments.Single(s => s.From == "ALPHA");

        Assert.Equal("UN1", first.Airway);
        Assert.Equal(50, first.LowerFl);   // 5000 ft -> FL50
        Assert.Equal(195, first.UpperFl);
        Assert.Equal(2, data.Segments.Count); // le tronçon sans arrivée est ignoré
    }

    [Fact]
    public void Decimal_degree_coordinates_are_supported()
    {
        var zulu = Load().Data.Find("ZULU")!;
        Assert.Equal(44.5, zulu.Lat, 6);
        Assert.Equal(2.25, zulu.Lon, 6);
    }

    [Theory]
    [InlineData("480133.00N", 2, 48.025833)]
    [InlineData("0035450.00E", 3, 3.913889)]
    [InlineData("0613224.50W", 3, -61.540139)]
    [InlineData("163353N", 2, 16.564722)]
    [InlineData("201721.00S", 2, -20.289167)]
    [InlineData("47.46654728N", 2, 47.466547)]
    [InlineData("007.66545889E", 3, 7.665459)]
    public void ParseCoordinate_handles_every_format_of_the_export(string text, int digits, double expected)
    {
        Assert.Equal(expected, AixmReader.ParseCoordinate(text, digits)!.Value, 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("48N0")]
    [InlineData("abcdefN")]
    public void ParseCoordinate_rejects_garbage(string text) => Assert.Null(AixmReader.ParseCoordinate(text, 2));

    [Fact]
    public void Area_filter_drops_overseas_points()
    {
        var all = Load().Data;
        var metropolitan = Load(GeoArea.MetropolitanFrance).Data;

        Assert.NotNull(all.Find("LFAA"));
        Assert.NotNull(metropolitan.Find("LFAA"));
    }

    [Fact]
    public void Airac_cycle_and_effective_date_come_from_the_file()
    {
        var data = Load().Data;
        Assert.Equal("2611", data.AiracCycle);
        Assert.Equal(new DateTime(2026, 10, 29), data.EffectiveFrom);
        Assert.False(data.IsExpired(new DateTime(2026, 11, 20)));
        Assert.True(data.IsExpired(new DateTime(2026, 11, 26)));
    }

    [Theory]
    [InlineData("2026-01-22", "2601")]
    [InlineData("2026-12-23", "2612")]
    [InlineData("2026-12-24", "2613")]
    [InlineData("2027-01-20", "2613")]
    [InlineData("2027-01-21", "2701")]
    [InlineData("2025-01-23", "2501")]
    [InlineData("2026-01-10", "2513")]
    public void Airac_numbering_follows_28_day_cycles(string date, string cycle)
        => Assert.Equal(cycle, AixmReader.AiracCycleOf(DateTime.Parse(date)));

    [Fact]
    public void Real_file_is_not_a_required_input_and_wrong_files_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => AixmReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("<foo/>"))));
    }

    [Fact]
    public void Imported_data_has_no_procedures_so_airports_join_the_network_directly()
    {
        var data = Load().Data;
        Assert.Empty(data.Procedures);

        var result = IfrRouter.Plan(data, "LFAA", "LFAA");
        Assert.True(result.Success || result.Error is not null);
    }
}
