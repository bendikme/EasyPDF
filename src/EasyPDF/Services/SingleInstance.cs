using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EasyPDF.Services;

/// <summary>
/// One EasyPDF process per Windows session. Opening a second PDF hands it to the process that is
/// already running - which opens it in a new window in a few milliseconds, with PDFium, fonts and
/// the runtime already warm - instead of starting a whole new process. The hand-over is a named
/// pipe only the same user can connect to. If anything about it fails, the new process simply
/// runs on its own: a file is never lost to a busy or exiting instance.
/// </summary>
public static class SingleInstance
{
    private const int MaxMessage = 1 << 20;

    private static Mutex? _mutex;
    private static Thread? _server;
    private static volatile bool _stopping;
    private static Func<string, string[], bool>? _handler;

    /// <summary>Unique per user and session, so different users and sessions never meet.</summary>
    internal static string Name { get; set; } = BuildName();

    private static string BuildName()
    {
        // The user by name: asking Windows for their SID costs several milliseconds, at the
        // start of every launch, to tell apart what a Local\ name (one per session) already does.
        var user = $"{Environment.UserDomainName}/{Environment.UserName}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)))[..16];
        // A separate instance group (for tests run alongside the user's own EasyPDF).
        var group = Environment.GetEnvironmentVariable("EASYPDF_INSTANCE") is { Length: > 0 } g ? $".{g}" : string.Empty;
        return $"EasyPDF.{hash}.{SessionId()}{group}";
    }

    /// <summary>
    /// The Windows session, asked of the kernel directly: Process.SessionId snapshots every
    /// process on the machine to answer the same question, which costs 20 ms at start-up.
    /// </summary>
    private static uint SessionId() =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session : 0;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    private static string MutexName => $@"Local\{Name}";

    /// <summary>
    /// Hands the command line to a running instance. Returns true when it took it, and this
    /// process should exit. Costs nothing when no instance is running.
    /// </summary>
    public static bool TryForward(string[] args, string currentDirectory, int timeoutMs = 3000)
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false;
            existing.Dispose();

            using var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);

            var payload = Encoding.UTF8.GetBytes(string.Join('\n', new[] { currentDirectory }.Concat(args)));
            if (payload.Length > MaxMessage) return false;

            Span<byte> length = stackalloc byte[4];
            BitConverter.TryWriteBytes(length, payload.Length);
            client.Write(length);
            client.Write(payload);
            client.Flush();

            // Pipes have no read timeout of their own; a reply that never comes means "run alone".
            var reply = new byte[1];
            var read = Task.Run(() => client.Read(reply, 0, 1));
            return read.Wait(timeoutMs) && read.Result == 1 && reply[0] == 1;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
        {
            AppLog.Info($"No running instance took the files ({ex.GetType().Name}); starting a new one.");
            return false;
        }
    }

    /// <summary>
    /// Becomes the instance others hand their files to. <paramref name="handler"/> gets the other
    /// process's current directory and arguments on a background thread and returns whether it
    /// took them.
    /// </summary>
    public static void Claim(Func<string, string[], bool> handler)
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out var created);
            if (!created)
            {
                // Another instance owns the name (it was busy or exiting when this one started).
                _mutex.Dispose();
                _mutex = null;
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("The single-instance name could not be claimed.", ex);
            return;
        }

        _handler = handler;
        _server = new Thread(Serve) { IsBackground = true, Name = "EasyPDF.Instance" };
        _server.Start();
    }

    /// <summary>Refuses further hand-overs; called as the app starts to exit.</summary>
    public static void Stop() => _stopping = true;

    private static void Serve()
    {
        while (!_stopping)
        {
            try
            {
                using var server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly);
                server.WaitForConnection();
                Handle(server);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                if (_stopping) return;
                AppLog.Warn("A hand-over from another EasyPDF failed.", ex);
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                AppLog.Error("The single-instance server stopped.", ex);
                ReleaseName();
                return;
            }
        }
    }

    private static void Handle(NamedPipeServerStream server)
    {
        var length = new byte[4];
        if (!ReadExactly(server, length)) return;
        var size = BitConverter.ToInt32(length);
        if (size is < 0 or > MaxMessage) return;

        var payload = new byte[size];
        if (!ReadExactly(server, payload)) return;

        var parts = Encoding.UTF8.GetString(payload).Split('\n');
        var accepted = false;
        if (!_stopping && parts.Length >= 1 && _handler is { } handler)
        {
            try
            {
                accepted = handler(parts[0], parts.Skip(1).ToArray());
            }
            catch (Exception ex)
            {
                AppLog.Warn("Opening files handed over from another EasyPDF failed.", ex);
            }
        }

        server.WriteByte(accepted ? (byte)1 : (byte)0);
        server.Flush();
        try
        {
            server.WaitForPipeDrain();
        }
        catch (IOException)
        {
        }
    }

    private static void ReleaseName()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not held by this thread: the process exit releases it.
        }
        _mutex?.Dispose();
        _mutex = null;
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = stream.Read(buffer, done, buffer.Length - done);
            if (read <= 0) return false;
            done += read;
        }
        return true;
    }
}
