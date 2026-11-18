using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PgMonitor.Core.Alarms;
using PgMonitor.Core.Model;
using PgMonitor.Infrastructure.Runtime;

namespace PgMonitor.App.ViewModels;

public sealed record AlarmRowView(string Database, int Blockers, int Waiters, string MaxState, string MaxWait);

/// <summary>One alarm level: its SQL text for the company form and what that SQL would return for the current sample.</summary>
public sealed partial class AlarmTierViewModel : ObservableObject
{
    private readonly AlarmTier _tier;

    [ObservableProperty] private string _status = "-";
    [ObservableProperty] private string _statusKind = "Info"; // Ok | Bad | Info

    public AlarmTierViewModel(AlarmTier tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        _tier = tier;
        Sql = AlarmQuery.Sql(tier);
        Title = string.Create(CultureInfo.InvariantCulture,
            $"{tier.Name}: bekleyen ≥ {(int)tier.WaiterMinWait.TotalSeconds} sn, bloklayan aynı durumda ≥ {(int)tier.BlockerMinState.TotalSeconds} sn");
    }

    public string Title { get; }
    public string Sql { get; }
    public ObservableCollection<AlarmRowView> Rows { get; } = [];

    public void Apply(ClusterSample sample, IReadOnlyDictionary<string, string> apps)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(apps);
        if (!sample.Succeeded)
        {
            Status = "Veri alınamıyor – bu örnek için sorgu değerlendirilemedi";
            StatusKind = "Info";
            return;
        }

        var rows = AlarmQuery.Evaluate(sample, _tier);
        Rows.Clear();
        foreach (var r in rows)
        {
            Rows.Add(new AlarmRowView(Fmt.Db(r.Database, apps), r.Blockers, r.Waiters,
                string.Create(CultureInfo.InvariantCulture, $"{r.MaxStateSeconds} sn"),
                string.Create(CultureInfo.InvariantCulture, $"{r.MaxWaitSeconds} sn")));
        }

        if (rows.Count == 0)
        {
            Status = "Alarm ÇALMAZ – sorgu satır döndürmez (sadece başlıklar gelir)";
            StatusKind = "Ok";
        }
        else
        {
            Status = "ALARM ÇALAR – " + string.Join(", ", rows.Select(r => Fmt.Db(r.Database, apps)));
            StatusKind = "Bad";
        }
    }
}

/// <summary>Shows what the company SQL alarm would do with the sample the application just collected.</summary>
public sealed partial class AlarmPreviewViewModel : ObservableObject
{
    private const string Intro =
        "Bu ekran, şirket alarmına verilecek SQL'in kuralını şu an elimizdeki örnek üzerinde uygular. Veritabanına ek sorgu " +
        "göndermez ve alarm üretmez. Şirket alarmı bu SQL'i kendi servis hesabıyla, kendi zamanlamasıyla çalıştırır. " +
        "Aşağıdaki SQL, formda kullanılacak metindir.";

    [ObservableProperty] private string _note = Intro;

    public AlarmTierViewModel Alarm { get; } = new(AlarmTier.Standard);

    public void Apply(MonitorSnapshot s, IReadOnlyDictionary<string, string> apps)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Sample is not { } sample)
        {
            return;
        }
        Alarm.Apply(sample, apps);
        Note = sample.Succeeded && sample.Visibility != Visibility.Full
            ? Intro + "\n\nKISMİ GÖRÜNÜRLÜK: yetkisiz hesapta gizli kalan session'lar şirket sorgusuna da görünmez; o session'lardaki lock'lar için alarm çalmaz."
            : Intro;
    }
}
