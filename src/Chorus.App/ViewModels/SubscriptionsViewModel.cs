using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Chorus.App.Services;
using Chorus.Core;
using Chorus.Core.Models;

namespace Chorus.App.ViewModels;

/// <summary>订阅管理页的 ViewModel：多订阅链接的增删改、逐个/批量更新。</summary>
public partial class SubscriptionsViewModel : ObservableObject
{
    public ObservableCollection<SubscriptionItemViewModel> Items { get; } = [];

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newUrl = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _manualNodeCount;
    [ObservableProperty] private int _subscriptionNodeCount;
    [ObservableProperty] private int _totalNodeCount;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private string _messageTitle = "";
    [ObservableProperty] private string _messageText = "";
    [ObservableProperty] private InfoBarSeverity _messageSeverity = InfoBarSeverity.Informational;

    public SubscriptionsViewModel() => Reload();

    /// <summary>从设置重建列表（增删改后调用）。</summary>
    public void Reload()
    {
        Ui.Post(() =>
        {
            Items.Clear();
            foreach (var entry in AppServices.Settings.Subscriptions)
                Items.Add(new SubscriptionItemViewModel(entry));
            ManualNodeCount = AppServices.Settings.Profile.Nodes.Count;
            SubscriptionNodeCount = AppServices.Settings.Subscriptions.Sum(s => s.Nodes.Count);
            TotalNodeCount = AppServices.Settings.EffectiveNodes().Count;
        });
    }

