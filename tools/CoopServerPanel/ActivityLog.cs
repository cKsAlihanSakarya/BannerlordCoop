namespace CoopServerPanel;

public enum ActivityKind
{
    Info,
    Success,
    Error,
}

/// <summary>
/// One line of the activity list. <paramref name="Code"/> is translated by the page;
/// <paramref name="Detail"/> is shown as written (a player name, seconds, or the server's own message).
/// </summary>
public sealed record ActivityEntry(DateTimeOffset At, string User, string Code, string Detail, ActivityKind Kind);

/// <summary>Recent panel actions, newest first, so friends can see who did what.</summary>
public sealed class ActivityLog
{
    private const int Capacity = 100;

    private readonly LinkedList<ActivityEntry> entries = new();
    private readonly object gate = new();
    private readonly ILogger<ActivityLog> logger;

    public ActivityLog(ILogger<ActivityLog> logger)
    {
        this.logger = logger;
    }

    public void Add(string user, string code, string detail = null, ActivityKind kind = ActivityKind.Info)
    {
        var entry = new ActivityEntry(DateTimeOffset.Now, user, code, detail, kind);
        lock (gate)
        {
            entries.AddFirst(entry);
            while (entries.Count > Capacity) entries.RemoveLast();
        }

        if (kind == ActivityKind.Error)
            logger.LogWarning("{User}: {Code} {Detail}", user, code, detail);
        else
            logger.LogInformation("{User}: {Code} {Detail}", user, code, detail);
    }

    public IReadOnlyList<ActivityEntry> Recent(int count)
    {
        lock (gate)
        {
            return entries.Take(count).ToArray();
        }
    }
}
