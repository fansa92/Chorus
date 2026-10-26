using System.Collections.Concurrent;

namespace Chorus.App.Services;

/// <summary>
/// 界面语言：跟随系统 / 简体中文 / English。
/// <para>
/// WinUI 3 的 unpackaged 应用不走 .resw + PRI 那套资源流水线（那套要靠
/// MakePri.exe 打包），所以这里用一张轻量的字符串表：<see cref="Loc.T"/> 按 key 取当前语言的文本，
/// ViewModel 暴露成可绑定属性，切换语言时统一刷新。
/// </para>
/// </summary>
public static class Loc
{
    /// <summary>可选值：system / zh-Hans / en-US。</summary>
    public static readonly string[] Options = ["system", "zh-Hans", "en-US"];

    private static readonly ConcurrentDictionary<string, string> ZhHans = new(StringComparer.Ordinal)
    {
        // ---- 导航 ----
        ["nav.dashboard"] = "仪表盘",
        ["nav.proxies"] = "策略组",
        ["nav.rules"] = "规则",
        ["nav.subscriptions"] = "订阅",
        ["nav.logs"] = "日志",
        ["nav.settings"] = "设置",

        // ---- 通用 ----
        ["common.refresh"] = "刷新",
        ["common.save"] = "保存",
        ["common.cancel"] = "取消",
        ["common.confirm"] = "确定",
        ["common.close"] = "关闭",
        ["common.search"] = "搜索",
        ["common.retry"] = "重试",
        ["common.none"] = "无",
        ["common.enabled"] = "已开启",
        ["common.disabled"] = "已关闭",

        // ---- 仪表盘 ----
        ["dash.title"] = "仪表盘",
        ["dash.run"] = "运行",
        ["dash.traffic"] = "流量统计",
        ["dash.traffic.perSecond"] = "每秒刷新",
        ["dash.traffic.download"] = "下载",
        ["dash.traffic.upload"] = "上传",
        ["dash.memory"] = "内核内存",
        ["dash.memory.caption"] = "核心进程工作集 · 每 2 秒采样",
        ["dash.network"] = "网络检测",
        ["dash.network.redetect"] = "重新检测",
        ["dash.network.latency"] = "延迟",
        ["dash.network.detectTime"] = "检测时间",
        ["dash.network.ip"] = "出口 IP",
        ["dash.network.local"] = "内网信息",
        ["dash.network.local.caption"] = "本机网卡地址",
        ["dash.network.local.ipv4"] = "IPv4",
        ["dash.network.local.ipv6"] = "IPv6",
        ["dash.network.local.none"] = "未检测到网卡地址",
        ["dash.mode"] = "出站模式",
        ["dash.mode.rule"] = "规则（按分流规则）",
        ["dash.mode.global"] = "全局（全部走代理）",
        ["dash.mode.direct"] = "直连（全部不走代理）",
        ["dash.connections"] = "活动连接",
        ["dash.connections.unit"] = "个连接正在活动",
        ["dash.connections.closeAll"] = "断开全部",
        ["dash.quick"] = "快捷开关",
        ["dash.quick.systemProxy"] = "系统代理",
        ["dash.quick.tun"] = "TUN 模式",
        ["dash.quick.allowLan"] = "允许局域网",

        // ---- 策略组 ----
        ["proxies.title"] = "策略组",
        ["proxies.testAll"] = "全部测速",
        ["proxies.testGroup"] = "测速本组",
        ["proxies.search"] = "搜索节点名称或策略组…",
        ["proxies.custom"] = "自定义",
        ["proxies.custom.title"] = "策略组自定义",
        ["proxies.custom.hint"] = "关掉的策略组不会写进配置，引用它的路由规则也会被剔除。",
        ["proxies.type.selector"] = "手动选择",
        ["proxies.type.urltest"] = "自动测速",
        ["proxies.type.fallback"] = "故障转移",
        ["proxies.badge.manual"] = "手动",
        ["proxies.badge.auto"] = "自动",
        ["proxies.node.count"] = "{0} 个节点",
        ["proxies.auto.reset"] = "交还内核自动选择",

        // ---- 策略组设置（参考 Bettbox 代理页 ProxiesSetting）----
        ["proxies.settings"] = "设置",
        ["proxies.settings.title"] = "策略组显示设置",
        ["proxies.settings.hint"] = "只影响策略组的展示方式，不写进代理配置，改完立刻生效。",
        ["proxies.settings.style"] = "风格",
        ["proxies.settings.sort"] = "排序",
        ["proxies.settings.layout"] = "布局",
        ["proxies.settings.size"] = "尺寸",
        ["proxies.settings.iconStyle"] = "图标样式",
        ["proxies.style.tab"] = "标签页",
        ["proxies.style.tab.sub"] = "一次显示一个策略组",
        ["proxies.style.list"] = "列表",
        ["proxies.style.list.sub"] = "所有策略组纵向排列",
        ["proxies.sort.none"] = "默认",
        ["proxies.sort.none.sub"] = "按核心返回的顺序",
        ["proxies.sort.delay"] = "延迟",
        ["proxies.sort.delay.sub"] = "快的节点排前面，未测速的排最后",
        ["proxies.sort.name"] = "名称",
        ["proxies.sort.name.sub"] = "按节点名称排序",
        ["proxies.layout.tight"] = "紧凑",
        ["proxies.layout.tight.sub"] = "间距更小，一屏看更多节点",
        ["proxies.layout.standard"] = "标准",
        ["proxies.layout.standard.sub"] = "默认间距",
        ["proxies.layout.loose"] = "宽松",
        ["proxies.layout.loose.sub"] = "间距更大，更透气",
        ["proxies.size.expand"] = "展开",
        ["proxies.size.expand.sub"] = "卡片更高，显示更多信息",
        ["proxies.size.shrink"] = "标准",
        ["proxies.size.shrink.sub"] = "默认高度",
        ["proxies.size.min"] = "最小",
        ["proxies.size.min.sub"] = "卡片更矮，尽量多排几个",
        ["proxies.icon.standard"] = "标准",
        ["proxies.icon.standard.sub"] = "协议徽标 + 延迟",
        ["proxies.icon.none"] = "无图标",
        ["proxies.icon.none.sub"] = "只显示节点名称",
        ["proxies.icon.iconOnly"] = "仅图标",
        ["proxies.icon.iconOnly.sub"] = "只显示图标，名称用悬浮提示",
        ["proxies.sticky"] = "组头自动吸顶",
        ["proxies.sticky.sub"] = "列表滚动时当前组名固定在顶部",
        ["proxies.hidden"] = "显示已关闭的策略组",
        ["proxies.hidden.sub"] = "在「自定义」里关掉的组也会出现在列表里",

        // ---- 订阅 ----
        ["subs.title"] = "订阅",
        ["subs.add"] = "添加订阅",
        ["subs.updateAll"] = "全部更新",
        ["subs.edit"] = "编辑订阅",
        ["subs.delete"] = "删除订阅",
        ["subs.check"] = "检查配置",

        // ---- 设置 ----
        ["settings.title"] = "设置",
        ["settings.hint"] = "改动即时保存，不需要再点保存。",
        ["settings.section.network"] = "网络",
        ["settings.section.subscription"] = "订阅与节点",
        ["settings.section.appearance"] = "外观",
        ["settings.section.other"] = "其他",
        ["settings.mixedPort"] = "混合代理端口",
        ["settings.mixedPort.sub"] = "HTTP 与 SOCKS 共用同一个端口",
        ["settings.clashApiPort"] = "Clash API 端口",
        ["settings.clashApiPort.sub"] = "策略组、规则与延迟测速都走这个接口",
        ["settings.allowLan"] = "允许局域网连接",
        ["settings.allowLan.sub"] = "别的设备可以用本机 IP + 混合端口走代理（重启核心后生效）",
        ["settings.systemProxy"] = "启动后自动设置系统代理",
        ["settings.systemProxy.sub"] = "开着开关会立即改系统代理；TUN 模式下自动让位",
        ["settings.tun"] = "启用 TUN 模式",
        ["settings.tun.sub"] = "接管全部流量，需要以管理员身份运行（重启核心后生效）",
        ["settings.ipv6"] = "启用 IPv6",
        ["settings.ipv6.sub"] = "关闭后 DNS 只解析 IPv4 地址（重启核心后生效）",
        ["settings.userAgent"] = "拉取订阅时的 User-Agent",
        ["settings.userAgent.sub"] = "转换服务按 UA 决定返回格式：sing-box 给原生 JSON（含策略组），clash-verge 给 Clash YAML",
        ["settings.routing"] = "使用订阅自带的分流规则",
        ["settings.routing.sub"] = "把订阅里的路由规则、规则集与 DNS 合并进配置；关闭则只用内置的规则/全局/直连",
        ["settings.manualNodes"] = "手动添加的节点",
        ["settings.manualNodes.sub"] = "粘贴分享链接导入，与订阅节点合并生效",
        ["settings.logLevel"] = "日志等级",
        ["settings.logLevel.sub"] = "等级越低越吵，排查路由问题时用 debug",
        ["settings.closeToTray"] = "关闭窗口时最小化到托盘",
        ["settings.closeToTray.sub"] = "关掉则直接退出程序并停止代理",
        ["settings.theme"] = "界面主题",
        ["settings.theme.sub"] = "浅色、深色或跟随系统的深浅色设置",
        ["settings.language"] = "界面语言",
        ["settings.language.sub"] = "立即生效，无需重启",
        ["settings.footer.storage"] = "设置保存在 %LOCALAPPDATA%\\Chorus\\settings.json，不在安装目录里。",
    };

