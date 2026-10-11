using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoopServerPanel;

/// <summary>A player as the server's own player list reports it.</summary>
public sealed record LivePlayer(int Id, string Name, string State);

/// <summary>
/// Follows the newest logs\coop-server-*.log the dedicated server writes (one per launch, with
/// "logFile": true in server-config.json). The server prints machine-readable lines there, such
/// as <c>@DS@{"ev":"state","phase":"serving",...}</c> and <c>@DS@{"ev":"players","list":[...]}</c>,
/// which give the phase and player list without typing anything into the server.
/// </summary>
public sealed class ServerLog
{
    private const int KeptLines = 4000;
    private const string EventMarker = "@DS@{";
    private static readonly Regex Timestamp = new(@"^\d\d:\d\d:\d\d\.\d{3}  ", RegexOptions.CultureInvariant);

    private readonly string logsDir;
    private readonly object gate = new();
    private readonly LinkedList<(long Seq, string Text)> lines = new();
    private readonly StringBuilder partial = new();
    private string file;
    private long position;
    private long seq;

    public ServerLog(string dataDir)
    {
        logsDir = Path.Combine(dataDir, "logs");
    }

    public string LogsDir => logsDir;

    /// <summary>The log file being followed, or null when there is none yet.</summary>
    public string CurrentFile { get { lock (gate) return file; } }

    /// <summary>boot, loading, serving or stopping; null before the first state line.</summary>
    public string Phase { get; private set; }
    public string Save { get; private set; }
    public bool? PasswordSet { get; private set; }
    public IReadOnlyList<LivePlayer> Players { get; private set; } = Array.Empty<LivePlayer>();

    /// <summary>When the followed file last grew; the server prints a pulse every ~15 s while serving.</summary>
    public DateTimeOffset LastWriteAt { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>When the followed file was created (the server's launch time).</summary>
    public DateTimeOffset FileCreatedAt { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Sequence number of the newest line read; lines after it are "new".</summary>
    public long Mark { get { lock (gate) return seq; } }

    /// <summary>Reads whatever the server appended since the last call, switching to a newer file when one appears.</summary>
    public void Poll()
    {
        lock (gate)
        {
            string newest = FindNewestFile();
            if (newest == null) return;

            if (!string.Equals(newest, file, StringComparison.OrdinalIgnoreCase))
            {
                file = newest;
                position = 0;
                partial.Clear();
                Phase = null;
                Save = null;
                PasswordSet = null;
                Players = Array.Empty<LivePlayer>();
                FileCreatedAt = File.GetCreationTime(newest);
            }

            ReadAppended();
        }
    }

    /// <summary>Lines read after <paramref name="mark"/>, oldest first.</summary>
    public IReadOnlyList<string> LinesSince(long mark)
    {
        lock (gate)
        {
            return lines.Where(line => line.Seq > mark).Select(line => line.Text).ToArray();
        }
    }

    private string FindNewestFile()
    {
        if (!Directory.Exists(logsDir)) return null;

        // Names carry the launch time (coop-server-20261011-133252.log), so they sort by age.
        return Directory.EnumerateFiles(logsDir, "coop-server-*.log")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private void ReadAppended()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException)
        {
            return;
        }

        using (stream)
        {
            if (stream.Length < position)
            {
                // Truncated or replaced in place; start over.
                position = 0;
                partial.Clear();
            }
            if (stream.Length == position) return;

            stream.Seek(position, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - position];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
            position += total;
            LastWriteAt = DateTimeOffset.Now;

            partial.Append(Encoding.UTF8.GetString(buffer, 0, total));
        }

        string text = partial.ToString();
        int lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0) return;

        partial.Clear();
        partial.Append(text, lastNewline + 1, text.Length - lastNewline - 1);

        foreach (string raw in text.Substring(0, lastNewline).Split('\n'))
            Handle(raw.TrimEnd('\r'));
    }

    private void Handle(string raw)
    {
        string text = Timestamp.IsMatch(raw) ? raw.Substring(14) : raw;
        if (text.Length == 0) return;

        // Events can follow engine output on the same line, so look anywhere in it.
        int marker = text.IndexOf(EventMarker, StringComparison.Ordinal);
        if (marker >= 0)
        {
            HandleEvent(text.Substring(marker + EventMarker.Length - 1));
            text = text.Substring(0, marker).TrimEnd();
            if (text.Length == 0) return;
        }

        lines.AddLast((++seq, text));
        while (lines.Count > KeptLines) lines.RemoveFirst();
    }

    private void HandleEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            switch (GetString(root, "ev"))
            {
                case "state":
                    Phase = GetString(root, "phase") ?? Phase;
                    Save = GetString(root, "save") ?? Save;
                    if (root.TryGetProperty("pw", out var pw) && pw.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        PasswordSet = pw.GetBoolean();
                    break;
                case "players":
                    if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        Players = list.EnumerateArray()
                            .Select(player => new LivePlayer(
                                player.TryGetProperty("id", out var id) && id.TryGetInt32(out int value) ? value : -1,
                                GetString(player, "name") ?? "?",
                                GetString(player, "state") ?? ""))
                            .ToArray();
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // A cut-off or unknown event line; the next one will carry the current state.
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
