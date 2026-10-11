using Common.Logging;
using Coop.Core.Common.Session;
using Serilog;
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Coop.Core.Server.Services.Admin;

/// <summary>
/// Serves <see cref="IServerAdminRequestHandler"/> on a local named pipe for the lifetime of the
/// server session, so a companion tool (such as tools/CoopServerPanel) can read the player list,
/// unstick players and schedule a graceful shutdown without the developer console.
/// </summary>
/// <remarks>
/// A named pipe is local to this computer and needs no open port or firewall rule. The default
/// pipe ACL only lets the account that started the server (and administrators) write to it, so
/// other accounts can connect but cannot send a request.
/// <para>
/// .NET Framework ignores cancellation tokens once a pipe wait or read has started, so every
/// timeout and the shutdown end the wait by closing the pipe instead.
/// </para>
/// </remarks>
public interface IServerAdminPipeHost : IDisposable
{
}

/// <inheritdoc cref="IServerAdminPipeHost"/>
public sealed class ServerAdminPipeHost : IServerAdminPipeHost
{
    private static readonly ILogger Logger = LogManager.GetLogger<ServerAdminPipeHost>();
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly IServerAdminRequestHandler handler;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private readonly Thread thread;
    private NamedPipeServerStream currentPipe;
    private int disposed;

    public ServerAdminPipeHost(IServerAdminRequestHandler handler)
        : this(handler, ServerAdminProtocol.GetPipeName(ManagedServerConfig.Port))
    {
    }

    internal ServerAdminPipeHost(IServerAdminRequestHandler handler, string pipeName)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (pipeName == null) throw new ArgumentNullException(nameof(pipeName));

        this.handler = handler;
        this.pipeName = pipeName;

        thread = new Thread(Serve)
        {
            IsBackground = true,
            Name = "Coop server admin pipe",
        };
        thread.Start();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;

        cancellation.Cancel();
        // Closing the pipe aborts a pending connection wait or read on every runtime.
        Interlocked.Exchange(ref currentPipe, null)?.Dispose();
        // The loop exits promptly; never hold the caller (the game thread) for long.
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Serve()
    {
        Logger.Information("Server admin pipe listening on {PipeName}", pipeName);
        CancellationToken token = cancellation.Token;

        // The next instance is created before the connected one is served and closed, so a
        // client never finds the name missing between two requests.
        NamedPipeServerStream next = TryCreatePipe();
        while (next != null && !token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe = next;
            next = null;
            Volatile.Write(ref currentPipe, pipe);

            try
            {
                if (token.IsCancellationRequested) break;
                pipe.WaitForConnection();
                next = TryCreatePipe();
                ServeOne(pipe);
            }
            catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException ||
                                              exception is InvalidOperationException)
            {
                // Closed by Dispose or a timeout, or the client hung up; not fatal to the listener.
                if (!token.IsCancellationRequested)
                    Logger.Debug(exception, "Server admin pipe request ended early");
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Server admin pipe request failed");
            }
            finally
            {
                Interlocked.CompareExchange(ref currentPipe, null, pipe);
                pipe.Dispose();
            }

            if (next == null && !token.IsCancellationRequested) next = TryCreatePipe();
        }

        next?.Dispose();
        cancellation.Dispose();
        Logger.Information("Server admin pipe {PipeName} closed", pipeName);
    }

    private NamedPipeServerStream TryCreatePipe()
    {
        try
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                2,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                65536);
        }
        catch (IOException exception)
        {
            // Another server on this computer already owns the name (same port).
            Logger.Warning(exception, "Server admin pipe {PipeName} is unavailable; the admin panel cannot reach this server", pipeName);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Server admin pipe {PipeName} could not be created", pipeName);
        }
        return null;
    }

    private void ServeOne(NamedPipeServerStream pipe)
    {
        string request;
        using (var timeout = new CancellationTokenSource(ReadTimeout))
        using (timeout.Token.Register(pipe.Dispose))
        {
            request = ReadRequestLine(pipe);
        }

        string response = request == null
            ? ServerAdminProtocol.Message(false, "No request was received.")
            : handler.Handle(request);

        byte[] bytes = Utf8NoBom.GetBytes(response + "\n");
        using (var timeout = new CancellationTokenSource(WriteTimeout))
        using (timeout.Token.Register(pipe.Dispose))
        {
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            // The client hangs up once it has the line; wait for that instead of closing under it.
            var rest = new byte[64];
            while (pipe.Read(rest, 0, rest.Length) > 0)
            {
            }
        }
    }

    /// <summary>Reads one line, or null on a hang-up or a line longer than the limit.</summary>
    private static string ReadRequestLine(Stream pipe)
    {
        var buffer = new byte[ServerAdminProtocol.MaxRequestLength + 2];
        int length = 0;

        while (length < buffer.Length)
        {
            int read = pipe.Read(buffer, length, buffer.Length - length);
            if (read == 0) break;

            int newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            length += read;
            if (newline >= 0)
                return Utf8NoBom.GetString(buffer, 0, newline).TrimEnd('\r');
        }

        // A client that wrote without a newline and closed still gets an answer.
        return length > 0 && length <= ServerAdminProtocol.MaxRequestLength
            ? Utf8NoBom.GetString(buffer, 0, length).TrimEnd('\r')
            : null;
    }
}
