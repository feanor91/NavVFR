using System.Net;
using VFRNav.Core.Services;

namespace VFRNav.Tests;

public class WeatherServiceTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            string body = request.RequestUri!.AbsolutePath.EndsWith("/metar", StringComparison.Ordinal) ? "METAR LFPO 311030Z CAVOK" : "TAF LFPO 311100Z CAVOK";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body + "\n") });
        }
    }

    [Fact]
    public async Task Cached_lookup_makes_one_pair_of_requests_per_airport_per_ten_minutes()
    {
        var handler = new FakeHandler();
        var service = new WeatherService(new HttpClient(handler));

        var (metar, taf) = await service.GetCachedAsync("LFPO");
        await service.GetCachedAsync("lfpo");

        Assert.Equal("METAR LFPO 311030Z CAVOK", metar);
        Assert.Equal("TAF LFPO 311100Z CAVOK", taf);
        Assert.Equal(2, handler.Calls); // un METAR et un TAF, une seule fois

        await service.GetCachedAsync("LFPG");
        Assert.Equal(4, handler.Calls);
    }
}
