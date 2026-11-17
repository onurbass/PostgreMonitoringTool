using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.App.ViewModels;

/// <summary>History of transactions that stayed open longer than the threshold (from SQLite).</summary>
public sealed partial class LongTransactionsViewModel(Func<IMonitorStore?> store, Func<IReadOnlyDictionary<string, string>> apps) : ObservableObject
{
    [ObservableProperty] private LongTxRow? _selected;
    [ObservableProperty] private string _summary = "";

    public ObservableCollection<LongTxRow> Items { get; } = [];

    [RelayCommand]
    public void Refresh()
    {
        var keep = Selected;
        Items.Clear();
        var list = store()?.ListLongTransactions() ?? [];
        foreach (var t in list)
        {
            Items.Add(LongTxRow.From(t, apps()));
        }
        Summary = $"{list.Count} kayıt (en yeni üstte). Süreler, transaction'ın son görüldüğü ana göredir.";
        Selected = keep is null ? null : Items.FirstOrDefault(i => i.Pid == keep.Pid && i.FirstSeen == keep.FirstSeen);
    }
}