    private static readonly ConcurrentDictionary<string, string> EnUs = new(StringComparer.Ordinal)
    {
        ["nav.dashboard"] = "Dashboard",
        ["nav.proxies"] = "Proxies",
        ["nav.rules"] = "Rules",
        ["nav.subscriptions"] = "Subscriptions",
        ["nav.logs"] = "Logs",
        ["nav.settings"] = "Settings",

        ["common.refresh"] = "Refresh",
        ["common.save"] = "Save",
        ["common.cancel"] = "Cancel",
        ["common.confirm"] = "OK",
        ["common.close"] = "Close",
        ["common.search"] = "Search",
        ["common.retry"] = "Retry",
        ["common.none"] = "None",
        ["common.enabled"] = "On",
        ["common.disabled"] = "Off",

        ["dash.title"] = "Dashboard",
        ["dash.run"] = "Run",
        ["dash.traffic"] = "Traffic",
        ["dash.traffic.perSecond"] = "Refreshes every second",
        ["dash.traffic.download"] = "Download",
        ["dash.traffic.upload"] = "Upload",
        ["dash.memory"] = "Core Memory",
        ["dash.memory.caption"] = "Core process working set · sampled every 2s",
        ["dash.network"] = "Network",
        ["dash.network.redetect"] = "Re-check",
        ["dash.network.latency"] = "Latency",
        ["dash.network.detectTime"] = "Checked at",
        ["dash.network.ip"] = "Exit IP",
        ["dash.network.local"] = "Local Network",
        ["dash.network.local.caption"] = "Addresses of this machine",
        ["dash.network.local.ipv4"] = "IPv4",
        ["dash.network.local.ipv6"] = "IPv6",
        ["dash.network.local.none"] = "No network interface address found",
        ["dash.mode"] = "Outbound Mode",
        ["dash.mode.rule"] = "Rule (by routing rules)",
        ["dash.mode.global"] = "Global (all via proxy)",
        ["dash.mode.direct"] = "Direct (all bypass proxy)",
        ["dash.connections"] = "Active Connections",
        ["dash.connections.unit"] = "connections active",
        ["dash.connections.closeAll"] = "Close All",
        ["dash.quick"] = "Quick Toggles",
        ["dash.quick.systemProxy"] = "System proxy",
        ["dash.quick.tun"] = "TUN mode",
        ["dash.quick.allowLan"] = "Allow LAN",

        ["proxies.title"] = "Proxies",
        ["proxies.testAll"] = "Test All",
        ["proxies.testGroup"] = "Test Group",
        ["proxies.search"] = "Search node or group…",
        ["proxies.custom"] = "Customize",
        ["proxies.custom.title"] = "Customize Groups",
        ["proxies.custom.hint"] = "Disabled groups are left out of the config, and rules referencing them are dropped.",
        ["proxies.type.selector"] = "Manual",
        ["proxies.type.urltest"] = "Auto Test",
        ["proxies.type.fallback"] = "Fallback",
        ["proxies.badge.manual"] = "Manual",
        ["proxies.badge.auto"] = "Auto",
        ["proxies.node.count"] = "{0} nodes",
        ["proxies.auto.reset"] = "Let the core choose automatically",

        // ---- Proxies display settings (mirrors Bettbox's ProxiesSetting) ----
        ["proxies.settings"] = "Settings",
        ["proxies.settings.title"] = "Proxies Display Settings",
        ["proxies.settings.hint"] = "Affects display only; nothing is written to the proxy config. Changes apply instantly.",
        ["proxies.settings.style"] = "Style",
        ["proxies.settings.sort"] = "Sort",
        ["proxies.settings.layout"] = "Layout",
        ["proxies.settings.size"] = "Size",
        ["proxies.settings.iconStyle"] = "Icon Style",
        ["proxies.style.tab"] = "Tabs",
        ["proxies.style.tab.sub"] = "One group at a time",
        ["proxies.style.list"] = "List",
        ["proxies.style.list.sub"] = "All groups stacked vertically",
        ["proxies.sort.none"] = "Default",
        ["proxies.sort.none.sub"] = "Order returned by the core",
        ["proxies.sort.delay"] = "Delay",
        ["proxies.sort.delay.sub"] = "Fastest first, untested last",
        ["proxies.sort.name"] = "Name",
        ["proxies.sort.name.sub"] = "Sorted by node name",
        ["proxies.layout.tight"] = "Tight",
        ["proxies.layout.tight.sub"] = "Less spacing, more nodes per screen",
        ["proxies.layout.standard"] = "Standard",
        ["proxies.layout.standard.sub"] = "Default spacing",
        ["proxies.layout.loose"] = "Loose",
        ["proxies.layout.loose.sub"] = "More breathing room",
        ["proxies.size.expand"] = "Expand",
        ["proxies.size.expand.sub"] = "Taller cards with more detail",
        ["proxies.size.shrink"] = "Standard",
        ["proxies.size.shrink.sub"] = "Default height",
        ["proxies.size.min"] = "Minimal",
        ["proxies.size.min.sub"] = "Shorter cards, fits more",
        ["proxies.icon.standard"] = "Standard",
        ["proxies.icon.standard.sub"] = "Protocol badge + delay",
        ["proxies.icon.none"] = "No icon",
        ["proxies.icon.none.sub"] = "Node name only",
        ["proxies.icon.iconOnly"] = "Icon only",
        ["proxies.icon.iconOnly.sub"] = "Icons only, name in tooltip",
        ["proxies.sticky"] = "Sticky group header",
        ["proxies.sticky.sub"] = "Keep the current group name pinned on scroll",
        ["proxies.hidden"] = "Show disabled groups",
        ["proxies.hidden.sub"] = "Also list groups turned off in Customize",

        ["subs.title"] = "Subscriptions",
        ["subs.add"] = "Add Subscription",
        ["subs.updateAll"] = "Update All",
        ["subs.edit"] = "Edit Subscription",
        ["subs.delete"] = "Delete Subscription",
        ["subs.check"] = "Check Config",

        ["settings.title"] = "Settings",
        ["settings.hint"] = "Changes are saved instantly — no save button needed.",
        ["settings.section.network"] = "Network",
        ["settings.section.subscription"] = "Subscriptions & Nodes",
        ["settings.section.appearance"] = "Appearance",
        ["settings.section.other"] = "Other",
        ["settings.mixedPort"] = "Mixed proxy port",
        ["settings.mixedPort.sub"] = "HTTP and SOCKS share this port",
        ["settings.clashApiPort"] = "Clash API port",
        ["settings.clashApiPort.sub"] = "Groups, rules and latency tests all use this API",
        ["settings.allowLan"] = "Allow LAN connections",
        ["settings.allowLan.sub"] = "Other devices can use this IP + mixed port (restart core to apply)",
        ["settings.systemProxy"] = "Set system proxy on startup",
        ["settings.systemProxy.sub"] = "Takes effect immediately; yields to TUN mode",
        ["settings.tun"] = "Enable TUN mode",
        ["settings.tun.sub"] = "Takes over all traffic, needs administrator rights (restart core to apply)",
        ["settings.ipv6"] = "Enable IPv6",
        ["settings.ipv6.sub"] = "When off, DNS only resolves IPv4 addresses (restart core to apply)",
        ["settings.userAgent"] = "User-Agent for subscription fetch",
        ["settings.userAgent.sub"] = "The converter decides the format by UA: sing-box returns native JSON with groups, clash-verge returns Clash YAML",
        ["settings.routing"] = "Use subscription routing rules",
        ["settings.routing.sub"] = "Merge the subscription's rules, rule sets and DNS; off keeps only the built-in rule/global/direct",
        ["settings.manualNodes"] = "Manually added nodes",
        ["settings.manualNodes.sub"] = "Paste share links to import; merged with subscription nodes",
        ["settings.logLevel"] = "Log level",
        ["settings.logLevel.sub"] = "Lower is noisier; use debug when troubleshooting routing",
        ["settings.closeToTray"] = "Minimize to tray on close",
        ["settings.closeToTray.sub"] = "Off means the app exits and stops the proxy",
        ["settings.theme"] = "Theme",
        ["settings.theme.sub"] = "Light, dark, or follow the system setting",
        ["settings.language"] = "Language",
        ["settings.language.sub"] = "Applies immediately, no restart needed",
        ["settings.footer.storage"] = "Settings are stored in %LOCALAPPDATA%\\Chorus\\settings.json, not in the install folder.",
    };

