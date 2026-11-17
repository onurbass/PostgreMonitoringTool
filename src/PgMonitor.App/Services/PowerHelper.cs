using System.Runtime.InteropServices;

namespace PgMonitor.App.Services;

/// <summary>
/// Asks Windows not to sleep while this process runs on AC power (SetThreadExecutionState). This is a request scoped
/// to the process; it changes no Windows setting and ends when the app exits. Closing the laptop lid still sleeps.
/// </summary>
internal static class PowerHelper
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    private static bool _wanted;

    public static void KeepAwake(bool wanted)
    {
        _wanted = wanted;
        Refresh();
    }

    /// <summary>Re-evaluates after a power change (AC plugged/unplugged).</summary>
    public static void Refresh()
    {
        var onAc = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
        _ = SetThreadExecutionState(_wanted && onAc ? EsContinuous | EsSystemRequired : EsContinuous); // 0 = failed; nothing to do then
    }

#pragma warning disable SYSLIB1054 // LibraryImport would need unsafe code for nothing here.
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SetThreadExecutionState(uint flags);
#pragma warning restore SYSLIB1054
}
