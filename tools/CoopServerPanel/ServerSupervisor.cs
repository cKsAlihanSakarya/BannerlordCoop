using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoopServerPanel;

public enum ServerState
{
    /// <summary>No server process.</summary>
    Stopped,
    /// <summary>The process runs and is booting or loading the campaign.</summary>
    Starting,
    /// <summary>The server reports it is serving.</summary>
    Running,
    /// <summary>The server is saving and shutting down.</summary>
    Stopping,
    /// <summary>A server process exists but its log went quiet.</summary>
    NotResponding,
}

public sealed record ServerSnapshot(
    ServerState State,
    string Save,
    bool? PasswordSet,
    IReadOnlyList<LivePlayer> Players,
    string Problem,
    DateTimeOffset CheckedAt);

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
/// Runs the official dedicated server (BannerlordCoopServer.exe): starts it in its own window,
/// types commands into that window the way the host would, and reads the results from the
/// server's log. Stops warn players with <c>coop.server.shutdown</c> and then use the server's
/// own <c>stop</c>, which saves the world before it exits.
/// </summary>
public sealed class ServerSupervisor : BackgroundService
{
    public const string ProcessName = "BannerlordCoopServer";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SilentLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AnswerQuiet = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(15);
    private const int MaxCrashRestarts = 3;
    private const int ExitVerificationFailed = 4;
    private static readonly Regex ControllerIdLine = new(@"^\s*ControllerId:\s*(\S+)", RegexOptions.CultureInvariant);
    private static readonly Regex PlayerNameLine = new(@"^\s*-\s*PlayerName:\s*(.+?)\s*$", RegexOptions.CultureInvariant);

    private readonly PanelSettings settings;
    private readonly ServerLog log;
    private readonly IConsoleTyper typer;
    private readonly ActivityLog activity;
    private readonly ILogger<ServerSupervisor> logger;
    private readonly object gate = new();
    private readonly SemaphoreSlim typing = new(1, 1);
    private readonly List<DateTimeOffset> crashTimes = new();

    private Process launched;
    private bool seenServingSinceLaunch;
    private bool expectingExit;
    private ServerSnapshot snapshot = new(ServerState.Stopped, null, null, Array.Empty<LivePlayer>(), null, DateTimeOffset.MinValue);
    private OperationInfo operation;
    private CancellationToken stoppingToken = CancellationToken.None;

    public ServerSupervisor(PanelSettings settings, ServerLog log, IConsoleTyper typer, ActivityLog activity,
        ILogger<ServerSupervisor> logger)
    {
        this.settings = settings;
        this.log = log;
        this.typer = typer;
        this.activity = activity;
        this.logger = logger;
    }

    public ServerSnapshot Snapshot { get { lock (gate) return snapshot; } }

    public OperationInfo Operation { get { lock (gate) return operation; } }

    /// <summary>The save server-config.json names, which a start hosts.</summary>
    public string ConfiguredSave => ReadConfiguredSave();

    /// <summary>False when server-config.json turns the log file off, which the panel needs.</summary>
    public bool LogFileEnabled => ReadConfigBool("logFile") ?? true;

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        stoppingToken = token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                Refresh();
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

    public ServerSnapshot Refresh()
    {
        log.Poll();

        Process process = FindServer();
        DateTimeOffset now = DateTimeOffset.Now;
        ServerState state;
        string problem = null;

        // A log from before this process started describes an earlier session. When the start
        // time can't be read (a server run as another account), trust the newest log.
        DateTimeOffset? started = process == null ? null : SafeStartTime(process);
        bool logIsCurrent = process != null && log.CurrentFile != null &&
                            (started == null || log.FileCreatedAt >= started.Value.AddSeconds(-30));
        string phase = logIsCurrent ? log.Phase : null;

        if (process == null)
        {
            state = ServerState.Stopped;
        }
        else if (phase == "serving")
        {
            state = now - log.LastWriteAt > SilentLimit ? ServerState.NotResponding : ServerState.Running;
            if (state == ServerState.NotResponding) problem = "silent";
            lock (gate)
            {
                if (launched != null && SafeId(launched) == SafeId(process)) seenServingSinceLaunch = true;
            }
        }
        else if (phase == "stopping")
        {
            state = ServerState.Stopping;
        }
        else
        {
            DateTimeOffset since = started ?? (logIsCurrent ? log.FileCreatedAt : now);
            state = now - since < StartupGrace ? ServerState.Starting : ServerState.NotResponding;
            if (state == ServerState.NotResponding) problem = logIsCurrent ? "stuck_loading" : "no_log";
        }

        var next = new ServerSnapshot(
            state,
            logIsCurrent ? log.Save : null,
            logIsCurrent ? log.PasswordSet : null,
            logIsCurrent && state != ServerState.Stopped ? log.Players : Array.Empty<LivePlayer>(),
            problem,
            now);

        process?.Dispose();
        lock (gate) snapshot = next;
        return next;
    }

