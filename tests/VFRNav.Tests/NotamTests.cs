using System.Net;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class NotamTests
{
    [Fact]
    public void Parse_reads_a_plain_array()
    {
        var list = NotamService.Parse("""[{"id":"A1234/26","message":"(A1234/26 NOTAMN Q) LFFF/QMRLC... E) RWY 08/26 CLSD)","startdate":"2610010800","enddate":"2610311800","location":"LFPO"}]""", "LFXX");
        var n = Assert.Single(list);
        Assert.Equal("A1234/26", n.Id);
        Assert.Equal("LFPO", n.Location);
        Assert.Contains("RWY 08/26 CLSD", n.Text);
        Assert.Equal("2610010800", n.Start);
        Assert.Equal("2610311800", n.End);
    }

    [Fact]
    public void Parse_reads_an_object_wrapping_the_list_and_falls_back_to_the_requested_location()
    {
        var list = NotamService.Parse("""{"count":1,"results":[{"Number":42,"Message":"texte"}]}""", "LFPG");
        var n = Assert.Single(list);
        Assert.Equal("42", n.Id);
        Assert.Equal("LFPG", n.Location);
        Assert.Equal("texte", n.Text);
    }

    [Fact]
    public void Parse_keeps_the_raw_item_when_no_text_field_is_known()
    {
        var n = Assert.Single(NotamService.Parse("""[{"zzz":"valeur"}]""", "LFPG"));
        Assert.Contains("valeur", n.Text);
    }

    [Fact]
    public void Parse_of_an_empty_or_unexpected_document_is_empty()
    {
        Assert.Empty(NotamService.Parse("[]", "LFPG"));
        Assert.Empty(NotamService.Parse("""{"error":"x"}""", "LFPG"));
        Assert.Empty(NotamService.Parse("42", "LFPG"));
    }

    private sealed class CountingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Results_are_cached_per_airport_to_save_the_free_quota()
    {
        var handler = new CountingHandler("""[{"id":"A1","message":"x"}]""");
        var service = new NotamService(new HttpClient(handler));
        await service.GetAsync("LFPO", "KEY");
        await service.GetAsync("lfpo", "KEY");
        Assert.Equal(1, handler.Calls);
        await service.GetAsync("LFPG", "KEY");
        Assert.Equal(2, handler.Calls);
        Assert.Contains("locations=LFPG", handler.LastUrl);
        Assert.Contains("api_key=KEY", handler.LastUrl);
    }

    [Fact]
    public async Task Http_error_is_reported()
    {
        var service = new NotamService(new HttpClient(new CountingHandler("denied", HttpStatusCode.Forbidden)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.GetAsync("LFPO", "BAD"));
        Assert.Contains("403", ex.Message);
    }
}
