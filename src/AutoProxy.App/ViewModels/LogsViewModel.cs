using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using AutoProxy.Core.Abstractions;
using AutoProxy.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoProxy.App.ViewModels;

public class LogRow
{
    public LogEntry Model { get; init; } = new();

    public string TimeText => Model.Timestamp.ToString("HH:mm:ss");
    public string LevelText => Model.Level.ToString().ToUpperInvariant();
    public string Category => Model.Category;
    public string Message => Model.Message;
    public string? Detail => Model.Detail;

    public Brush LevelBrush => Model.Level switch
    {
        LogLevel.Error => (Brush)Application.Current.FindResource("DangerBrush"),
        LogLevel.Warning => (Brush)Application.Current.FindResource("WarningBrush"),
        _ => (Brush)Application.Current.FindResource("TextSecondaryBrush"),
    };
}

public partial class LogsViewModel : ObservableObject
{
    private readonly ILogService _logService;
    private readonly ILogRepository _logRepo;
    private readonly CancellationTokenSource _cts = new();

    public LogsViewModel(ILogService logService, ILogRepository logRepo)
    {
        _logService = logService;
        _logRepo = logRepo;
        _logService.EntryAdded += OnEntryAdded;
    }

    public ObservableCollection<LogRow> Rows { get; } = new();

    public void RefreshAll()
    {
        _ = LoadRecentAsync();
    }

    private async Task LoadRecentAsync()
    {
        try
        {
            var entries = await _logRepo.GetRecentAsync(500, _cts.Token);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Rows.Clear();
                foreach (var entry in entries.AsEnumerable().Reverse())
                {
                    Rows.Add(new LogRow { Model = entry });
                }
            });
        }
        catch (Exception ex)
        {
            _logService.Warning("Logs", "Could not load log history.", ex.Message);
        }
    }

    private void OnEntryAdded(object? sender, LogEntry entry)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Rows.Add(new LogRow { Model = entry });
            while (Rows.Count > 800)
                Rows.RemoveAt(0);
        });
    }
}