    public bool TryStart(string user, out string error) =>
        TryRunOperation("start", user, token => StartServerAsync(user, token), out error);

    public bool TryStop(string user, int seconds, out string error) =>
        TryRunOperation("stop", user, token => StopServerAsync(user, seconds, restart: false, token), out error);

    public bool TryRestart(string user, int seconds, out string error) =>
        TryRunOperation("restart", user, token => StopServerAsync(user, seconds, restart: true, token), out error);

    public bool TryForceStop(string user, out string error) =>
        TryRunOperation("kill", user, token => ForceStopAsync(user, token), out error);

    /// <summary>Looks up the player's controller id with coop.debug.players.list, then runs coop.unstuck.</summary>
    public async Task<(bool Ok, string Code, string Message)> UnstuckAsync(string user, string playerName, CancellationToken token)
    {
        try
        {
            IReadOnlyList<string> listing = await RunCommandAsync("coop.debug.players.list", token);
            string controllerId = FindControllerId(listing, playerName);
            if (controllerId == null)
            {
                activity.Add(user, "unstuck_failed", $"{playerName}: not registered", ActivityKind.Error);
                return (false, "player_not_found", null);
            }

            IReadOnlyList<string> answer = await RunCommandAsync($"coop.unstuck {controllerId}", token);
            bool ok = answer.Any(line => line.Contains("Unstuck player", StringComparison.Ordinal));
            string message = Summarize(answer);
            activity.Add(user, ok ? "unstuck_ok" : "unstuck_failed", ok ? playerName : $"{playerName}: {message}",
                ok ? ActivityKind.Success : ActivityKind.Error);
            return (ok, ok ? null : "unstuck_refused", message);
        }
        catch (OperationFailedException failure)
        {
            activity.Add(user, "unstuck_failed", $"{playerName}: {failure.Code}", ActivityKind.Error);
            return (false, failure.Code, failure.Detail);
        }
    }

    public async Task<(bool Ok, string Code, string Message)> CancelShutdownAsync(string user, CancellationToken token)
    {
        try
        {
            IReadOnlyList<string> answer = await RunCommandAsync("coop.server.shutdown cancel", token);
            bool ok = answer.Any(line => line.Contains("shutdown cancelled", StringComparison.OrdinalIgnoreCase) ||
                                         line.Contains("shutdown cleared", StringComparison.OrdinalIgnoreCase));
            string message = Summarize(answer);
            activity.Add(user, ok ? "cancel_ok" : "cancel_failed", ok ? null : message,
                ok ? ActivityKind.Success : ActivityKind.Error);
            return (ok, ok ? null : "cancel_refused", message);
        }
        catch (OperationFailedException failure)
        {
            activity.Add(user, "cancel_failed", failure.Code, ActivityKind.Error);
            return (false, failure.Code, failure.Detail);
        }
    }

