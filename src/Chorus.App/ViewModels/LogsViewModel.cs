using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Chorus.App.Services;
using Chorus.Core.ClashApi;

namespace Chorus.App.ViewModels;

public partial class LogsViewModel : ObservableObject
{
    private const int MaxLines = 2000;

    public ObservableCollection<LogLine> Lines { get; } = [];

    public LogsViewModel()
    {
        AppServices.Runner.LogReceived += entry => Ui.Post(() =>
        {
            Lines.Add(new LogLine(
                entry.Time.LocalDateTime.ToString("HH:mm:ss"),
                entry.Level,
                entry.Message));
            if (Lines.Count > MaxLines)
                Lines.RemoveAt(0);
        });
    }

    public void Clear() => Lines.Clear();
}

public sealed class LogLine
{
    public string Time { get; }
    public string Level { get; }
    public string Message { get; }

    public LogLine(string time, string level, string message)
    {
        Time = time;
        Level = level;
        Message = message;
    }
}
