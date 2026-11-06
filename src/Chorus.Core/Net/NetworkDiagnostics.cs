using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using Microsoft.Win32;

namespace Chorus.Core.Net;

public sealed record NetworkDiagnosticsContext(
    int MixedPort, int ApiPort, bool? CoreRunning, bool TunEnabled,
    bool SystemProxyExpected, bool AllowLan);

public enum DiagnosticStatus { Passed, Warning, Failed, Skipped }

public sealed record DiagnosticResult(
    string Id, string Title, DiagnosticStatus Status, string Detail, string Advice = "");

public sealed record DiagnosticProgress(int Completed, int Total, string Current, DiagnosticResult? Result);

public sealed class NetworkDiagnostics
{
    internal sealed record ProxyState(bool Enabled, string? Server, bool HasPac);

    internal sealed class Dependencies
    {
        public Func<bool> NetworkAvailable { get; init; } = NetworkInterface.GetIsNetworkAvailable;
        public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } =
            (host, ct) => Dns.GetHostAddressesAsync(host, ct);
        public Func<IPEndPoint[]> Listeners { get; init; } =
            () => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        public Func<ProxyState> SystemProxy { get; init; } = ReadSystemProxy;
        public Func<bool> Administrator { get; init; } = IsAdministrator;
        public Func<int, HttpMessageHandler> ProxyHandler { get; init; } = CreateProxyHandler;
    }

    private readonly Dependencies _dependencies;
    private readonly TimeSpan _timeout;

    public NetworkDiagnostics() : this(new Dependencies(), TimeSpan.FromSeconds(5)) { }

    internal NetworkDiagnostics(Dependencies dependencies, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _dependencies = dependencies;
        _timeout = timeout;
    }

    public async Task<IReadOnlyList<DiagnosticResult>> RunAsync(NetworkDiagnosticsContext context,
        IProgress<DiagnosticProgress>? progress = null, CancellationToken ct = default)
    {
        var checks = new (string Id, string Title, string Advice, Func<CancellationToken, Task<DiagnosticResult>> Run)[]
        {
            ("network", "本机网络", "检查网线、Wi-Fi 和网卡是否启用。", _ => Task.FromResult(
                _dependencies.NetworkAvailable()
                    ? new DiagnosticResult("network", "本机网络", DiagnosticStatus.Passed, "检测到已连接的非回环网络接口；不代表互联网可达。")
                    : new DiagnosticResult("network", "本机网络", DiagnosticStatus.Failed, "未检测到已连接的网络接口。", "检查网线、Wi-Fi 和网卡是否启用。"))),
            ("dns", "系统 DNS", "检查系统 DNS 设置及网络连接；TUN 可能影响解析路径。", CheckDnsAsync),
            ("core", "核心状态", "启动核心后重试；若启动失败请查看核心日志。", _ => Task.FromResult(
                new DiagnosticResult("core", "核心状态", context.CoreRunning switch
                {
                    true => DiagnosticStatus.Passed,
                    false => DiagnosticStatus.Warning,
                    _ => DiagnosticStatus.Warning
                }, context.CoreRunning switch
                {
                    true => "开始诊断时核心进程正在运行。",
                    false => "开始诊断时核心未运行。",
                    _ => "无法读取核心进程状态。"
                }, context.CoreRunning == true ? "" : "启动核心后重试；若启动失败请查看核心日志。"))),
            ("mixed", $"Mixed 端口 {context.MixedPort}", "检查端口配置；如刚修改过设置，请重启核心后重试。",
                _ => Task.FromResult(CheckPort(context.MixedPort, "mixed", "Mixed", context, context.AllowLan))),
            ("api", $"API 端口 {context.ApiPort}", "检查 API 端口配置并重试。",
                _ => Task.FromResult(CheckPort(context.ApiPort, "api", "API", context, false))),
            ("system-proxy", "系统代理", "检查 Windows 系统代理设置及 PAC 配置。",
                _ => Task.FromResult(CheckSystemProxy(context))),
            ("tun", "TUN 权限", "如需 TUN，请以管理员身份重新运行 Chorus。",
                _ => Task.FromResult(!context.TunEnabled
                    ? new DiagnosticResult("tun", "TUN 权限", DiagnosticStatus.Skipped, "配置未开启 TUN。")
                    : _dependencies.Administrator()
                        ? new DiagnosticResult("tun", "TUN 权限", DiagnosticStatus.Passed, "配置开启 TUN，当前进程具有管理员权限；不代表 TUN 已生效。")
                        : new DiagnosticResult("tun", "TUN 权限", DiagnosticStatus.Failed, "配置开启 TUN，但当前进程没有管理员权限。", "以管理员身份重新运行 Chorus。"))),
            ("http", "Mixed HTTP 连通性", "检查核心、节点和路由规则，以及探测站点可达性；不会直连回退。",
                token => CheckHttpAsync(context, token))
        };
        var results = new List<DiagnosticResult>();
        foreach (var check in checks)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(results.Count, checks.Length, check.Title, null));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_timeout);
            DiagnosticResult result;
            try
            {
                result = await Task.Run(() => check.Run(timeout.Token), timeout.Token)
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = new(check.Id, check.Title, DiagnosticStatus.Failed,
                    $"检查超时（每项最多 {_timeout.TotalSeconds:0.#} 秒）。", check.Advice);
            }
            catch (Exception)
            {
                result = new(check.Id, check.Title, DiagnosticStatus.Failed, "检查失败或无法读取系统状态。", check.Advice);
            }
            ct.ThrowIfCancellationRequested();
            results.Add(result);
            progress?.Report(new(results.Count, checks.Length, check.Title, result));
        }
        return results;
    }

    private async Task<DiagnosticResult> CheckDnsAsync(CancellationToken ct)
    {
        var host = new Uri(NetworkProbe.LatencyUrl).Host;
        var addresses = await _dependencies.Resolve(host, ct).ConfigureAwait(false);
        return addresses.Length > 0
            ? new("dns", "系统 DNS", DiagnosticStatus.Passed, $"系统解析器已解析 {host}；可能使用系统缓存或 TUN。")
            : new("dns", "系统 DNS", DiagnosticStatus.Failed, $"系统解析器未返回 {host} 的地址。", "检查系统 DNS 设置及网络连接。");
    }

    private DiagnosticResult CheckPort(int port, string id, string name, NetworkDiagnosticsContext context, bool allowLan)
    {
        var title = $"{name} 端口 {port}";
        if (!ValidPort(port))
            return new(id, title, DiagnosticStatus.Failed, "端口不在 1–65535 范围内。", "在设置页填写有效端口。");
        if (context.MixedPort == context.ApiPort)
            return new(id, title, DiagnosticStatus.Failed, "Mixed 与 API 配置使用相同端口。", "为 Mixed 和 API 配置不同端口。");
        var listening = _dependencies.Listeners().Any(endpoint => endpoint.Port == port &&
            endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
            (allowLan || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.Loopback)));
        if (context.CoreRunning != false)
            return listening
                ? new(id, title, DiagnosticStatus.Passed, "配置地址存在 TCP 监听；未验证进程归属或协议，不将其判定为冲突。")
                : new(id, title, DiagnosticStatus.Warning, "配置地址未发现 TCP 监听。", "等待核心启动完成；如设置有变更，请重启核心后重试。");
        return listening
            ? new(id, title, DiagnosticStatus.Warning, "核心停止时发现 TCP 端口占用；未识别占用进程。", "检查其他程序或核心实例，或在设置页更换端口。")
            : new(id, title, DiagnosticStatus.Passed, "未发现配置地址的 IPv4 TCP 监听；不保证端口可绑定（如系统保留端口）。");
    }

    private DiagnosticResult CheckSystemProxy(NetworkDiagnosticsContext context)
    {
        var proxy = _dependencies.SystemProxy();
        if (proxy.HasPac)
            return new("system-proxy", "系统代理", DiagnosticStatus.Warning, "检测到 PAC 配置，实际代理路径可能被覆盖。", "检查 Windows 自动代理脚本；此诊断不访问 PAC 地址。");
        if (!proxy.Enabled)
            return new("system-proxy", "系统代理", context.SystemProxyExpected && !context.TunEnabled && context.CoreRunning == true
                ? DiagnosticStatus.Warning : DiagnosticStatus.Skipped, "Windows 固定系统代理未开启。",
                context.TunEnabled ? "TUN 模式不要求开启系统代理。" : "需要应用走系统代理时，请启动核心并检查系统代理开关。");
        if (!ProxyMatches(proxy.Server, context.MixedPort))
            return new("system-proxy", "系统代理", DiagnosticStatus.Warning, "系统 HTTP/HTTPS 代理未同时指向配置的本机 Mixed 端口。", "检查 Windows 系统代理地址及 Chorus Mixed 端口。");
        return context.CoreRunning == true
            ? new("system-proxy", "系统代理", DiagnosticStatus.Passed, "系统 HTTP/HTTPS 代理指向配置的本机 Mixed 端口，核心正在运行。")
            : new("system-proxy", "系统代理", DiagnosticStatus.Warning, "系统代理指向配置端口，但核心未运行或状态未知，可能导致断网。", "启动核心，或手动关闭不再使用的系统代理。");
    }

    internal static bool ProxyMatches(string? server, int port)
    {
        if (!ValidPort(port) || string.IsNullOrWhiteSpace(server)) return false;
        bool Matches(string value) => Uri.TryCreate(value.Contains("://") ? value : "http://" + value,
                UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.Port == port &&
            uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1");
        var entries = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 1 && !entries[0].Contains('=')) return Matches(entries[0]);
        var protocols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var pair = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !protocols.TryAdd(pair[0], pair[1])) return false;
        }
        return protocols.TryGetValue("http", out var http) && Matches(http) &&
            protocols.TryGetValue("https", out var https) && Matches(https);
    }

    private async Task<DiagnosticResult> CheckHttpAsync(NetworkDiagnosticsContext context, CancellationToken ct)
    {
        if (!ValidPort(context.MixedPort))
            return new("http", "Mixed HTTP 连通性", DiagnosticStatus.Skipped, "Mixed 端口无效，未发送请求。", "修正 Mixed 端口后重试。");
        if (context.CoreRunning == false)
            return new("http", "Mixed HTTP 连通性", DiagnosticStatus.Skipped, "核心未运行，未向配置端口发送代理请求。", "启动核心后重新诊断。");
        using var http = new HttpClient(_dependencies.ProxyHandler(context.MixedPort)) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(NetworkProbe.LatencyUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.NoContent
            ? new("http", "Mixed HTTP 连通性", DiagnosticStatus.Passed,
                $"经 127.0.0.1:{context.MixedPort} HTTP 代理收到预期 HTTP 204；无直连回退。",
                "仅证明此请求经 Mixed 入口可达；核心规则可能选择直连，不证明远端代理节点或所有网站可用。")
            : new("http", "Mixed HTTP 连通性", DiagnosticStatus.Failed,
                $"经配置代理收到 HTTP {(int)response.StatusCode}，预期 204；无直连回退。", "检查核心、节点和路由规则，以及探测站点可达性。");
    }

    internal static HttpClientHandler CreateProxyHandler(int port) => new()
    {
        UseProxy = true,
        Proxy = new WebProxy($"http://127.0.0.1:{port}") { BypassProxyOnLocal = false },
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false
    };

    private static bool ValidPort(int port) => port is > 0 and <= 65535;

    private static ProxyState ReadSystemProxy()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")
            ?? throw new InvalidOperationException();
        return new(key.GetValue("ProxyEnable") is int value && value != 0,
            key.GetValue("ProxyServer") as string, !string.IsNullOrWhiteSpace(key.GetValue("AutoConfigURL") as string));
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
