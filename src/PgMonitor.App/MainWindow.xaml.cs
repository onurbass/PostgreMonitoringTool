using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PgMonitor.App.ViewModels;

namespace PgMonitor.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>True while the user really wants to quit (tray menu); otherwise closing only hides to the tray.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide(); // monitoring continues in the background
        }
        base.OnClosing(e);
    }

    // Passwords go straight from the PasswordBox to the credential store and the box is cleared.
    private void SavePgPassword_Click(object sender, RoutedEventArgs e) { _vm.SavePostgresPassword(PgPassword.Password, persist: true); PgPassword.Clear(); }
    private void SessionPgPassword_Click(object sender, RoutedEventArgs e) { _vm.SavePostgresPassword(PgPassword.Password, persist: false); PgPassword.Clear(); }
    private void DeletePgPassword_Click(object sender, RoutedEventArgs e) => _vm.DeletePostgresPassword();
    private void SaveSmtpPassword_Click(object sender, RoutedEventArgs e) { _vm.SaveSmtpPassword(SmtpPassword.Password, persist: true); SmtpPassword.Clear(); }
    private void SessionSmtpPassword_Click(object sender, RoutedEventArgs e) { _vm.SaveSmtpPassword(SmtpPassword.Password, persist: false); SmtpPassword.Clear(); }
    private void DeleteSmtpPassword_Click(object sender, RoutedEventArgs e) => _vm.DeleteSmtpPassword();
    private async void TestMail_Click(object sender, RoutedEventArgs e) => await _vm.SendTestMailAsync().ConfigureAwait(true);

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var typed = PgPassword.Password;
        PgPassword.Clear();
        await _vm.TestConnectionAsync(typed).ConfigureAwait(true);
    }

    /// <summary>Renders each tab to a PNG (used by the --snapshot self-check; no screen capture involved).</summary>
    public void SaveTabSnapshots(string folder)
    {
        Directory.CreateDirectory(folder);
        string[] names = ["1-pano", "2-olaylar", "3-uzun-transactionlar", "4-ayarlar"];
        for (var i = 0; i < names.Length; i++)
        {
            Tabs.SelectedIndex = i;
            if (i == 1 && _vm.Incidents.Incidents.Count > 0)
            {
                _vm.Incidents.SelectedIncident = _vm.Incidents.Incidents[0];
            }
            Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            UpdateLayout();
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new RenderTargetBitmap(
                (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(folder, names[i] + ".png"));
            png.Save(fs);
        }
        Tabs.SelectedIndex = 0;
    }
}