    /// <summary>当前语言设置值（system / zh-Hans / en-US）。</summary>
    public static string Current { get; private set; } = "system";

    /// <summary>语言切换时触发，ViewModel 收到后刷新所有可绑定文本。</summary>
    public static event Action? LanguageChanged;

    /// <summary>实际生效的语言：跟随系统时按 Windows 的首选语言判断。</summary>
    public static string Resolved =>
        Current switch
        {
            "zh-Hans" => "zh-Hans",
            "en-US" => "en-US",
            _ => SystemLanguageIsChinese() ? "zh-Hans" : "en-US",
        };

    /// <summary>Windows 当前语言是不是中文。取不到就按中文处理（界面默认是中文）。</summary>
    private static bool SystemLanguageIsChinese()
    {
        try
        {
            var name = Windows.Globalization.ApplicationLanguages.Languages.FirstOrDefault();
            return !string.IsNullOrEmpty(name) && name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>切换语言（"system" / "zh-Hans" / "en-US"）。</summary>
    public static void SetLanguage(string value)
    {
        Current = string.IsNullOrWhiteSpace(value) ? "system" : value;
        LanguageChanged?.Invoke();
    }

    /// <summary>取当前语言的文本；缺 key 时退回简体中文，再缺就返回 key 本身。</summary>
    public static string T(string key)
    {
        var table = Resolved == "en-US" ? EnUs : ZhHans;
        if (table.TryGetValue(key, out var text)) return text;
        return ZhHans.TryGetValue(key, out var fallback) ? fallback : key;
    }

    /// <summary>取带占位符的文本，如 T("proxies.node.count", 12)。</summary>
    public static string T(string key, params object[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }
}
