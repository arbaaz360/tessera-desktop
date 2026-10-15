namespace Tessera.Api.Boot;

/// <summary>
/// Shuts the core down when stdin reaches end of file.
/// </summary>
/// <remarks>
/// The shell holds the write end of the pipe for as long as it lives. A
/// shell that exits cleanly closes it; a shell that crashes has it closed by
/// the OS. Either way the core sees EOF and stops, so there are no orphans
/// (CONTRACT §1). A dedicated thread, not a task: the read blocks for the
/// life of the process and must not occupy a pool thread.
/// </remarks>
public static class StdinWatch
{
    public static void Start(Action onEof)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var stdin = Console.OpenStandardInput();
                var buffer = new byte[256];
                while (stdin.Read(buffer, 0, buffer.Length) > 0)
                {
                    // Nothing is ever expected on stdin; bytes are discarded.
                }
            }
            catch (IOException)
            {
                // A broken pipe is EOF by another name.
            }
            catch (ObjectDisposedException)
            {
            }

            onEof();
        })
        {
            IsBackground = true,
            Name = "stdin-eof",
        };
        thread.Start();
    }
}
