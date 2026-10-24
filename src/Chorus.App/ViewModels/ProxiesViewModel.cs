using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core.ClashApi;

namespace Chorus.App.ViewModels;

public partial class ProxiesViewModel : ObservableObject
{
    public ObservableCollection<ProxyGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private string _statusText = "";

    public async Task RefreshAsync()
    {
        if (!AppServices.Runner.IsRunning)
        {
            Ui.Post(() => StatusText = "核心未运行");
            return;
        }

        try
        {
            var proxies = await AppServices.Clash.GetProxiesAsync();
            var groups = proxies.Values
                .Where(p => p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase)
                         || p.Type.Equals("URLTest", StringComparison.OrdinalIgnoreCase)
                         || p.Type.Equals("Fallback", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            Ui.Post(() =>
            {
                Groups.Clear();
                foreach (var group in groups)
                {
                    var gvm = new ProxyGroupViewModel(group.Name, group.Type)
                    {
                        SelectedName = group.Now ?? "",
                    };
                    foreach (var member in group.All)
                        gvm.Members.Add(new ProxyMemberViewModel(gvm, member)
                        {
                            IsSelected = member == group.Now,
                        });
                    Groups.Add(gvm);
                }
                StatusText = $"已加载 {Groups.Count} 个策略组";
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => StatusText = "刷新失败：" + ex.Message);
        }
    }

    /// <summary>在策略组里选中一个成员（节点或嵌套的策略组）。</summary>
    public async Task SelectAsync(ProxyGroupViewModel group, ProxyMemberViewModel member)
    {
        if (group.SelectedName == member.Name) return;
        try
        {
            await AppServices.Clash.SelectAsync(group.Name, member.Name);
            Ui.Post(() =>
            {
                group.SelectedName = member.Name;
                foreach (var m in group.Members)
                    m.IsSelected = m.Name == member.Name;
                StatusText = $"{group.Name} → {member.Name}";
            });
        }
        catch (Exception ex)
        {
            Ui.Post(() => StatusText = "切换失败：" + ex.Message);
        }
    }

    public async Task TestGroupDelayAsync(ProxyGroupViewModel group)
    {
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = group.Members.Select(async member =>
        {
            await gate.WaitAsync();
            try
            {
                var delay = await AppServices.Clash.GetDelayAsync(member.Name);
                Ui.Post(() => member.DelayText = delay is int ms ? $"{ms} ms" : "超时");
            }
            catch
            {
                Ui.Post(() => member.DelayText = "失败");
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    public async Task TestAllGroupsAsync()
    {
        foreach (var group in Groups.ToList())
            await TestGroupDelayAsync(group);
    }
}

public partial class ProxyGroupViewModel : ObservableObject
{
    public string Name { get; }
    public string TypeText { get; }

    [ObservableProperty] private string _selectedName = "";

    public ObservableCollection<ProxyMemberViewModel> Members { get; } = [];

    public ProxyGroupViewModel(string name, string type)
    {
        Name = name;
        TypeText = type switch
        {
            "Selector" or "selector" => "手动选择",
            "URLTest" or "urltest" => "自动测速",
            "Fallback" => "故障转移",
            _ => type,
        };
    }
}

public partial class ProxyMemberViewModel : ObservableObject
{
    public ProxyGroupViewModel Group { get; }
    public string Name { get; }
    public string GroupName => Group.Name;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _delayText = "";

    public string DisplayText => DelayText.Length == 0 ? Name : $"{Name} ({DelayText})";

    public ProxyMemberViewModel(ProxyGroupViewModel group, string name)
    {
        Group = group;
        Name = name;
    }

    partial void OnDelayTextChanged(string value) => OnPropertyChanged(nameof(DisplayText));
}
