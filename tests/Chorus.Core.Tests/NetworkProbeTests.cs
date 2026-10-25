using System.Net;
using System.Text;
using Chorus.Core.Net;
using Xunit;

namespace Chorus.Core.Tests;

public class NetworkProbeTests
{
    private const string IpApiOk =
        """{"status":"success","country":"中国香港","city":"香港","isp":"HKT","query":"203.0.113.7"}""";

    private const string IpWhoOk =
        """{"ip":"198.51.100.9","success":true,"country":"日本","city":"东京","connection":{"isp":"NTT"}}""";

    /// <summary>按 host 决定返回什么，用来模拟「主接口挂了、备用接口还活着」。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

    private static NetworkProbe Probe(StubHandler handler) =>
        new(new HttpClient(handler), TimeSpan.FromSeconds(2));

    [Fact]
    public async Task Run_Returns_Latency_And_Ip_From_Primary_Endpoint()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.Host == "ip-api.com" ? Json(IpApiOk) : NoContent());

        var result = await Probe(handler).RunAsync();

        Assert.NotNull(result.LatencyMs);
        Assert.NotNull(result.Info);
        Assert.Equal("203.0.113.7", result.Info!.Ip);
        Assert.Equal("中国香港", result.Info.Country);
        Assert.Equal("HKT", result.Info.Isp);
    }

    [Fact]
    public async Task Lookup_Falls_Back_To_Second_Endpoint_When_Primary_Fails()
    {
        var handler = new StubHandler(req => req.RequestUri!.Host == "ip-api.com"
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : Json(IpWhoOk));

        var info = await Probe(handler).LookupIpAsync();

        Assert.NotNull(info);
        Assert.Equal("198.51.100.9", info!.Ip);
        Assert.Contains(handler.Requested, url => url.Contains("ipwho.is"));
    }

    [Fact]
    public async Task Lookup_Returns_Null_When_Every_Endpoint_Fails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var info = await Probe(handler).LookupIpAsync();

        Assert.Null(info);
    }

    [Fact]
    public async Task Latency_Is_Null_When_Probe_Endpoint_Fails()
    {
        var handler = new StubHandler(req => req.RequestUri!.Host == "ip-api.com"
            ? Json(IpApiOk)
            : throw new HttpRequestException("网络不通"));

        var result = await Probe(handler).RunAsync();

        Assert.Null(result.LatencyMs);
        // 延迟挂掉不影响 IP 归属（两者并行，互不连坐）
        Assert.NotNull(result.Info);
    }

    [Fact]
    public async Task Run_Does_Not_Throw_When_Everything_Fails()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("断网"));

        var result = await Probe(handler).RunAsync();

        Assert.Null(result.LatencyMs);
        Assert.Null(result.Info);
    }

    [Fact]
    public async Task Primary_Endpoint_Asks_For_Country_Code()
    {
        // ip-api 的 fields 参数会裁掉没列出的字段：漏掉 countryCode 就没法显示国旗
        var handler = new StubHandler(req => req.RequestUri!.Host == "ip-api.com" ? Json(IpApiOk) : NoContent());

        await Probe(handler).LookupIpAsync();

        Assert.Contains(handler.Requested, url => url.Contains("ip-api.com") && url.Contains("countryCode"));
    }
}
