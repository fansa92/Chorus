using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chorus.Core.Models;

namespace Chorus.Core.Configuration;

public enum ConfigCheckStatus
{
    /// <summary>sing-box check 通过。</summary>
    Passed,
    /// <summary>配置生成失败或 sing-box check 报错。</summary>
    Failed,
    /// <summary>没有找到核心可执行文件，跳过检查（不阻止保存）。</summary>
    Skipped,
}

/// <summary>一次订阅配置检查的结果。</summary>
public sealed record ConfigCheckReport(
    ConfigCheckStatus Status,
    int NodeCount,
    int GroupCount,
    int RuleCount,
    int RuleSetCount,
    string? Error = null)
{
    /// <summary>没通过检查。</summary>
    public bool Failed => Status == ConfigCheckStatus.Failed;

    /// <summary>可以保存（通过或跳过）。</summary>
    public bool Ok => !Failed;

    public string Summary =>
        $"{NodeCount} 个节点 · {GroupCount} 个策略组 · {RuleCount} 条路由规则 · {RuleSetCount} 个规则集";

    public string Describe() => Status switch
    {
        ConfigCheckStatus.Passed => "配置检查通过（sing-box check）：" + Summary,
        ConfigCheckStatus.Skipped => "已跳过配置检查（未找到 sing-box.exe）：" + Summary,
        _ => "配置检查未通过：" + (Error ?? "未知错误"),
    };
}

/// <summary>
/// 订阅配置检查：把某份订阅当作「当前使用的配置」渲染成 config.json，
/// 交给 sing-box 自带的检查器（sing-box check -c）验证是否可用。
/// 不需要额外组件，也不启动核心、不占端口、不下载规则集——只做静态校验。
/// Bettbox 的做法是把配置热应用到已运行的核心，失败就回滚到上一份配置；
/// sing-box 有独立的 check 子命令，可以在落盘之前就完成同样的「先验证再采用」。
/// </summary>
public static partial class SubscriptionCheck
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// 构造一份「只让这份订阅生效」的临时设置（不改动传入的 settings）。
    /// 手动节点始终参与生成配置，所以一并保留；其余订阅全部排除。
    /// </summary>
    public static AppSettings SettingsFor(AppSettings source, SubscriptionEntry entry) => new()
    {
        MixedPort = source.MixedPort,
        ClashApiPort = source.ClashApiPort,
        ClashApiSecret = source.ClashApiSecret,
        AllowLan = source.AllowLan,
        EnableTun = source.EnableTun,
        EnableSystemProxy = source.EnableSystemProxy,
        CloseToTray = source.CloseToTray,
        LogLevel = source.LogLevel,
        SubscriptionUserAgent = source.SubscriptionUserAgent,
        Subscriptions = [entry],
        ActiveSubscriptionUrl = entry.Url,
        ApplySubscriptionRouting = source.ApplySubscriptionRouting,
        Profile = source.Profile,
    };

    /// <summary>渲染这份订阅的配置，并给出可显示在界面上的规模统计。</summary>
    public static (string Json, int Nodes, int Groups, int Rules, int RuleSets) BuildJson(
        AppSettings source, SubscriptionEntry entry)
    {
        var (config, _) = ConfigGenerator.BuildWithReport(SettingsFor(source, entry));
        var route = config["route"] as JsonObject;
        var rules = (route?["rules"] as JsonArray)?.Count ?? 0;
        var ruleSets = (route?["rule_set"] as JsonArray)?.Count ?? 0;

        return (config.ToJsonString(WriteOptions), entry.Nodes.Count, entry.Groups.Count, rules, ruleSets);
    }

    /// <summary>
    /// 检查某份订阅生成的配置是否可用。
    /// <paramref name="checkConfig"/> 通常是「用 sing-box check 校验这个临时文件」；
    /// 传 null 表示机器上没有核心，直接记为 Skipped（不阻止保存）。
    /// </summary>
    public static async Task<ConfigCheckReport> RunAsync(
        AppSettings source,
        SubscriptionEntry entry,
        Func<string, Task>? checkConfig,
        CancellationToken ct = default)
    {
        (string Json, int Nodes, int Groups, int Rules, int RuleSets) built;
        try
        {
            built = BuildJson(source, entry);
        }
        catch (Exception ex)
        {
            return new ConfigCheckReport(ConfigCheckStatus.Failed, entry.Nodes.Count, entry.Groups.Count, 0, 0,
                Clean(ex.Message));
        }

        if (checkConfig is null)
            return new ConfigCheckReport(ConfigCheckStatus.Skipped, built.Nodes, built.Groups, built.Rules, built.RuleSets);

        string? path = null;
        try
        {
            path = Path.Combine(Path.GetTempPath(), $"chorus-check-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(path, built.Json, ct);
            await checkConfig(path);
            return new ConfigCheckReport(ConfigCheckStatus.Passed, built.Nodes, built.Groups, built.Rules, built.RuleSets);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConfigCheckReport(ConfigCheckStatus.Failed, built.Nodes, built.Groups, built.Rules, built.RuleSets,
                Clean(ex.Message));
        }
        finally
        {
            try
            {
                if (path is not null && File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 临时文件删不掉不影响结论
            }
        }
    }

    /// <summary>
    /// 把 sing-box 的原始报错整理成一句能放在卡片上的话：
    /// 去掉「配置校验失败：」包装、`FATAL[0000]` 前缀和临时文件路径，最多保留三行。
    /// </summary>
    public static string Clean(string raw)
    {
        var text = raw.Replace("配置校验失败：", "", StringComparison.Ordinal).Trim();
        text = LevelPrefixRegex().Replace(text, "");
        text = TempPathRegex().Replace(text, "");
        var lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(3);
        var joined = string.Join("；", lines);
        return joined.Length <= 400 ? joined : joined[..400] + "…";
    }

    [GeneratedRegex(@"(?m)^(FATAL|ERROR|WARN|INFO|DEBUG)\[\d+\]\s*")]
    private static partial Regex LevelPrefixRegex();

    /// <summary>我们的临时文件是 .json，路径里带盘符冒号，必须用 <c>.*?\.json:</c> 才能整段吃掉。</summary>
    [GeneratedRegex(@"decode config at .*?\.json:\s*")]
    private static partial Regex TempPathRegex();
}
