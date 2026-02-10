using System.Net;
using System.Text;
using SirThaddeus.WebSearch;

namespace SirThaddeus.Tests;

public class WeatherServiceTests
{
    [Fact]
    public async Task Geocode_CachesResults_ByNormalizedPlace()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("photon.komoot.io/api", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"features":[{"geometry":{"coordinates":[-111.789,43.826]},"properties":{"name":"Rexburg","state":"Idaho","country":"United States","countrycode":"US"}}]}""");
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new WeatherService(
            new WeatherServiceOptions { GeocodeCacheMinutes = 1_440, PlaceMemoryEnabled = false },
            http);

        var first = await svc.GeocodeAsync("Rexburg, ID", 1);
        var second = await svc.GeocodeAsync("rexburg,   id", 1);

        Assert.NotEmpty(first.Results);
        Assert.NotEmpty(second.Results);
        Assert.False(first.Cache.Hit);
        Assert.True(second.Cache.Hit);
        Assert.Single(handler.RequestedUrls.Where(u =>
            u.Contains("photon.komoot.io/api", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Geocode_FallsBackToOpenMeteo_WhenPhotonFails()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("photon.komoot.io/api", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"error":"throttled"}""", HttpStatusCode.TooManyRequests);
            }

            if (url.Contains("geocoding-api.open-meteo.com", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"results":[{"name":"Rexburg","admin1":"Idaho","country":"United States","country_code":"US","latitude":43.826,"longitude":-111.789}]}""");
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new WeatherService(new WeatherServiceOptions(), http);

        var geocode = await svc.GeocodeAsync("Rexburg, Idaho", maxResults: 1);

        Assert.Equal("open-meteo-fallback", geocode.Source);
        Assert.Single(geocode.Results);
        Assert.Contains(handler.RequestedUrls, u =>
            u.Contains("photon.komoot.io/api", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(handler.RequestedUrls, u =>
            u.Contains("geocoding-api.open-meteo.com", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Forecast_UsLocation_UsesNwsPrimary()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/points/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"properties":{"forecast":"https://api.weather.gov/gridpoints/PIH/100,100/forecast","forecastHourly":"https://api.weather.gov/gridpoints/PIH/100,100/forecast/hourly","relativeLocation":{"properties":{"city":"Rexburg","state":"ID"}}}}""");
            }
            if (url.EndsWith("/forecast", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"properties":{"periods":[{"startTime":"2026-02-10T08:00:00-07:00","temperature":39,"temperatureUnit":"F","shortForecast":"Partly Cloudy","isDaytime":true},{"startTime":"2026-02-10T20:00:00-07:00","temperature":28,"temperatureUnit":"F","shortForecast":"Mostly Clear","isDaytime":false}]}}""");
            }
            if (url.EndsWith("/forecast/hourly", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"properties":{"periods":[{"startTime":"2026-02-10T10:00:00-07:00","temperature":37,"temperatureUnit":"F","shortForecast":"Windy","windSpeed":"12 mph","relativeHumidity":{"value":71}}]}}""");
            }
            if (url.Contains("/alerts/active", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"features":[{"properties":{"headline":"Winter Weather Advisory","severity":"Moderate","event":"Winter Weather Advisory"}}]}""");
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new WeatherService(new WeatherServiceOptions(), http);

        var wx = await svc.ForecastAsync(43.826, -111.789, "Rexburg, ID", "US", days: 3);

        Assert.Equal("nws", wx.Provider);
        Assert.Equal("us_primary", wx.ProviderReason);
        Assert.NotNull(wx.Current);
        Assert.Equal(37, wx.Current!.Temperature);
        Assert.Contains(handler.RequestedUrls, u =>
            u.Contains("api.weather.gov/points", StringComparison.OrdinalIgnoreCase));

        var nwsRequests = handler.Requests
            .Where(r => r.Url.Contains("api.weather.gov", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(nwsRequests);
        Assert.All(nwsRequests, req =>
        {
            Assert.False(string.IsNullOrWhiteSpace(req.UserAgent));
            Assert.Contains(req.Accept, a =>
                string.Equals(a, "application/geo+json", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task Forecast_NonUs_UsesOpenMeteo()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.open-meteo.com", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"current":{"time":"2026-02-10T16:00","temperature_2m":6.0,"weather_code":3,"wind_speed_10m":14.0,"relative_humidity_2m":81},"daily":{"time":["2026-02-10","2026-02-11"],"temperature_2m_max":[8.0,9.0],"temperature_2m_min":[3.0,4.0],"weather_code":[3,61]}}""");
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new WeatherService(new WeatherServiceOptions(), http);

        var wx = await svc.ForecastAsync(48.8566, 2.3522, "Paris, FR", "FR", days: 2);

        Assert.Equal("open-meteo", wx.Provider);
        Assert.Equal("non_us_openmeteo", wx.ProviderReason);
        Assert.NotNull(wx.Current);
        Assert.Equal("C", wx.Current!.Unit);
        Assert.True(handler.RequestedUrls.All(u =>
            !u.Contains("api.weather.gov", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Forecast_UsFallsBackToOpenMeteo_WhenNwsFails()
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
                return JsonResponse(
                    """{"current":{"time":"2026-02-10T10:00","temperature_2m":32.0,"weather_code":71,"wind_speed_10m":10.0,"relative_humidity_2m":78},"daily":{"time":["2026-02-10"],"temperature_2m_max":[35.0],"temperature_2m_min":[24.0],"weather_code":[71]}}""");
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new WeatherService(new WeatherServiceOptions(), http);

        var wx = await svc.ForecastAsync(43.826, -111.789, "Rexburg, ID", "US", days: 1);

        Assert.Equal("open-meteo", wx.Provider);
        Assert.Equal("fallback_nws_error", wx.ProviderReason);
        Assert.NotNull(wx.Current);
        Assert.Equal("F", wx.Current!.Unit);
    }

    [Fact]
    public async Task Forecast_Cache_HitOnSecondRequest()
    {
        var handler = new RecordingHttpHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("api.open-meteo.com", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    """{"current":{"time":"2026-02-10T16:00","temperature_2m":7.0,"weather_code":2,"wind_speed_10m":8.0,"relative_humidity_2m":70},"daily":{"time":["2026-02-10"],"temperature_2m_max":[9.0],"temperature_2m_min":[3.0],"weather_code":[2]}}""");
            }

            return JsonResponse("""{"error":"unexpected"}""", HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler);
        var svc = new WeatherService(
            new WeatherServiceOptions { ForecastCacheMinutes = 15 },
            http);

        var first = await svc.ForecastAsync(48.8566, 2.3522, "Paris, FR", "FR", days: 1);
        var second = await svc.ForecastAsync(48.8566, 2.3522, "Paris, FR", "FR", days: 1);

        Assert.False(first.Cache.Hit);
        Assert.True(second.Cache.Hit);
        Assert.Single(handler.RequestedUrls.Where(u =>
            u.Contains("api.open-meteo.com", StringComparison.OrdinalIgnoreCase)));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;
        public List<string> RequestedUrls { get; } = [];
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            RequestedUrls.Add(url);
            Requests.Add(new RecordedRequest(
                Url: url,
                UserAgent: request.Headers.UserAgent.ToString(),
                Accept: request.Headers.Accept.Select(a => a.MediaType ?? "").ToArray()));
            return Task.FromResult(_responder(request));
        }
    }

    private sealed record RecordedRequest(string Url, string UserAgent, IReadOnlyList<string> Accept);
}
