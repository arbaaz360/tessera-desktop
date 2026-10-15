using System.Diagnostics;
using System.Globalization;

namespace Tessera.Api.Boot;

/// <summary>
/// <c>core.pid</c> in the data dir: who is running against this library.
/// </summary>
/// <remarks>
/// Two cores on one database would both scan, both thumbnail and both hold
/// the vector index; the shell kills a stale pid before spawning and the core
/// refuses to start over a live one (CONTRACT §1, exit code 3). The pid is
/// checked for being OUR kind of process, so a number reused by something
/// unrelated after a crash does not lock the library for ever.
/// </remarks>
public static class PidFile
{
    /// <summary>The live tessera-core the file names, or null when nothing is running.</summary>
    public static int? LiveOwner(string path)
    {
        if (!File.Exists(path) ||
            !int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            pid <= 0 || pid == Environment.ProcessId)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return null;
            }

            // A reused pid belongs to something else. Only our own name counts.
            return process.ProcessName.StartsWith("tessera-core", StringComparison.OrdinalIgnoreCase) ? pid : null;
        }
        catch (ArgumentException)
        {
            return null;   // no such process
        }
        catch (InvalidOperationException)
        {
            return null;   // exited between the two calls
        }
    }

    public static void Write(string path, int pid) =>
        File.WriteAllText(path, pid.ToString(CultureInfo.InvariantCulture));

    /// <summary>Remove the file, but only while it still names us.</summary>
    public static void Remove(string path, int pid)
    {
        try
        {
            if (File.Exists(path) &&
                int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var named) &&
                named == pid)
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A stale pid left behind is harmless: the next start checks liveness.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