    public async Task AddAsync()
    {
        if (IsBusy) return;
        var url = NewUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Show("错误", "订阅链接必须以 http:// 或 https:// 开头", InfoBarSeverity.Error);
            return;
        }
        if (AppServices.Settings.Subscriptions.Any(s =>
                string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            Show("错误", "该订阅链接已存在", InfoBarSeverity.Error);
            return;
        }

        var name = NewName.Trim();
        var entry = new SubscriptionEntry
        {
            Name = name.Length > 0 ? name : SubscriptionEntryConverter.DefaultName(url),
            Url = url,
        };
        AppServices.Settings.Subscriptions.Add(entry);

        IsBusy = true;
        try
        {
            var (ok, message) = await UpdateEntryAsync(entry);
            AppServices.Store.Save(AppServices.Settings);
            NewName = "";
            NewUrl = "";
            var note = ok ? await ApplyAsync() : "";
            Show(
                ok ? "添加成功" : "添加成功但更新失败",
                ok ? $"「{entry.Name}」已添加，解析到 {entry.Nodes.Count} 个节点、{entry.Groups.Count} 个策略组。{Join(message, note)}"
                   : $"「{entry.Name}」已添加，但更新失败：{message}",
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    /// <summary>弹窗面板保存后修改订阅（名称/链接），并立即重新拉取。</summary>
    public async Task EditAsync(SubscriptionItemViewModel item, string name, string url)
    {
        if (IsBusy) return;
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Show("错误", "订阅链接必须以 http:// 或 https:// 开头", InfoBarSeverity.Error);
            return;
        }
        if (AppServices.Settings.Subscriptions.Any(s =>
                !ReferenceEquals(s, item.Entry)
                && string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            Show("错误", "该订阅链接已存在", InfoBarSeverity.Error);
            return;
        }

        var trimmed = name.Trim();
        item.Entry.Name = trimmed.Length > 0 ? trimmed : SubscriptionEntryConverter.DefaultName(url);
        item.Entry.Url = url;
        Reload();

        IsBusy = true;
        try
        {
            var (ok, message) = await UpdateEntryAsync(item.Entry);
            AppServices.Store.Save(AppServices.Settings);
            var note = ok ? await ApplyAsync() : "";
            Show(
                ok ? "保存成功" : "保存成功但更新失败",
                ok ? $"「{item.Entry.Name}」：{item.Entry.Nodes.Count} 个节点、{item.Entry.Groups.Count} 个策略组。{Join(message, note)}"
                   : $"「{item.Entry.Name}」已保存，但更新失败：{message}",
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    public async Task UpdateAsync(SubscriptionItemViewModel item)
    {
        if (IsBusy) return;
        IsBusy = true;
        item.IsUpdating = true;
        try
        {
            var (ok, message) = await UpdateEntryAsync(item.Entry);
            AppServices.Store.Save(AppServices.Settings);
            var note = ok ? await ApplyAsync() : "";
            Show(
                ok ? "更新成功" : "更新失败",
                ok ? $"「{item.Entry.Name}」：{item.Entry.Nodes.Count} 个节点、{item.Entry.Groups.Count} 个策略组。{Join(message, note)}"
                   : message,
                ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            item.IsUpdating = false;
            IsBusy = false;
            Reload();
        }
    }

    public async Task UpdateAllAsync()
    {
        if (IsBusy) return;
        if (Items.Count == 0)
        {
            Show("提示", "还没有添加任何订阅", InfoBarSeverity.Informational);
            return;
        }
        IsBusy = true;
        try
        {
            var okCount = 0;
            var failures = new List<string>();
            foreach (var item in Items.ToList())
            {
                item.IsUpdating = true;
                var (ok, message) = await UpdateEntryAsync(item.Entry);
                if (ok) okCount++;
                else failures.Add($"{item.Entry.Name}：{message}");
                item.IsUpdating = false;
            }
            AppServices.Store.Save(AppServices.Settings);
            var note = failures.Count == 0 ? await ApplyAsync() : "";
            Show(
                failures.Count == 0 ? "全部更新成功" : "部分更新失败",
                failures.Count == 0
                    ? $"{okCount} 个订阅全部更新成功。{Join(AppServices.Subscriptions.LastFetchDetail, note)}"
                    : $"成功 {okCount} 个，失败 {failures.Count} 个。首个错误：{failures[0]}",
                failures.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        {
            IsBusy = false;
            Reload();
        }
    }

    public void Remove(SubscriptionItemViewModel item)
    {
        AppServices.Settings.Subscriptions.Remove(item.Entry);
        AppServices.Store.Save(AppServices.Settings);
        Reload();
        Show("已删除", $"订阅「{item.Entry.Name}」已删除。重启核心后生效。", InfoBarSeverity.Informational);
    }

    /// <summary>下载并解析单个订阅，写回 Entry。返回 (是否成功, 提示信息)。</summary>
    private static async Task<(bool Ok, string Message)> UpdateEntryAsync(SubscriptionEntry entry)
    {
        try
        {
            var (nodes, groups, routing, errors) = await AppServices.Subscriptions.ImportFullAsync(entry.Url);
            entry.Nodes = nodes;
            entry.Groups = groups;
            // 订阅自带的 route/dns 要跟着存下来：它们既决定规则页显示什么，
            // 也决定生成配置时能不能把规则集合并进去
            entry.RouteJson = routing?.RouteJson;
            entry.DnsJson = routing?.DnsJson;
            entry.LastUpdated = DateTimeOffset.Now;
            entry.LastError = errors.Count > 0 ? errors[0] : null;
            var detail = AppServices.Subscriptions.LastFetchDetail;
            var routingNote = routing?.HasAny == true ? "（含订阅路由规则）" : "";
            return (true, detail is null ? routingNote : $"{detail}{routingNote}");
        }
        catch (Exception ex)
        {
            entry.LastError = ex.Message;
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 订阅数据变化后让新配置真正生效：核心在运行时按新配置重启（否则策略组页读到的
    /// 仍是旧配置，看起来就像「策略组没加载」）；核心未运行时提示启动后生效。
    /// </summary>
    private static async Task<string> ApplyAsync()
    {
        if (!AppServices.Runner.IsRunning) return "启动核心后生效。";
        try
        {
            await AppServices.Runner.StopAsync();
            await AppServices.Runner.StartAsync();
            _ = AppServices.ProxiesVm.RefreshAsync();
            return "核心已按新配置重启。";
        }
        catch (Exception ex)
        {
            return $"自动重启核心失败（{ex.Message}），请手动重启核心。";
        }
    }

    /// <summary>把若干条提示拼接成一句（跳过空项），避免文案里出现多余空格或空括号。</summary>
    private static string Join(params string?[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private void Show(string title, string text, InfoBarSeverity severity)
    {
        MessageTitle = title;
        MessageText = text;
        MessageSeverity = severity;
        HasMessage = true;
    }
}

/// <summary>列表中单个订阅的包装（暴露状态文本与更新中标记）。</summary>
public partial class SubscriptionItemViewModel : ObservableObject
{
    public SubscriptionEntry Entry { get; }

    [ObservableProperty] private bool _isUpdating;

    public string Name => Entry.Name;
    public string Url => Entry.Url;

    /// <summary>订阅名首字符，用作卡片图标。</summary>
    public string IconLetter => Name.Length == 0 ? "?" : Name[..1];

    public string StatusText
    {
        get
        {
            if (Entry.LastError is { } error) return $"更新失败：{error}";
            if (Entry.LastUpdated is { } updated)
                return $"{Entry.Nodes.Count} 个节点 · {updated.LocalDateTime:yyyy-MM-dd HH:mm} 更新";
            return "尚未更新";
        }
    }

    /// <summary>列表显示的状态：更新中优先。</summary>
    public string DisplayStatus => IsUpdating ? "正在更新…" : StatusText;

    public SubscriptionItemViewModel(SubscriptionEntry entry) => Entry = entry;

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(DisplayStatus));
}
