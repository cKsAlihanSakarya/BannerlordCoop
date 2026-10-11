using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoopServerPanel;

/// <summary>Discord's REST API with the bot token, retrying when Discord asks to slow down.</summary>
public sealed class DiscordRest : IDisposable
{
    private readonly HttpClient http;
    private readonly string apiBase;

    public DiscordRest(PanelSettings settings)
    {
        apiBase = settings.DiscordApiBase.TrimEnd('/');
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", settings.DiscordToken.Trim());
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://github.com/Bannerlord-Coop-Team/BannerlordCoop, 1.0)");
    }

    public async Task<JsonElement?> SendAsync(HttpMethod method, string path, JsonNode body, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, apiBase + path);
            if (body != null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await http.SendAsync(request, token);
            string text = await response.Content.ReadAsStringAsync(token);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
            {
                double wait = 1;
                try
                {
                    using var limited = JsonDocument.Parse(text);
                    if (limited.RootElement.TryGetProperty("retry_after", out var retry)) wait = retry.GetDouble();
                }
                catch (JsonException)
                {
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 0.1, 30)), token);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw new DiscordException((int)response.StatusCode, text);

            if (string.IsNullOrWhiteSpace(text)) return null;
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
    }

    public void Dispose() => http.Dispose();
}

public sealed class DiscordException : Exception
{
    public DiscordException(int status, string body) : base($"Discord answered {status}: {body}")
    {
        Status = status;
    }

    public int Status { get; }
}

/// <summary>
/// One gateway connection to Discord: hello, heartbeats, identify or resume, and dispatch events.
/// Reconnects on its own, except after close codes that say the token or setup is wrong.
/// </summary>
public sealed class DiscordGateway
{
    private const int OpDispatch = 0;
    private const int OpHeartbeat = 1;
    private const int OpIdentify = 2;
    private const int OpResume = 6;
    private const int OpReconnect = 7;
    private const int OpInvalidSession = 9;
    private const int OpHello = 10;
    private const int OpHeartbeatAck = 11;

    private readonly string token;
    private readonly string gatewayUrl;
    private readonly ILogger logger;
    private readonly Func<string, JsonElement, Task> onDispatch;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    private string sessionId;
    private string resumeUrl;
    private long? sequence;

    public DiscordGateway(PanelSettings settings, ILogger logger, Func<string, JsonElement, Task> onDispatch)
    {
        token = settings.DiscordToken.Trim();
        gatewayUrl = settings.DiscordGatewayUrl;
        this.logger = logger;
        this.onDispatch = onDispatch;
    }

    /// <summary>Set when Discord refused the bot for good (wrong token, bad intents).</summary>
    public string FatalError { get; private set; }

    public bool Connected { get; private set; }

