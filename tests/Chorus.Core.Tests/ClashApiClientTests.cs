using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Chorus.Core.ClashApi;
using Xunit;

namespace Chorus.Core.Tests;

public class ClashApiClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return Responder(request);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task GetProxiesAsync_Parses_Groups_And_Nodes()
    {
        var handler = new FakeHandler
        {
            Responder = _ => JsonResponse(
                """{"proxies":{"🚀 节点选择":{"type":"Selector","now":"节点A","all":["节点A","♻️ 自动选择"]},"节点A":{"type":"Shadowsocks","history":[{"time":"t","delay":123}]}}}"""),
        };
        var client = new ClashApiClient(handler);
        client.Configure("127.0.0.1", 9090, "secret123");

        var proxies = await client.GetProxiesAsync();

        Assert.Equal(2, proxies.Count);
        Assert.Equal("Selector", proxies["🚀 节点选择"].Type);
        Assert.Equal("节点A", proxies["🚀 节点选择"].Now);
        Assert.Equal(["节点A", "♻️ 自动选择"], proxies["🚀 节点选择"].All);
        Assert.Equal(123, proxies["节点A"].LastDelay);
        Assert.Equal("Bearer secret123", handler.Requests[0].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task SelectAsync_Sends_Put_With_Escaped_Path()
    {
        var handler = new FakeHandler { Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent) };
        var client = new ClashApiClient(handler);

        await client.SelectAsync("🚀 节点选择", "节点A");

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/proxies/🚀 节点选择", Uri.UnescapeDataString(request.RequestUri!.AbsolutePath));

        var name = JsonNode.Parse(handler.Bodies.Single()!)?["name"]?.GetValue<string>();
        Assert.Equal("节点A", name);
    }

    [Fact]
    public async Task SelectAsync_Throws_With_Body_On_Error()
    {
        var handler = new FakeHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"message":"group not found"}""", Encoding.UTF8, "application/json"),
            },
        };
        var client = new ClashApiClient(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SelectAsync("g", "n"));

        Assert.Contains("group not found", ex.Message);
    }

    [Fact]
    public async Task GetDelayAsync_Returns_Delay()
    {
        var handler = new FakeHandler { Responder = _ => JsonResponse("""{"delay":234}""") };
        var client = new ClashApiClient(handler);

        var delay = await client.GetDelayAsync("节点A");

        Assert.Equal(234, delay);
        var path = Uri.UnescapeDataString(handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("/proxies/节点A/delay", path);
    }

    [Fact]
    public async Task GetDelayAsync_Returns_Null_On_Error()
    {
        var handler = new FakeHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"message":"timeout"}"""),
            },
        };
        var client = new ClashApiClient(handler);

        var delay = await client.GetDelayAsync("bad");

        Assert.Null(delay);
    }
}
