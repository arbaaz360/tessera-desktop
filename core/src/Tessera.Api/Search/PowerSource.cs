using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tessera.Api.Search;

/// <summary>
/// Mains or battery, asked of the OS at most every half minute.
/// </summary>
/// <remarks>
/// Embedding a library is an hour of every core; on a laptop unplugged that
/// is the battery gone. The settings page has <c>embedOnBattery</c>; this is
/// the other half of the answer. Unknown platforms count as mains.
/// </remarks>
public sealed partial class PowerSource
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private DateTime _checkedAt;
    private bool _onBattery;

    public bool OnBattery
    {
        get
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _checkedAt > CacheFor)
                {
                    _onBattery = Probe();
                    _checkedAt = DateTime.UtcNow;
                }

                return _onBattery;
            }
        }
    }

    private static bool Probe()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;
            }

            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("pmset") { RedirectStandardOutput = true, UseShellExecute = false };
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add("batt");
                using var p = Process.Start(psi);
                if (p is null)
                {
                    return false;
                }

                var text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                return text.Contains("Battery Power", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Not knowing is mains: better to embed than to wait for ever.
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