    public async Task RunAsync(CancellationToken stop)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested && FatalError == null)
        {
            DateTimeOffset started = DateTimeOffset.Now;
            try
            {
                await RunSessionAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or JsonException
                                                  or HttpRequestException or InvalidOperationException)
            {
                logger.LogWarning("Discord connection lost: {Message}", exception.Message);
            }
            finally
            {
                Connected = false;
            }

            if (FatalError != null) break;
            // A session that lasted a while was healthy; start the backoff over.
            if (DateTimeOffset.Now - started > TimeSpan.FromMinutes(1)) backoff = TimeSpan.FromSeconds(1);
            try
            {
                await Task.Delay(backoff, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
        }
    }

    private async Task RunSessionAsync(CancellationToken stop)
    {
        using var socket = new ClientWebSocket();
        bool resuming = sessionId != null && resumeUrl != null;
        await socket.ConnectAsync(new Uri(resuming ? resumeUrl : gatewayUrl), stop);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task heartbeats = Task.CompletedTask;
        bool acked = true;

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                string message = await ReceiveAsync(socket, session.Token);
                if (message == null) break;

                using var document = JsonDocument.Parse(message);
                JsonElement root = document.RootElement;
                int op = root.GetProperty("op").GetInt32();

                switch (op)
                {
                    case OpHello:
                        int interval = root.GetProperty("d").GetProperty("heartbeat_interval").GetInt32();
                        heartbeats = HeartbeatAsync(socket, interval, () => acked, value => acked = value, session);
                        if (resuming)
                            await SendAsync(socket, new JsonObject
                            {
                                ["op"] = OpResume,
                                ["d"] = new JsonObject { ["token"] = token, ["session_id"] = sessionId, ["seq"] = sequence },
                            }, session.Token);
                        else
                            await SendAsync(socket, new JsonObject
                            {
                                ["op"] = OpIdentify,
                                ["d"] = new JsonObject
                                {
                                    ["token"] = token,
                                    // Interactions arrive without any intent.
                                    ["intents"] = 0,
                                    ["properties"] = new JsonObject
                                    {
                                        ["os"] = "windows",
                                        ["browser"] = "coop-server-panel",
                                        ["device"] = "coop-server-panel",
                                    },
                                },
                            }, session.Token);
                        break;

                    case OpHeartbeatAck:
                        acked = true;
                        break;

                    case OpHeartbeat:
                        await SendAsync(socket, new JsonObject { ["op"] = OpHeartbeat, ["d"] = sequence }, session.Token);
                        break;

                    case OpReconnect:
                        await CloseAsync(socket, WebSocketCloseStatus.ProtocolError);
                        return;

                    case OpInvalidSession:
                        bool resumable = root.TryGetProperty("d", out var canResume) && canResume.ValueKind == JsonValueKind.True;
                        if (!resumable)
                        {
                            sessionId = null;
                            resumeUrl = null;
                            sequence = null;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble() * 4), session.Token);
                        await CloseAsync(socket, WebSocketCloseStatus.ProtocolError);
                        return;

                    case OpDispatch:
                        if (root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number) sequence = s.GetInt64();
                        string type = root.GetProperty("t").GetString();
                        JsonElement data = root.GetProperty("d").Clone();
                        if (type == "READY")
                        {
                            sessionId = data.GetProperty("session_id").GetString();
                            string url = data.TryGetProperty("resume_gateway_url", out var r) ? r.GetString() : null;
                            resumeUrl = url == null ? null : url.TrimEnd('/') + "/?v=10&encoding=json";
                            Connected = true;
                            logger.LogInformation("Discord bot connected");
                        }
                        else if (type == "RESUMED")
                        {
                            Connected = true;
                        }

                        // Handlers answer Discord on their own; never hold up the read loop.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await onDispatch(type, data);
                            }
                            catch (Exception exception)
                            {
                                logger.LogError(exception, "Discord event {Type} failed", type);
                            }
                        });
                        break;
                }
            }

            HandleClose(socket);
        }
        finally
        {
            session.Cancel();
            try
            {
                await heartbeats;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
            {
            }
        }
    }

    private void HandleClose(ClientWebSocket socket)
    {
        int code = (int?)socket.CloseStatus ?? 0;
        switch (code)
        {
            case 4004:
                FatalError = "token_rejected";
                logger.LogError("Discord rejected the bot token (4004). Check DiscordToken in panelsettings.json.");
                break;
            case >= 4010 and <= 4014:
                FatalError = "setup_rejected";
                logger.LogError("Discord closed the bot for good ({Code}): {Reason}", code, socket.CloseStatusDescription);
                break;
            case 4007:
            case 4009:
                // Bad sequence or a timed-out session can't be resumed.
                sessionId = null;
                resumeUrl = null;
                sequence = null;
                break;
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket socket, int interval, Func<bool> isAcked, Action<bool> setAcked,
        CancellationTokenSource session)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(interval * Random.Shared.NextDouble()), session.Token);
        while (!session.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (!isAcked())
            {
                // No answer to the last beat: the connection is dead even if the socket looks open.
                logger.LogWarning("Discord stopped answering heartbeats; reconnecting");
                await CloseAsync(socket, WebSocketCloseStatus.ProtocolError);
                session.Cancel();
                return;
            }

            setAcked(false);
            await SendAsync(socket, new JsonObject { ["op"] = OpHeartbeat, ["d"] = sequence }, session.Token);
            await Task.Delay(interval, session.Token);
        }
    }

    private async Task SendAsync(ClientWebSocket socket, JsonNode payload, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await sendLock.WaitAsync(token);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private static async Task CloseAsync(ClientWebSocket socket, WebSocketCloseStatus status)
    {
        try
        {
            // Any code but 1000/1001 keeps the session resumable.
            await socket.CloseOutputAsync(status, "reconnect", CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
        {
        }
    }
}
