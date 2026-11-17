using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Model;
using PgMonitor.Infrastructure.Runtime;

namespace PgMonitor.App.ViewModels;

/// <summary>Live view of the latest monitoring cycle.</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    [ObservableProperty] private string _status = "Başlatılıyor…";
    [ObservableProperty] private string _statusKind = "Info"; // Ok | Warn | Bad | Info
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string _lastSample = "-";
    [ObservableProperty] private string _sessions = "-";
    [ObservableProperty] private string _lockWaiters = "-";
    [ObservableProperty] private string _overThreshold = "-";
    [ObservableProperty] private string _longestWait = "-";
    [ObservableProperty] private string _visibility = "-";
    [ObservableProperty] private string _storage = "-";
    [ObservableProperty] private string _mail = "-";
    [ObservableProperty] private string _warnings = "";
    [ObservableProperty] private BlockerRow? _selectedBlocker;
    [ObservableProperty] private SessionRowView? _selectedWaiter;
    [ObservableProperty] private LongTxRow? _selectedLongTx;
    [ObservableProperty] private string _longTxCount = "-";
    [ObservableProperty] private string _longTxHeader = "Uzun transaction'lar";

    public ObservableCollection<BlockerRow> Blockers { get; } = [];
    public ObservableCollection<LongTxRow> LongTransactions { get; } = [];
    public ObservableCollection<SessionRowView> Waiters { get; } = [];
    public ObservableCollection<DatabaseRow> Databases { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];

    public void Apply(MonitorSnapshot s, IReadOnlyDictionary<string, string> apps, string storageLine)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(apps);
        var sample = s.Sample;
        var state = s.State;

        if (sample is null)
        {
            return;
        }

        if (!sample.Succeeded)
        {
            Status = "VERİ ALINAMIYOR";
            StatusKind = "Bad";
            StatusDetail = $"{sample.Failure} – {s.CollectError ?? ""} ({s.ConsecutiveFailures} kez üst üste). Bu süre 'düzeldi' sayılmaz.";
        }
        else if (state.Phase != IncidentPhase.Healthy && state.Incident is { } inc)
        {
            Status = state.Phase == IncidentPhase.Recovering
                ? $"OLAY #{inc.Id} – DÜZELİYOR"
                : $"OLAY #{inc.Id} – {Fmt.Severity(inc.Severity).ToUpperInvariant()}";
            StatusKind = state.Phase == IncidentPhase.Recovering ? "Warn" : "Bad";
            StatusDetail = $"Başlangıç {Fmt.Time(inc.OpenedAt)} • veritabanları: {string.Join(", ", inc.Databases.Select(d => Fmt.Db(d, apps)))}";
        }
        else if (sample.Visibility == Core.Model.Visibility.Partial)
        {
            Status = "KISMİ GÖRÜNÜRLÜK";
            StatusKind = "Warn";
            StatusDetail = "Bazı session'ların ayrıntısı yetki nedeniyle gizli; gizli session'lardaki lock'lar görülemez.";
        }
        else
        {
            Status = "SAĞLIKLI";
            StatusKind = "Ok";
            StatusDetail = "30 sn'yi aşan lock beklemesi yok.";
        }

        LastSample = Fmt.Clock(sample.SampledAt);
        Visibility = sample.Succeeded ? Fmt.Visibility(sample.Visibility) : "-";
        Storage = storageLine;
        Mail = s.Mail is { } m ? $"kuyrukta {m.Pending}, gönderildi {m.Sent}, başarısız {m.Failed}" + (s.MailError is null ? "" : $" • son hata: {s.MailError}") : "kapalı";

        var warnings = new List<string>();
        if (s.StorageDegraded)
        {
            warnings.Add($"Kayıt hatası: {s.StorageError}. İzleme sürüyor ama kanıt diske yazılamıyor.");
        }
        if (s.StorageOverCap)
        {
            warnings.Add("Depolama sınırı aşıldı; açık olayların detayı seyreltildi.");
        }
        Warnings = string.Join("\n", warnings);

        if (!sample.Succeeded)
        {
            return; // keep the last good tables on screen
        }

        var now = sample.SampledAt;
        Sessions = sample.Sessions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var eval = s.Lock;
        LockWaiters = (eval?.Analysis.WaiterCount ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        OverThreshold = (eval?.QualifyingWaiters ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        LongestWait = eval is null || eval.Analysis.WaiterCount == 0 ? "-" : IncidentEngine.Format(eval.Analysis.LongestWait);

        Replace(Blockers, eval?.Analysis.RootBlockers.Select(r => BlockerRow.From(r, now, apps)) ?? []);
        Replace(Waiters, eval?.Analysis.Waiters
            .OrderBy(w => w.QueryStart ?? DateTimeOffset.MaxValue)
            .Select(w => SessionRowView.From(w, now, apps)) ?? []);
        Replace(Databases, sample.Sessions
            .GroupBy(r => r.Database ?? "-", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DatabaseRow(
                Fmt.Db(g.Key, apps),
                g.Count(),
                g.Count(r => r.State == "active"),
                g.Count(r => r.IsIdleInTransaction),
                g.Count(r => r.IsWaitingOnLock))));

        LongTxCount = s.LongTransactions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        LongTxHeader = $"Uzun transaction'lar ({s.LongTransactions.Count})";
        var keep = SelectedLongTx?.Pid;
        Replace(LongTransactions, s.LongTransactions.Select(t => LongTxRow.From(t, now, apps)));
        SelectedLongTx = LongTransactions.FirstOrDefault(r => r.Pid == keep);
    }

    /// <summary>"Son olaylar" comes from SQLite, so nothing disappears when monitoring restarts.</summary>
    public void SetEvents(IEnumerable<EventRow> rows) => Replace(Events, rows);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items)
        {
            target.Add(i);
        }
    }
}
