using System.Net;
using System.Text;
using SirThaddeus.WebSearch;

namespace SirThaddeus.Tests;

public class PublicApiProvidersTests
{
    [Fact]
    public async Task Timezone_UsesNwsForUs_AndCaches()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.weather.gov/points", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"properties":{"timeZone":"America/Denver"}}""");
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var provider = new TimezoneProvider(
            new PublicApiServiceOptions { TimezoneCacheMinutes = 60 },
            http);

        var first = await provider.ResolveAsync(43.826, -111.789, "US");
        var second = await provider.ResolveAsync(43.826, -111.789, "US");

        Assert.Equal("America/Denver", first.Timezone);
        Assert.Equal("nws", first.Source);
        Assert.False(first.Cache.Hit);
        Assert.True(second.Cache.Hit);
        Assert.Single(handler.Requests.Where(r =>
            r.Url.Contains("api.weather.gov/points", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Timezone_FallsBackToOpenMeteo_WhenNwsFails()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.weather.gov/points", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"error":"down"}""", HttpStatusCode.ServiceUnavailable);
            }

            if (url.Contains("api.open-meteo.com", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"timezone":"America/Denver"}""");
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var provider = new TimezoneProvider(new PublicApiServiceOptions(), http);

        var result = await provider.ResolveAsync(43.826, -111.789, "US");

        Assert.Equal("America/Denver", result.Timezone);
        Assert.Equal("open-meteo", result.Source);
        Assert.Contains(handler.Requests, r =>
            r.Url.Contains("api.weather.gov/points", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.Requests, r =>
            r.Url.Contains("api.open-meteo.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Holidays_Get_FiltersRegion_AndCaches()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/PublicHolidays/2026/US", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """
                    [
                      {"date":"2026-01-01","localName":"New Year's Day","name":"New Year's Day","countryCode":"US","global":true,"counties":null,"launchYear":null,"types":["Public"]},
                      {"date":"2026-03-01","localName":"Idaho Day","name":"Idaho Day","countryCode":"US","global":false,"counties":["US-ID"],"launchYear":2000,"types":["Public"]},
                      {"date":"2026-03-02","localName":"California Day","name":"California Day","countryCode":"US","global":false,"counties":["US-CA"],"launchYear":2000,"types":["Public"]}
                    ]
                    """);
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var provider = new NagerDateHolidaysProvider(
            new PublicApiServiceOptions { HolidaysCacheMinutes = 120 },
            http);

        var first = await provider.GetHolidaysAsync("US", 2026, "US-ID", maxItems: 20);
        var second = await provider.GetHolidaysAsync("US", 2026, "US-ID", maxItems: 20);

        Assert.False(first.Cache.Hit);
        Assert.True(second.Cache.Hit);
        Assert.Contains(first.Holidays, h => h.Name.Contains("New Year's Day", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(first.Holidays, h => h.Name.Contains("Idaho Day", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(first.Holidays, h => h.Name.Contains("California Day", StringComparison.OrdinalIgnoreCase));
        Assert.Single(handler.Requests.Where(r =>
            r.Url.Contains("/PublicHolidays/2026/US", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Feed_Fetch_ParsesRss_AndCaches()
    {
        var handler = new RecordingHttpHandler(_ =>
            XmlResponse(
                """
                <rss version="2.0">
                  <channel>
                    <title>Engineering Blog</title>
                    <description>Latest engineering updates</description>
                    <item>
                      <title>Post One</title>
                      <link>https://example.com/post-1</link>
                      <description><![CDATA[First summary]]></description>
                      <pubDate>Tue, 10 Feb 2026 10:00:00 GMT</pubDate>
                    </item>
                    <item>
                      <title>Post Two</title>
                      <link>https://example.com/post-2</link>
                      <description><![CDATA[Second summary]]></description>
                      <pubDate>Tue, 10 Feb 2026 09:00:00 GMT</pubDate>
                    </item>
                  </channel>
                </rss>
                """));

        using var http = new HttpClient(handler);
        var provider = new FeedProvider(
            new PublicApiServiceOptions
            {
                FeedCacheMinutes = 30,
                BlockPrivateNetworkTargets = false
            },
            http);

        var first = await provider.FetchAsync("https://example.com/rss.xml", maxItems: 5);
        var second = await provider.FetchAsync("https://example.com/rss.xml", maxItems: 5);

        Assert.Equal("Engineering Blog", first.FeedTitle);
        Assert.Equal("rss", first.Source);
        Assert.Equal(2, first.Items.Count);
        Assert.False(first.Cache.Hit);
        Assert.True(second.Cache.Hit);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task StatusProbe_HeadFallbacksToGet_WhenHeadBlocked()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            if (req.Method == HttpMethod.Head)
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            if (req.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK);
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        using var http = new HttpClient(handler);
        var probe = new StatusProbe(
            new PublicApiServiceOptions { BlockPrivateNetworkTargets = false },
            http);

        var result = await probe.CheckAsync("https://example.com/health");

        Assert.True(result.Reachable);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("GET", result.Method);
        Assert.Contains(handler.Requests, r => r.Method == "HEAD");
        Assert.Contains(handler.Requests, r => r.Method == "GET");
    }

    [Fact]
    public async Task StatusProbe_BlocksLocalhost_WhenPrivateBlockingEnabled()
    {
        var handler = new RecordingHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));

        using var http = new HttpClient(handler);
        var probe = new StatusProbe(
            new PublicApiServiceOptions { BlockPrivateNetworkTargets = true },
            http);

        var result = await probe.CheckAsync("http://localhost:8080");

        Assert.False(result.Reachable);
        Assert.Contains("blocked", result.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage XmlResponse(string xml, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml")
        };
    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                Method: request.Method.Method,
                Url: request.RequestUri?.ToString() ?? ""));
            return Task.FromResult(_responder(request));
        }
    }

    private sealed record RecordedRequest(string Method, string Url);
}