    /// <summary>Launch arguments for BannerlordCoopServer.exe.</summary>
    public static List<string> BuildArguments(PanelSettings settings)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.DataDir))
        {
            arguments.Add("--data-dir");
            arguments.Add(settings.DataDir.Trim());
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

    /// <summary>Quotes one argument per Windows command-line rules.</summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c == ' ' || c == '\t' || c == '"')) return argument;

        var builder = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            builder.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            builder.Append(c);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>Finds the ControllerId under "- PlayerName: name" in coop.debug.players.list output.</summary>
    public static string FindControllerId(IReadOnlyList<string> listing, string playerName)
    {
        string current = null;
        foreach (string line in listing)
        {
            Match name = PlayerNameLine.Match(line);
            if (name.Success)
            {
                current = name.Groups[1].Value;
                continue;
            }

            Match id = ControllerIdLine.Match(line);
            if (id.Success && current != null && string.Equals(current, playerName, StringComparison.OrdinalIgnoreCase))
                return id.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Types <paramref name="command"/> into the server window and returns what it printed in reply:
    /// the lines after its "&gt; command" echo, without the server's periodic noise.
    /// </summary>
    private async Task<IReadOnlyList<string>> RunCommandAsync(string command, CancellationToken token)
    {
        await typing.WaitAsync(token);
        try
        {
            int pid = FindServerPid();
            if (pid == 0) throw new OperationFailedException("not_running");

            log.Poll();
            long mark = log.Mark;

            TypeResult typed = await Task.Run(() => typer.Type(pid, command), token);
            switch (typed)
            {
                case TypeResult.AccessDenied:
                    throw new OperationFailedException("needs_admin");
                case TypeResult.NoConsole:
                case TypeResult.Failed:
                    throw new OperationFailedException("type_failed");
            }

            string echo = "> " + command;
            DateTimeOffset echoDeadline = DateTimeOffset.Now + EchoTimeout;
            int echoIndex = -1;
            IReadOnlyList<string> seen = Array.Empty<string>();
            while (DateTimeOffset.Now < echoDeadline)
            {
                await Task.Delay(150, token);
                log.Poll();
                seen = log.LinesSince(mark);
                echoIndex = IndexOfEcho(seen, echo);
                if (echoIndex >= 0) break;
            }
            if (echoIndex < 0) throw new OperationFailedException("no_echo");

            // The answer is printed right after the echo; stop once the log has been quiet briefly.
            DateTimeOffset answerDeadline = DateTimeOffset.Now + AnswerWindow;
            DateTimeOffset lastGrowth = DateTimeOffset.Now;
            int count = seen.Count;
            while (DateTimeOffset.Now < answerDeadline && DateTimeOffset.Now - lastGrowth < AnswerQuiet)
            {
                await Task.Delay(100, token);
                log.Poll();
                seen = log.LinesSince(mark);
                if (seen.Count != count)
                {
                    count = seen.Count;
                    lastGrowth = DateTimeOffset.Now;
                }
            }
            echoIndex = IndexOfEcho(seen, echo);

            var answer = new List<string>();
            for (int i = echoIndex + 1; i < seen.Count; i++)
            {
                string line = seen[i];
                if (line.StartsWith("[DedicatedServer] > ", StringComparison.Ordinal)) break;
                if (IsNoise(line)) continue;
                answer.Add(line);
            }
            return answer;
        }
        finally
        {
            typing.Release();
        }
    }

    private static int IndexOfEcho(IReadOnlyList<string> lines, string echo)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.EndsWith(echo, StringComparison.Ordinal) && line.Contains("[DedicatedServer]", StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>Periodic lines that can land between a command and its answer.</summary>
    private static bool IsNoise(string line) =>
        line.StartsWith("[DedicatedServer] pulse:", StringComparison.Ordinal) ||
        line.StartsWith("[Coop] Packet profile", StringComparison.Ordinal) ||
        line.StartsWith("[Coop] Time control request", StringComparison.Ordinal) ||
        Regex.IsMatch(line, @"^\[\d\d:\d\d:\d\d\.\d{3}\]");

    private static string Summarize(IReadOnlyList<string> answer)
    {
        string text = string.Join(" ", answer.Take(3).Select(line => line.Trim()));
        return text.Length > 300 ? text.Substring(0, 300) + "…" : text;
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
                activity.Add(user, failure.Code, failure.Detail, failure.Kind);
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
                activity.Add(user, "unexpected_error", exception.Message, ActivityKind.Error);
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
        if (Refresh().State != ServerState.Stopped)
            throw new OperationFailedException("already_running");

        string exe = settings.ServerExe;
        if (!File.Exists(exe))
            throw new OperationFailedException("exe_missing", exe);
        if (!LogFileEnabled)
            throw new OperationFailedException("log_disabled");

        string save = ConfiguredSave;
        activity.Add(user, "start_requested", save);
        Progress("starting", save);

        // Its own window, like a double-click, so the host can still see and type into it.
        var startInfo = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = true,
            Arguments = string.Join(" ", BuildArguments(settings).Select(QuoteArgument)),
        };

        Process process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
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
            seenServingSinceLaunch = false;
            expectingExit = false;
        }

        DateTimeOffset deadline = DateTimeOffset.Now + StartupGrace;
        while (DateTimeOffset.Now < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);

            if (HasExited(process))
            {
                int code = SafeExitCode(process);
                throw new OperationFailedException(code == ExitVerificationFailed ? "start_verification" : "start_exited",
                    code.ToString(CultureInfo.InvariantCulture));
            }

            ServerSnapshot state = Refresh();
            if (state.State == ServerState.Running)
            {
                activity.Add(user, "started", state.Save ?? save, ActivityKind.Success);
                Progress("started", state.Save ?? save);
                return;
            }
            Progress("starting", state.Save ?? save);
        }

        throw new OperationFailedException("start_timeout");
    }

    private async Task StopServerAsync(string user, int seconds, bool restart, CancellationToken token)
    {
        ServerSnapshot current = Refresh();
        if (current.State == ServerState.Stopped) throw new OperationFailedException("not_running");
        if (current.State == ServerState.NotResponding) throw new OperationFailedException("not_responding");

        activity.Add(user, restart ? "restart_requested" : "stop_requested",
            seconds.ToString(CultureInfo.InvariantCulture));

        // Warn, close joins and disconnect everyone first; the server's stop then saves and exits.
        if (seconds > 0 && current.State == ServerState.Running && current.Players.Count > 0)
            await WarnAndDrainAsync(seconds, token);

        Progress("closing");
        lock (gate) expectingExit = true;
        int pid = FindServerPid();
        await RunCommandAsync("stop", token);

        DateTimeOffset deadline = DateTimeOffset.Now + StopTimeout;
        while (DateTimeOffset.Now < deadline && IsAlive(pid))
        {
            await Task.Delay(500, token);
            if (Refresh().State == ServerState.Stopping) Progress("saving");
        }
        if (IsAlive(pid)) throw new OperationFailedException("stop_timeout");

        activity.Add(user, "stopped", null, ActivityKind.Success);
        Progress("stopped");

        if (restart) await StartServerAsync(user, token);
    }

    private async Task WarnAndDrainAsync(int seconds, CancellationToken token)
    {
        long mark = log.Mark;
        DateTimeOffset scheduledAt = DateTimeOffset.Now;
        IReadOnlyList<string> answer = await RunCommandAsync(
            $"coop.server.shutdown {seconds.ToString(CultureInfo.InvariantCulture)}", token);
        if (!answer.Any(line => line.Contains("shutdown scheduled", StringComparison.OrdinalIgnoreCase)))
            throw new OperationFailedException("shutdown_refused", Summarize(answer));

        DateTimeOffset deadline = scheduledAt + TimeSpan.FromSeconds(seconds) + ShutdownMargin;
        while (true)
        {
            await Task.Delay(1000, token);
            if (DateTimeOffset.Now > deadline) throw new OperationFailedException("shutdown_timeout");

            ServerSnapshot state = Refresh();
            if (state.State == ServerState.Stopped) return;

            IReadOnlyList<string> lines = log.LinesSince(mark);
            if (lines.Any(line => line.Contains("Server shutdown complete", StringComparison.Ordinal))) return;
            if (lines.Any(line => line.Contains("Server shutdown cancelled", StringComparison.Ordinal)))
                throw new OperationFailedException("stop_cancelled", null, ActivityKind.Info);
            string failed = lines.LastOrDefault(line => line.Contains("Server shutdown failed", StringComparison.Ordinal));
            if (failed != null) throw new OperationFailedException("save_failed", failed);

            if (lines.Any(line => line.Contains("Server shutdown: saving", StringComparison.Ordinal)))
            {
                Progress("saving");
                continue;
            }

            int left = seconds - (int)(DateTimeOffset.Now - scheduledAt).TotalSeconds;
            Progress(left > 0 ? "countdown" : "draining",
                left > 0 ? left.ToString(CultureInfo.InvariantCulture) : null);
        }
    }

    private async Task ForceStopAsync(string user, CancellationToken token)
    {
        activity.Add(user, "kill_requested");
        int pid = FindServerPid();
        if (pid == 0) throw new OperationFailedException("not_running");

        Progress("closing");
        lock (gate) expectingExit = true;
        try
        {
            using var process = Process.GetProcessById(pid);
            // The launcher runs the engine as a child; end both.
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Already gone.
        }
        catch (Win32Exception)
        {
            throw new OperationFailedException("needs_admin");
        }

        DateTimeOffset deadline = DateTimeOffset.Now + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.Now < deadline && IsAlive(pid)) await Task.Delay(500, token);
        if (IsAlive(pid)) throw new OperationFailedException("kill_failed");

        activity.Add(user, "killed", null, ActivityKind.Success);
        Progress("killed");
    }

    private void OnLaunchedExited(object sender, EventArgs e)
    {
        var process = (Process)sender;
        int exitCode = SafeExitCode(process);
        bool wasServing;
        lock (gate)
        {
            if (!ReferenceEquals(process, launched) || expectingExit) return;
            wasServing = seenServingSinceLaunch;
        }

        // 0 is a stop typed into the window or F10; that's the host's own choice.
        if (exitCode == 0)
        {
            activity.Add("panel", "stopped_outside");
            return;
        }

        // Exits during startup are reported by the start itself.
        if (!wasServing) return;

        activity.Add("panel", "crash_detected", exitCode.ToString(CultureInfo.InvariantCulture), ActivityKind.Error);
        if (!settings.RestartOnCrash) return;

        lock (crashTimes)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            crashTimes.RemoveAll(time => now - time > CrashWindow);
            if (crashTimes.Count >= MaxCrashRestarts)
            {
                activity.Add("panel", "crash_giveup", null, ActivityKind.Error);
                return;
            }
            crashTimes.Add(now);
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            if (!TryStart("panel", out _)) activity.Add("panel", "crash_restart_skipped");
        });
    }

    /// <summary>The server process: the one the panel started, else any running BannerlordCoopServer.</summary>
    private Process FindServer()
    {
        lock (gate)
        {
            if (launched != null && !HasExited(launched))
            {
                try
                {
                    return Process.GetProcessById(SafeId(launched));
                }
                catch (ArgumentException)
                {
                    // Exited in between.
                }
            }
        }

        Process[] found = Process.GetProcessesByName(ProcessName);
        Process alive = null;
        foreach (Process process in found)
        {
            if (alive == null && !HasExited(process)) alive = process;
            else process.Dispose();
        }
        return alive;
    }

    private int FindServerPid()
    {
        using Process process = FindServer();
        return process == null ? 0 : SafeId(process);
    }

    private static bool IsAlive(int pid)
    {
        if (pid == 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !HasExited(process);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private string ReadConfiguredSave()
    {
        JsonElement? config = ReadConfig();
        return config is JsonElement root && root.TryGetProperty("saveName", out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private bool? ReadConfigBool(string name)
    {
        JsonElement? config = ReadConfig();
        return config is JsonElement root && root.TryGetProperty(name, out var value) &&
               value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private JsonElement? ReadConfig()
    {
        string path = Path.Combine(settings.EffectiveDataDir, "server-config.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True while running, and also when the process can't be inspected (another account).</summary>
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
        catch (Win32Exception)
        {
            return false;
        }
    }

    private static DateTimeOffset? SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
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
        typing.Dispose();
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
