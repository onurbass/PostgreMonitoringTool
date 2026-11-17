using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PgMonitor.Core.Incidents;
using PgMonitor.Core.Locks;
using PgMonitor.Infrastructure.Storage;

namespace PgMonitor.App.ViewModels;

public sealed record IncidentRow(long Id, string Opened, string Closed, string Duration, string Status, string Severity, string Databases, int Samples);

public sealed record SampleRow(StoredSample Sample, string Time, string Phase, string Summary);

/// <summary>Incident history from SQLite: list, timeline, per-sample sessions and the lock chain at that moment.</summary>
public sealed partial class IncidentsViewModel(Func<IMonitorStore?> store, Func<IReadOnlyDictionary<string, string>> apps) : ObservableObject
{
    [ObservableProperty] private IncidentRow? _selectedIncident;
    [ObservableProperty] private SampleRow? _selectedSample;
    [ObservableProperty] private SessionRowView? _selectedSession;
    [ObservableProperty] private string _detailHeader = "Bir olay seçin.";
    [ObservableProperty] private string _sampleHeader = "";
    [ObservableProperty] private string _sqlText = "";

    public ObservableCollection<IncidentRow> Incidents { get; } = [];
    public ObservableCollection<EventRow> Events { get; } = [];
    public ObservableCollection<SampleRow> Samples { get; } = [];
    public ObservableCollection<BlockerRow> Blockers { get; } = [];
    public ObservableCollection<SessionRowView> Sessions { get; } = [];

    [RelayCommand]
    public void Refresh()
    {
        var s = store();
        var selected = SelectedIncident?.Id;
        Incidents.Clear();
        if (s is null)
        {
            return;
        }
        foreach (var i in s.ListIncidents())
        {
            var end = i.ClosedAt;
            Incidents.Add(new IncidentRow(
                i.Id,
                Fmt.Time(i.OpenedAt),
                Fmt.Time(end),
                end is null ? "sürüyor" : IncidentEngine.Format(end.Value - i.OpenedAt),
                StatusText(i.Status),
                Fmt.Severity(i.Severity),
                string.Join(", ", i.Databases.Select(d => Fmt.Db(d, apps()))),
                i.SampleCount));
        }
        if (selected is not null)
        {
            SelectedIncident = Incidents.FirstOrDefault(i => i.Id == selected);
        }
    }

    partial void OnSelectedIncidentChanged(IncidentRow? value)
    {
        Events.Clear();
        Samples.Clear();
        Blockers.Clear();
        Sessions.Clear();
        SqlText = "";
        if (value is null || store()?.GetIncident(value.Id) is not { } detail)
        {
            DetailHeader = "Bir olay seçin.";
            return;
        }
        DetailHeader = $"Olay #{value.Id} • {value.Status} • {value.Severity} • {value.Opened} → {value.Closed} • {value.Databases}";
        foreach (var e in detail.Events)
        {
            Events.Add(new EventRow(Fmt.Time(e.At), Fmt.Event(e.Kind), Fmt.Severity(e.Severity), "", e.Detail));
        }
        foreach (var x in detail.Samples)
        {
            var waiters = x.Sessions.Count(r => r.IsWaitingOnLock);
            var summary = x.Failure is { } f
                ? $"veri alınamadı ({f})"
                : string.Create(CultureInfo.InvariantCulture, $"{waiters} lock bekleyen, {x.Sessions.Count} kayıtlı session") + (x.RedactedRows > 0 ? $", {x.RedactedRows} gizli" : "");
            Samples.Add(new SampleRow(x, Fmt.Time(x.At), PhaseText(x.Phase), summary));
        }
        // Start at the worst moment: the sample with the most lock waiters.
        SelectedSample = Samples.OrderByDescending(r => r.Sample.Sessions.Count(s => s.IsWaitingOnLock)).ThenBy(r => r.Sample.At).FirstOrDefault();
    }

    partial void OnSelectedSampleChanged(SampleRow? value)
    {
        Blockers.Clear();
        Sessions.Clear();
        SqlText = "";
        if (value is null)
        {
            SampleHeader = "";
            return;
        }
        var map = apps();
        var x = value.Sample;
        var analysis = LockGraph.Analyze(x.Sessions, x.At);
        SampleHeader = $"{value.Time} ({value.Phase}) – {value.Summary}";
        foreach (var r in analysis.RootBlockers)
        {
            Blockers.Add(BlockerRow.From(r, x.At, map));
        }
        foreach (var r in x.Sessions.OrderByDescending(r => r.IsWaitingOnLock).ThenBy(r => r.TransactionStart ?? DateTimeOffset.MaxValue))
        {
            Sessions.Add(SessionRowView.From(r, x.At, map));
        }
    }

    partial void OnSelectedSessionChanged(SessionRowView? value) => SqlText = value?.FullSql ?? "";

    private static string StatusText(IncidentStatus s) => s switch
    {
        IncidentStatus.Active => "Aktif",
        IncidentStatus.Recovering => "Düzeliyor",
        IncidentStatus.Closed => "Kapandı",
        IncidentStatus.Interrupted => "Yarıda kaldı",
        _ => s.ToString(),
    };

    private static string PhaseText(SamplePhase p) => p switch
    {
        SamplePhase.Pre => "olay öncesi",
        SamplePhase.Active => "aktif",
        SamplePhase.Recovering => "düzelme",
        _ => p.ToString(),
    };
}
