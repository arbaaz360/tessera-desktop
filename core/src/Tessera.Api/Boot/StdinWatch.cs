namespace Tessera.Api.Boot;

/// <summary>
/// Shuts the core down when stdin reaches end of file.
/// </summary>
/// <remarks>
/// The shell holds the write end of the pipe for as long as it lives. A
/// shell that exits cleanly closes it; a shell that crashes has it closed by
/// the OS. Either way the core sees EOF and stops, so there are no orphans
/// (CONTRACT §1). A dedicated thread, not a task: the read blocks for the
/// life of the process and must not occupy a pool thread. Nothing that
/// happens on this thread may take the process down: a stdin that cannot be
/// read at all (a handle that is not a pipe, not a console, not a file) is
/// logged and ignored, and the shell's kill-on-exit is the backstop.
/// </remarks>
public static class StdinWatch
{
    public static void Start(Action onEof, Action<string> onUnusable)
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
            catch (Exception ex)
            {
                // Not EOF: the handle itself refuses to be read. Stay up.
                onUnusable(ex.Message);
                return;
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
