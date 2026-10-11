using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoopServerPanel;

public enum ServerState
{
    /// <summary>No server process.</summary>
    Stopped,
    /// <summary>The panel started the process and it is still loading.</summary>
    Starting,
    /// <summary>The server answers on its admin pipe.</summary>
    Running,
    /// <summary>A server process exists but does not answer.</summary>
    NotResponding,
}

public sealed record ServerSnapshot(ServerState State, JsonElement? Server, string Problem, DateTimeOffset CheckedAt);

/// <summary>The current or most recent long action (start, stop, restart, force stop).</summary>
public sealed record OperationInfo(
    string Name,
    string User,
    string Code,
    string Detail,
    bool Running,
    bool Failed,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Owns the server process: polls the admin pipe, runs one start/stop/restart at a time and
/// restarts a crashed server. Stops always go through the mod's graceful shutdown, so the
/// campaign is saved before the process closes.
/// </summary>
public sealed class ServerSupervisor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ShutdownMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(15);
    private const int MaxCrashRestarts = 3;
    private static readonly Regex RemainingSeconds = new(@"remaining=(\d+)s", RegexOptions.CultureInvariant);

    private readonly PanelSettings settings;
    private readonly AdminPipeClient pipe;
    private readonly ActivityLog log;
    private readonly ILogger<ServerSupervisor> logger;
    private readonly object gate = new();
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private readonly List<DateTimeOffset> crashTimes = new();

    private Process launched;
    private DateTimeOffset launchedAt;
    private bool seenRunningSinceLaunch;
    private bool expectingExit;
    private int knownPid;
    private string knownLaunchSave;
    private ServerSnapshot snapshot = new(ServerState.Stopped, null, null, DateTimeOffset.MinValue);
    private OperationInfo operation;
    private CancellationToken stoppingToken = CancellationToken.None;

    public ServerSupervisor(PanelSettings settings, AdminPipeClient pipe, ActivityLog log, ILogger<ServerSupervisor> logger)
    {
        this.settings = settings;
        this.pipe = pipe;
        this.log = log;
        this.logger = logger;
    }

    public ServerSnapshot Snapshot
    {
        get { lock (gate) return snapshot; }
    }

    public OperationInfo Operation
    {
        get { lock (gate) return operation; }
    }

    /// <summary>The save a start would load: the configured one, else the last one the server reported.</summary>
    public string NextSave
    {
        get
        {
            lock (gate)
                return !string.IsNullOrWhiteSpace(settings.SaveName) ? settings.SaveName.Trim() : knownLaunchSave;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        stoppingToken = token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Status refresh failed");
            }

            try
            {
                await Task.Delay(PollInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<ServerSnapshot> RefreshAsync(CancellationToken token)
    {
        await refreshLock.WaitAsync(token);
        try
        {
            ServerSnapshot next;
            DateTimeOffset now = DateTimeOffset.Now;
            try
            {
                JsonElement reply = await pipe.SendAsync("status", StatusTimeout, token);
                if (IsOk(reply))
                {
                    int pid = reply.TryGetProperty("processId", out var pidValue) && pidValue.TryGetInt32(out var id) ? id : 0;
                    string save = GetString(reply, "launchSave");
                    lock (gate)
                    {
                        if (pid > 0) knownPid = pid;
                        if (!string.IsNullOrEmpty(save)) knownLaunchSave = save;
                        if (launched != null && SafeId(launched) == pid) seenRunningSinceLaunch = true;
                    }
                    next = new ServerSnapshot(ServerState.Running, reply, null, now);
                }
                else
                {
                    next = new ServerSnapshot(ServerState.NotResponding, null, GetString(reply, "message"), now);
                }
            }
            catch (ServerUnreachableException exception)
            {
                int pid = FindServerPid();
                if (pid == 0)
                {
                    next = new ServerSnapshot(ServerState.Stopped, null, null, now);
                }
                else
                {
                    bool starting;
                    lock (gate)
                    {
                        starting = launched != null && SafeId(launched) == pid && !seenRunningSinceLaunch &&
                                   now - launchedAt < StartupGrace;
                    }
                    next = new ServerSnapshot(starting ? ServerState.Starting : ServerState.NotResponding, null,
                        exception.Message, now);
                }
            }

            lock (gate) snapshot = next;
            return next;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public bool TryStart(string user, out string error) =>
        TryRunOperation("start", user, token => StartServerAsync(user, token), out error);

    public bool TryStop(string user, int seconds, out string error) =>
        TryRunOperation("stop", user, token => StopServerAsync(user, seconds, restart: false, token), out error);

    public bool TryRestart(string user, int seconds, out string error) =>
        TryRunOperation("restart", user, token => StopServerAsync(user, seconds, restart: true, token), out error);

    public bool TryForceStop(string user, out string error) =>
        TryRunOperation("kill", user, token => ForceStopAsync(user, token), out error);

    public async Task<(bool Ok, string Message)> UnstuckAsync(string user, string controllerId, CancellationToken token)
    {
        string label = DescribePlayer(controllerId);
        try
        {
            JsonElement reply = await pipe.SendAsync($"unstuck {controllerId}", CommandTimeout, token);
            bool ok = IsOk(reply);
            string message = GetString(reply, "message");
            log.Add(user, ok ? "unstuck_ok" : "unstuck_failed", ok ? label : $"{label}: {message}",
                ok ? ActivityKind.Success : ActivityKind.Error);
            return (ok, message);
        }
        catch (ServerUnreachableException exception)
        {
            log.Add(user, "unstuck_failed", $"{label}: {exception.Message}", ActivityKind.Error);
            return (false, exception.Message);
        }
    }

    public async Task<(bool Ok, string Message)> CancelShutdownAsync(string user, CancellationToken token)
    {
        try
        {
            JsonElement reply = await pipe.SendAsync("cancel", CommandTimeout, token);
            bool ok = IsOk(reply);
            string message = GetString(reply, "message");
            log.Add(user, ok ? "cancel_ok" : "cancel_failed", ok ? null : message,
                ok ? ActivityKind.Success : ActivityKind.Error);
            return (ok, message);
        }
        catch (ServerUnreachableException exception)
        {
            log.Add(user, "cancel_failed", exception.Message, ActivityKind.Error);
            return (false, exception.Message);
        }
    }

    /// <summary>Launch arguments for a dedicated server hosting <paramref name="save"/>.</summary>
    public static List<string> BuildArguments(PanelSettings settings, string save)
    {
        var arguments = new List<string>
        {
            "/singleplayer",
            "/server",
            "_MODULES_*" + string.Join("*", settings.Modules) + "*_MODULES_",
            "/coopsave",
            save,
            "/coopvisibility",
            settings.Visibility,
        };

        if (!string.IsNullOrEmpty(settings.ServerPassword))
        {
            arguments.Add("/cooppassword");
            arguments.Add(settings.ServerPassword);
        }

        arguments.AddRange(SplitArguments(settings.ExtraArguments));
        return arguments;
    }

    /// <summary>Splits on spaces, keeping "double quoted" parts together.</summary>
    public static IEnumerable<string> SplitArguments(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var current = new System.Text.StringBuilder();
        bool quoted = false;
        bool any = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) yield return current.ToString();
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any) yield return current.ToString();
    }

    private bool TryRunOperation(string name, string user, Func<CancellationToken, Task> work, out string error)
    {
        lock (gate)
        {
            if (operation is { Running: true })
            {
                error = "busy";
                return false;
            }

            operation = new OperationInfo(name, user, name + "_requested", null, true, false, DateTimeOffset.Now, null);
        }

        error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await work(stoppingToken);
                Finish(failed: false);
            }
            catch (OperationFailedException failure)
            {
                log.Add(user, failure.Code, failure.Detail, failure.Kind);
                Progress(failure.Code, failure.Detail);
                Finish(failed: failure.Kind == ActivityKind.Error);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Finish(failed: true);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Operation {Name} failed", name);
                log.Add(user, "unexpected_error", exception.Message, ActivityKind.Error);
                Progress("unexpected_error", exception.Message);
                Finish(failed: true);
            }
        });
        return true;
    }

    private void Progress(string code, string detail = null)
    {
        lock (gate)
        {
            if (operation != null) operation = operation with { Code = code, Detail = detail };
        }
    }

    private void Finish(bool failed)
    {
        lock (gate)
        {
            if (operation != null)
                operation = operation with { Running = false, Failed = failed, FinishedAt = DateTimeOffset.Now };
        }
    }

    private async Task StartServerAsync(string user, CancellationToken token)
    {
        ServerSnapshot current = await RefreshAsync(token);
        if (current.State != ServerState.Stopped)
            throw new OperationFailedException("already_running");

        string save = NextSave;
        if (string.IsNullOrWhiteSpace(save))
            throw new OperationFailedException("no_save");

        string exe = settings.BannerlordExe;
        if (!File.Exists(exe))
            throw new OperationFailedException("exe_missing", exe);

        var startInfo = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
        };
        foreach (string argument in BuildArguments(settings, save))
            startInfo.ArgumentList.Add(argument);

        log.Add(user, "start_requested", save);
        Progress("starting", save);

        Process process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            throw new OperationFailedException("start_failed", exception.Message);
        }
        if (process == null) throw new OperationFailedException("start_failed");

        process.EnableRaisingEvents = true;
        process.Exited += OnLaunchedExited;
        lock (gate)
        {
            launched?.Dispose();
            launched = process;
            launchedAt = DateTimeOffset.Now;
            seenRunningSinceLaunch = false;
            expectingExit = false;
            knownPid = process.Id;
        }

        DateTimeOffset deadline = DateTimeOffset.Now + StartupGrace;
        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);

            ServerSnapshot state = await RefreshAsync(token);
            if (state.State == ServerState.Running && CampaignLoaded(state))
            {
                log.Add(user, "started", save, ActivityKind.Success);
                Progress("started", save);
                return;
            }

            if (HasExited(process))
            {
                // A clean, quick exit can be a relaunch through Steam; give the new process a moment.
                int exitCode = SafeExitCode(process);
                if (exitCode != 0 || DateTimeOffset.Now - launchedAt > TimeSpan.FromMinutes(2) &&
                    FindServerPid() == 0)
                {
                    throw new OperationFailedException("start_exited",
                        exitCode.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        throw new OperationFailedException("start_timeout");
    }

    private async Task StopServerAsync(string user, int seconds, bool restart, CancellationToken token)
    {
        log.Add(user, restart ? "restart_requested" : "stop_requested",
            seconds.ToString(CultureInfo.InvariantCulture));

        JsonElement reply;
        try
        {
            reply = await pipe.SendAsync($"shutdown {seconds}", CommandTimeout, token);
        }
        catch (ServerUnreachableException)
        {
            throw new OperationFailedException("not_responding");
        }

        if (!IsOk(reply))
            throw new OperationFailedException("shutdown_refused", GetString(reply, "message"));

        DateTimeOffset deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(seconds) + ShutdownMargin;
        int pid = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            if (DateTimeOffset.Now > deadline)
                throw new OperationFailedException("shutdown_timeout");

            ServerSnapshot state = await RefreshAsync(token);
            if (state.State == ServerState.Stopped) break;
            // Saving can hold the game thread for a while; keep waiting.
            if (state.State != ServerState.Running || state.Server is not JsonElement server) continue;

            pid = server.TryGetProperty("processId", out var pidValue) && pidValue.TryGetInt32(out var id) ? id : pid;
            string phase = null;
            string detail = null;
            if (server.TryGetProperty("shutdown", out var shutdown))
            {
                phase = GetString(shutdown, "phase");
                detail = GetString(shutdown, "detail");
            }

            switch (phase)
            {
                case "Countdown":
                    var match = detail == null ? null : RemainingSeconds.Match(detail);
                    Progress("countdown", match is { Success: true } ? match.Groups[1].Value : null);
                    continue;
                case "Draining":
                    Progress("draining");
                    continue;
                case "Saving":
                    Progress("saving");
                    continue;
                case "Completed":
                    break;
                case "Failed":
                    throw new OperationFailedException("save_failed", detail);
                case "Idle":
                    throw new OperationFailedException("stop_cancelled", null, ActivityKind.Info);
                default:
                    continue;
            }

            break;
        }

        Progress("closing");
        await CloseServerAsync(pid == 0 ? FindServerPid() : pid, askToExit: true, token);
        log.Add(user, "stopped", null, ActivityKind.Success);
        Progress("stopped");

        if (restart) await StartServerAsync(user, token);
    }

    private async Task ForceStopAsync(string user, CancellationToken token)
    {
        log.Add(user, "kill_requested");
        int pid = FindServerPid();
        if (pid == 0) throw new OperationFailedException("not_running");

        Progress("closing");
        // A server that still answers may be stuck loading; let it quit cleanly if it allows that.
        await CloseServerAsync(pid, askToExit: true, token, gracefulWait: TimeSpan.FromSeconds(10));
        log.Add(user, "killed", null, ActivityKind.Success);
        Progress("killed");
    }

    /// <summary>Asks the server to quit, then closes its window, then ends the process.</summary>
    private async Task CloseServerAsync(int pid, bool askToExit, CancellationToken token, TimeSpan? gracefulWait = null)
    {
        lock (gate) expectingExit = true;
        if (pid == 0) return;

        if (askToExit)
        {
            try
            {
                JsonElement reply = await pipe.SendAsync("exit", TimeSpan.FromSeconds(10), token);
                if (IsOk(reply) && await WaitForExitAsync(pid, gracefulWait ?? TimeSpan.FromSeconds(60), token)) return;
            }
            catch (ServerUnreachableException)
            {
                // Not answering; fall through to closing the window.
            }
        }

        if (!IsAlive(pid)) return;

        try
        {
            using var process = Process.GetProcessById(pid);
            process.CloseMainWindow();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return;
        }

        if (await WaitForExitAsync(pid, TimeSpan.FromSeconds(20), token)) return;

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return;
        }

        if (!await WaitForExitAsync(pid, TimeSpan.FromSeconds(15), token))
            throw new OperationFailedException("kill_failed");
    }

    private void OnLaunchedExited(object sender, EventArgs e)
    {
        var process = (Process)sender;
        int exitCode = SafeExitCode(process);
        bool wasRunning;
        lock (gate)
        {
            if (!ReferenceEquals(process, launched) || expectingExit) return;
            wasRunning = seenRunningSinceLaunch;
        }

        log.Add("panel", "crash_detected", exitCode.ToString(CultureInfo.InvariantCulture), ActivityKind.Error);

        // A server that never finished loading would just fail again.
        if (!settings.RestartOnCrash || !wasRunning) return;

        lock (crashTimes)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            crashTimes.RemoveAll(time => now - time > CrashWindow);
            if (crashTimes.Count >= MaxCrashRestarts)
            {
                log.Add("panel", "crash_giveup", null, ActivityKind.Error);
                return;
            }
            crashTimes.Add(now);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            if (!TryStart("panel", out _)) log.Add("panel", "crash_restart_skipped");
        });
    }

    private int FindServerPid()
    {
        int pid;
        lock (gate)
        {
            if (launched != null && !HasExited(launched)) return SafeId(launched);
            pid = knownPid;
        }

        return pid > 0 && IsAlive(pid) ? pid : 0;
    }

    /// <summary>True when <paramref name="pid"/> is a live process with the server exe's name.</summary>
    private bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && string.Equals(process.ProcessName,
                Path.GetFileNameWithoutExtension(settings.BannerlordExe), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (!IsAlive(pid)) return true;
            await Task.Delay(500, token);
        }
        return !IsAlive(pid);
    }

    private string DescribePlayer(string controllerId)
    {
        if (Snapshot.Server is JsonElement server && server.TryGetProperty("players", out var players) &&
            players.ValueKind == JsonValueKind.Array)
        {
            foreach (var player in players.EnumerateArray())
            {
                if (GetString(player, "controllerId") != controllerId) continue;
                return GetString(player, "hero") ?? GetString(player, "name") ?? controllerId;
            }
        }
        return controllerId;
    }

    /// <summary>Players can join once the server reports the campaign loaded.</summary>
    private static bool CampaignLoaded(ServerSnapshot state) =>
        state.Server is JsonElement server && server.TryGetProperty("campaignLoaded", out var loaded) &&
        loaded.ValueKind == JsonValueKind.True;

    private static bool IsOk(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("ok", out var ok) &&
        ok.ValueKind == JsonValueKind.True;

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int SafeId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    public override void Dispose()
    {
        lock (gate) launched?.Dispose();
        refreshLock.Dispose();
        base.Dispose();
    }
}

/// <summary>Ends an operation with a code the page translates.</summary>
public sealed class OperationFailedException : Exception
{
    public OperationFailedException(string code, string detail = null, ActivityKind kind = ActivityKind.Error)
        : base(code)
    {
        Code = code;
        Detail = detail;
        Kind = kind;
    }

    public string Code { get; }
    public string Detail { get; }
    public ActivityKind Kind { get; }
}
