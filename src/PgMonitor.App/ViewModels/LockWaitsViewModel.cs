using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.App.ViewModels;

/// <summary>History of every recorded lock wait (from SQLite), including short ones that opened no incident.</summary>
public sealed partial class LockWaitsViewModel(Func<IMonitorStore?> store, Func<IReadOnlyDictionary<string, string>> apps) : ObservableObject
{
    [ObservableProperty] private LockWaitRow? _selected;
    [ObservableProperty] private string _summary = "";

    public ObservableCollection<LockWaitRow> Items { get; } = [];

    [RelayCommand]
    public void Refresh()
    {
        var keep = Selected;
        Items.Clear();
        var list = store()?.ListLockWaits() ?? [];
        foreach (var w in list)
        {
            Items.Add(LockWaitRow.From(w, apps()));
        }
        Summary = $"{list.Count} kayıt (en yeni üstte, en fazla 1000 gösterilir).";
        Selected = keep is null ? null : Items.FirstOrDefault(i => i.Pid == keep.Pid && i.FirstSeen == keep.FirstSeen);
    }
}
