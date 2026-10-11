using Coop.Core.Server.Services.Admin;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CoopServerPanel;

/// <summary>One request, one reply over the server's local admin pipe.</summary>
public sealed class AdminPipeClient
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string pipeName;

    public AdminPipeClient(int gamePort) : this(ServerAdminProtocol.GetPipeName(gamePort))
    {
    }

    public AdminPipeClient(string pipeName)
    {
        this.pipeName = pipeName;
    }

    /// <summary>
    /// Sends <paramref name="request"/> and parses the reply. Throws
    /// <see cref="ServerUnreachableException"/> when no server answers in time.
    /// </summary>
    public async Task<JsonElement> SendAsync(string request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            // The server keeps a spare instance listening, so a live server accepts at once.
            await pipe.ConnectAsync(TimeSpan.FromSeconds(2), cts.Token);

            byte[] bytes = Utf8NoBom.GetBytes(request + "\n");
            await pipe.WriteAsync(bytes, cts.Token);
            await pipe.FlushAsync(cts.Token);

            using var reader = new StreamReader(pipe, Utf8NoBom, false, 4096, leaveOpen: true);
            string line = await reader.ReadLineAsync(cts.Token);
            if (line == null) throw new ServerUnreachableException("The server closed the pipe without replying.");

            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServerUnreachableException("The server did not answer in time.");
        }
        catch (TimeoutException)
        {
            throw new ServerUnreachableException("No server is listening.");
        }
        catch (IOException exception)
        {
            throw new ServerUnreachableException(exception.Message);
        }
        catch (JsonException)
        {
            throw new ServerUnreachableException("The server sent a reply the panel cannot read.");
        }
    }
}

public sealed class ServerUnreachableException : Exception
{
    public ServerUnreachableException(string message) : base(message)
    {
    }
}
