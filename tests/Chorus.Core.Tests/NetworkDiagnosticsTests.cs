using System.Net;
using Chorus.Core.Net;
using Xunit;

namespace Chorus.Core.Tests;

public class NetworkDiagnosticsTests
{
    private static readonly NetworkDiagnosticsContext Context = new(17897, 19090, true, false, true, false);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private sealed class ProgressSink(Action<DiagnosticProgress> report) : IProgress<DiagnosticProgress>
    {
        public void Report(DiagnosticProgress value) => report(value);
    }

    private static NetworkDiagnostics Create(
        Func<bool>? network = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? dns = null,
        Func<IPEndPoint[]>? listeners = null,
        Func<NetworkDiagnostics.ProxyState>? proxy = null,
        Func<bool>? admin = null,
        Func<int, HttpMessageHandler>? handler = null,
        TimeSpan? timeout = null) => new(new NetworkDiagnostics.Dependencies
        {
            NetworkAvailable = network ?? (() => true),
            Resolve = dns ?? ((_, _) => Task.FromResult(new[] { IPAddress.Loopback })),
            Listeners = listeners ?? (() => [new(IPAddress.Loopback, Context.MixedPort), new(IPAddress.Loopback, Context.ApiPort)]),
            SystemProxy = proxy ?? (() => new(true, $"127.0.0.1:{Context.MixedPort}", false)),
            Administrator = admin ?? (() => true),
            ProxyHandler = handler ?? (_ => new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent))))
        }, timeout ?? TimeSpan.FromSeconds(2));

    private static DiagnosticResult Item(IReadOnlyList<DiagnosticResult> results, string id) => Assert.Single(results, result => result.Id == id);

    [Fact]
    public async Task ReportsAllChecksAndOrderedProgressUsingExistingProbeEndpoint()
    {
        var updates = new List<DiagnosticProgress>();
        var handler = new Handler((request, _) =>
        {
            Assert.Equal(NetworkProbe.LatencyUrl, request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var diagnostics = Create(dns: (host, _) =>
        {
            Assert.Equal(new Uri(NetworkProbe.LatencyUrl).Host, host);
            return Task.FromResult(new[] { IPAddress.Loopback });
        }, handler: port =>
        {
            Assert.Equal(Context.MixedPort, port);
            return handler;
        });

        var results = await diagnostics.RunAsync(Context, new ProgressSink(updates.Add));

        Assert.Equal(8, results.Count);
        Assert.Equal(16, updates.Count);
        Assert.Equal(Enumerable.Range(1, 8), updates.Where(update => update.Result is not null).Select(update => update.Completed));
        Assert.All(updates, update => Assert.Equal(8, update.Total));
        Assert.Equal(DiagnosticStatus.Skipped, Item(results, "tun").Status);
        Assert.Equal(DiagnosticStatus.Passed, Item(results, "http").Status);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LocalFailuresDoNotBlockProxyRequestAndErrorsAreSanitized()
    {
        const string secret = "https://user:password@subscription.invalid/?api_secret=private";
        var results = await Create(network: () => throw new Exception(secret),
            dns: (_, _) => throw new Exception(secret), listeners: () => throw new Exception(secret),
            proxy: () => throw new Exception(secret), admin: () => throw new Exception(secret))
            .RunAsync(Context with { TunEnabled = true });

        Assert.Equal(8, results.Count);
        Assert.Equal(DiagnosticStatus.Passed, Item(results, "http").Status);
        Assert.Equal(6, results.Count(result => result.Status == DiagnosticStatus.Failed));
        Assert.All(results, result => Assert.DoesNotContain(secret, result.ToString()));
    }

    [Fact]
    public async Task ProxyFailureNeverFallsBackToDirect()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("private credentials"));
        var factories = 0;
        var results = await Create(handler: port =>
        {
            factories++;
            Assert.Equal(Context.MixedPort, port);
            return handler;
        }).RunAsync(Context);

        Assert.Equal(DiagnosticStatus.Failed, Item(results, "http").Status);
        Assert.DoesNotContain("private credentials", Item(results, "http").ToString());
        Assert.Contains("不会直连回退", Item(results, "http").Advice);
        Assert.Equal(1, factories);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ProductionTransportSendsConnectToConfiguredProxyWithoutExternalRequest()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            var firstLine = await reader.ReadLineAsync(timeout.Token);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
            return firstLine;
        }, timeout.Token);

        var results = await Create(handler: NetworkDiagnostics.CreateProxyHandler)
            .RunAsync(Context with { MixedPort = port }, ct: timeout.Token);

        Assert.Equal("CONNECT www.gstatic.com:443 HTTP/1.1", await accept);
        Assert.Equal(DiagnosticStatus.Failed, Item(results, "http").Status);
        Assert.Contains("不会直连回退", Item(results, "http").Advice);
    }

    [Theory]
    [InlineData(204, DiagnosticStatus.Passed)]
    [InlineData(200, DiagnosticStatus.Failed)]
    [InlineData(302, DiagnosticStatus.Failed)]
    [InlineData(407, DiagnosticStatus.Failed)]
    [InlineData(503, DiagnosticStatus.Failed)]
    public async Task OnlyExpected204Passes(int code, DiagnosticStatus expected)
    {
        var results = await Create(handler: _ => new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)))).RunAsync(Context);
        Assert.Equal(expected, Item(results, "http").Status);
    }

    [Fact]
    public void ProductionHandlerUsesExplicitProxyWithoutRedirectsOrCredentials()
    {
        using var handler = NetworkDiagnostics.CreateProxyHandler(Context.MixedPort);
        var destination = new Uri(NetworkProbe.LatencyUrl);
        Assert.True(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.Proxy!.Credentials);
        Assert.False(handler.Proxy.IsBypassed(destination));
        Assert.Equal(new Uri($"http://127.0.0.1:{Context.MixedPort}"), handler.Proxy.GetProxy(destination));
    }

    [Fact]
    public async Task TimeoutsContinueWithRemainingChecksEvenIfResolverIgnoresCancellation()
    {
        var pending = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var results = await Create(dns: (_, _) => pending.Task, timeout: TimeSpan.FromMilliseconds(150))
                .RunAsync(Context).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("超时", Item(results, "dns").Detail);
            Assert.Equal(DiagnosticStatus.Passed, Item(results, "http").Status);
            Assert.Equal(8, results.Count);
        }
        finally
        {
            pending.TrySetResult([]);
        }
    }

    [Fact]
    public async Task HttpRequestHasTimeoutAndReceivesCancellation()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var results = await Create(handler: _ => handler, timeout: TimeSpan.FromMilliseconds(150))
            .RunAsync(Context).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("超时", Item(results, "http").Detail);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancellationStopsRemainingChecksAndPreservesCompletedProgress()
    {
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new List<DiagnosticResult>();
        var requests = 0;
        var diagnostics = Create(dns: async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return [];
        }, handler: _ => { requests++; throw new InvalidOperationException(); });
        var run = diagnostics.RunAsync(Context, new ProgressSink(update =>
        {
            if (update.Result is not null) results.Add(update.Result);
        }), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("network", Assert.Single(results).Id);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task PreCancelledRunDoesNotProbe()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(network: () => { calls++; return true; }).RunAsync(Context, ct: cts.Token));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true, true, DiagnosticStatus.Passed)]
    [InlineData(true, false, DiagnosticStatus.Warning)]
    [InlineData(false, true, DiagnosticStatus.Warning)]
    [InlineData(false, false, DiagnosticStatus.Passed)]
    public async Task PortsDistinguishRunningListenersFromStoppedOccupancy(bool running, bool occupied, DiagnosticStatus status)
    {
        var results = await Create(listeners: () => occupied
            ? [new(IPAddress.Any, Context.MixedPort), new(IPAddress.Loopback, Context.ApiPort)] : [])
            .RunAsync(Context with { CoreRunning = running });
        Assert.Equal(status, Item(results, "mixed").Status);
        Assert.Equal(status, Item(results, "api").Status);
        if (occupied) Assert.Contains(running ? "未验证进程归属" : "未识别占用进程", Item(results, "mixed").Detail);
    }

    [Theory]
    [InlineData(false, DiagnosticStatus.Passed)]
    [InlineData(true, DiagnosticStatus.Warning)]
    public async Task StoppedMixedPortRespectsLanBindAddress(bool allowLan, DiagnosticStatus expected)
    {
        var results = await Create(listeners: () => [new(IPAddress.Parse("192.0.2.1"), Context.MixedPort)])
            .RunAsync(Context with { CoreRunning = false, AllowLan = allowLan });
        Assert.Equal(expected, Item(results, "mixed").Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task InvalidMixedPortIsReportedAndNeverRequested(int port)
    {
        var calls = 0;
        var results = await Create(handler: _ => { calls++; throw new InvalidOperationException(); })
            .RunAsync(Context with { MixedPort = port });
        Assert.Equal(DiagnosticStatus.Failed, Item(results, "mixed").Status);
        Assert.Equal(DiagnosticStatus.Skipped, Item(results, "http").Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StoppedCoreDoesNotSendRequestsToAnUnrelatedLocalListener()
    {
        var calls = 0;
        var results = await Create(handler: _ => { calls++; throw new InvalidOperationException(); })
            .RunAsync(Context with { CoreRunning = false });
        Assert.Equal(DiagnosticStatus.Skipped, Item(results, "http").Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DuplicatePortsAreConfigurationErrors()
    {
        var results = await Create().RunAsync(Context with { ApiPort = Context.MixedPort });
        Assert.Equal(DiagnosticStatus.Failed, Item(results, "mixed").Status);
        Assert.Equal(DiagnosticStatus.Failed, Item(results, "api").Status);
    }

    [Theory]
    [InlineData("127.0.0.1:17897", true)]
    [InlineData("localhost:17897", true)]
    [InlineData("http=127.0.0.1:17897;https=localhost:17897", true)]
    [InlineData("HTTP=localhost:17897; HTTPS=127.0.0.1:17897", true)]
    [InlineData("http=127.0.0.1:17897", false)]
    [InlineData("http=127.0.0.1:17897;https=127.0.0.1:9090", false)]
    [InlineData("127.0.0.1:7897", false)]
    [InlineData("proxy.invalid:17897", false)]
    [InlineData("http://user:secret@127.0.0.1:17897", false)]
    [InlineData("http://127.0.0.1:17897/?secret=hidden", false)]
    [InlineData("socks=127.0.0.1:17897", false)]
    [InlineData(null, false)]
    public void MatchesBothSystemProxyProtocolsWithoutExposingValues(string? server, bool expected)
    {
        Assert.Equal(expected, NetworkDiagnostics.ProxyMatches(server, Context.MixedPort));
    }

    [Theory]
    [InlineData(true, false, true, DiagnosticStatus.Passed)]
    [InlineData(true, false, false, DiagnosticStatus.Warning)]
    [InlineData(true, true, true, DiagnosticStatus.Warning)]
    [InlineData(false, false, true, DiagnosticStatus.Warning)]
    [InlineData(false, false, false, DiagnosticStatus.Skipped)]
    public async Task SystemProxyChecksRealStatePacAndCore(bool enabled, bool pac, bool running, DiagnosticStatus expected)
    {
        var results = await Create(proxy: () => new(enabled, $"127.0.0.1:{Context.MixedPort}", pac))
            .RunAsync(Context with { CoreRunning = running });
        Assert.Equal(expected, Item(results, "system-proxy").Status);
    }

    [Fact]
    public async Task ForeignProxyValueNeverAppearsInResults()
    {
        const string server = "http://user:secret@private.invalid:1234";
        var results = await Create(proxy: () => new(true, server, false)).RunAsync(Context);
        Assert.Equal(DiagnosticStatus.Warning, Item(results, "system-proxy").Status);
        Assert.All(results, result => Assert.DoesNotContain(server, result.ToString()));
    }

    [Theory]
    [InlineData(false, false, DiagnosticStatus.Skipped)]
    [InlineData(true, false, DiagnosticStatus.Failed)]
    [InlineData(true, true, DiagnosticStatus.Passed)]
    public async Task TunChecksElevationOnlyWhenConfigured(bool tun, bool admin, DiagnosticStatus status)
    {
        var calls = 0;
        var results = await Create(admin: () => { calls++; return admin; })
            .RunAsync(Context with { TunEnabled = tun });
        Assert.Equal(status, Item(results, "tun").Status);
        Assert.Equal(tun ? 1 : 0, calls);
    }

    [Fact]
    public async Task UnavailableNetworkAndEmptyDnsDoNotPreventIndependentHttpProbe()
    {
        var results = await Create(network: () => false, dns: (_, _) => Task.FromResult(Array.Empty<IPAddress>()))
            .RunAsync(Context with { CoreRunning = null });
        Assert.Equal(DiagnosticStatus.Failed, Item(results, "network").Status);
        Assert.Equal(DiagnosticStatus.Failed, Item(results, "dns").Status);
        Assert.Equal(DiagnosticStatus.Warning, Item(results, "core").Status);
        Assert.Equal(DiagnosticStatus.Passed, Item(results, "http").Status);
    }
}
