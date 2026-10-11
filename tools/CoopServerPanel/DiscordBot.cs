using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CoopServerPanel;

/// <summary>
/// Discord slash commands for the same actions as the web page, plus notices in a channel when
/// the server starts, stops or crashes. Only members with one of the configured roles may use
/// the commands. Replies are in Turkish, the group's language.
/// </summary>
public sealed class DiscordBot : BackgroundService
{
    private const int InteractionCommand = 2;
    private const int InteractionAutocomplete = 4;
    private const int ReplyMessage = 4;
    private const int ReplyDeferred = 5;
    private const int ReplyAutocomplete = 8;
    private const int FlagEphemeral = 64;
    private const int OptionString = 3;
    private const int OptionInteger = 4;

    private readonly PanelSettings settings;
    private readonly ServerSupervisor supervisor;
    private readonly ActivityLog activity;
    private readonly ILogger<DiscordBot> logger;
    private DiscordRest rest;
    private DiscordGateway gateway;
    private string applicationId;
    private CancellationToken stopping;

    public DiscordBot(PanelSettings settings, ServerSupervisor supervisor, ActivityLog activity, ILogger<DiscordBot> logger)
    {
        this.settings = settings;
        this.supervisor = supervisor;
        this.activity = activity;
        this.logger = logger;
    }

    private volatile string setupProblem;

    /// <summary>Shown on the web page: off, connecting, connected, or why Discord refused.</summary>
    public string Status => !settings.DiscordEnabled ? "off"
        : gateway?.FatalError ?? setupProblem ?? (gateway?.Connected == true ? "connected" : "connecting");

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!settings.DiscordEnabled) return;
        stopping = token;
        rest = new DiscordRest(settings);

        // The token has to work before anything else does.
        while (applicationId == null && !token.IsCancellationRequested)
        {
            try
            {
                JsonElement? app = await rest.SendAsync(HttpMethod.Get, "/applications/@me", null, token);
                applicationId = app?.GetProperty("id").GetString();
            }
            catch (DiscordException exception) when (exception.Status == 401)
            {
                setupProblem = "token_rejected";
                logger.LogError("Discord rejected DiscordToken (401). Copy the token again from the Developer Portal (Bot, Reset Token).");
                return;
            }
            catch (Exception exception) when (exception is DiscordException or HttpRequestException or TaskCanceledException
                                                  && !token.IsCancellationRequested)
            {
                setupProblem = "unreachable";
                logger.LogWarning("Discord can't be reached, retrying in 30 s: {Message}", exception.Message);
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
        }
        if (applicationId == null) return;
        setupProblem = null;

        activity.Added += OnActivity;
        Task players = settings.DiscordNotifyPlayers && HasChannel ? WatchPlayersAsync(token) : Task.CompletedTask;
        // The bot comes online even when the commands are refused, so the cause shows up here.
        Task commands = RegisterCommandsAsync(token);

        gateway = new DiscordGateway(settings, logger, OnDispatchAsync);
        await gateway.RunAsync(token);
        activity.Added -= OnActivity;
        await players;
        await commands;
    }

    /// <summary>Registers the slash commands, retrying every 2 minutes until Discord accepts them.</summary>
    private async Task RegisterCommandsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await rest.SendAsync(HttpMethod.Put,
                    $"/applications/{applicationId}/guilds/{settings.DiscordGuildId.Trim()}/commands", Commands(), token);
                setupProblem = null;
                logger.LogInformation("Discord commands registered in server {Guild}", settings.DiscordGuildId);
                return;
            }
            catch (DiscordException exception) when (exception.Status is 403 or 404)
            {
                setupProblem = "commands_rejected";
                logger.LogError("Discord refused the slash commands in server {Guild} ({Status}): {Message}. Check DiscordGuildId, and invite the bot again with the applications.commands scope ticked.",
                    settings.DiscordGuildId, exception.Status, exception.Message);
            }
            catch (Exception exception) when (exception is DiscordException or HttpRequestException or TaskCanceledException
                                                  && !token.IsCancellationRequested)
            {
                logger.LogWarning("Discord command registration failed: {Message}", exception.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool HasChannel => ulong.TryParse(settings.DiscordChannelId?.Trim(), out _);

    private static JsonNode Commands()
    {
        JsonObject Command(string name, string description, params JsonObject[] options)
        {
            var command = new JsonObject { ["name"] = name, ["description"] = description, ["type"] = 1 };
            if (options.Length > 0) command["options"] = new JsonArray(options);
            return command;
        }

        JsonObject Warning() => new()
        {
            ["type"] = OptionInteger,
            ["name"] = "uyari",
            ["description"] = "Kaç saniye önce oyuncular uyarılsın (0 = hemen)",
            ["required"] = false,
            ["min_value"] = 0,
            ["max_value"] = 3600,
        };

        JsonObject Player() => new()
        {
            ["type"] = OptionString,
            ["name"] = "oyuncu",
            ["description"] = "Sunucudaki oyuncu",
            ["required"] = true,
            ["autocomplete"] = true,
        };

        return new JsonArray(
            Command("durum", "Sunucu açık mı, kimler oynuyor"),
            Command("baslat", "Sunucuyu başlatır"),
            Command("yeniden-baslat", "Oyuncuları uyarır, kaydeder, sunucuyu kapatıp tekrar açar", Warning()),
            Command("kapat", "Oyuncuları uyarır, kaydeder ve sunucuyu kapatır", Warning()),
            Command("iptal", "Geri sayımdaki kapatmayı iptal eder"),
            Command("zorla-kapat", "Donan sunucuyu kayıt yapmadan kapatır", new JsonObject
            {
                ["type"] = OptionString,
                ["name"] = "onay",
                ["description"] = "Son kayıttan sonraki ilerleme kaybolur",
                ["required"] = true,
                ["choices"] = new JsonArray(new JsonObject { ["name"] = "Evet, kayıt yapmadan kapat", ["value"] = "evet" }),
            }),
            Command("unstuck", "Takılan oyuncuyu kurtarır", Player()),
            Command("at", "Oyuncuyu sunucudan atar", Player()),
            Command("duyuru", "Oyundaki herkese mesaj gönderir", new JsonObject
            {
                ["type"] = OptionString,
                ["name"] = "mesaj",
                ["description"] = "Gönderilecek mesaj",
                ["required"] = true,
                ["max_length"] = 200,
            }),
            Command("kaydet", "Dünyayı şimdi kaydeder"));
    }

    private async Task OnDispatchAsync(string type, JsonElement data)
    {
        if (type != "INTERACTION_CREATE") return;
        if (GetString(data, "guild_id") != settings.DiscordGuildId.Trim()) return;

        int kind = data.TryGetProperty("type", out var t) ? t.GetInt32() : 0;
        string id = GetString(data, "id");
        string token = GetString(data, "token");
        JsonElement command = data.GetProperty("data");
        string name = GetString(command, "name");

        if (kind == InteractionAutocomplete)
        {
            await Callback(id, token, new JsonObject
            {
                ["type"] = ReplyAutocomplete,
                ["data"] = new JsonObject { ["choices"] = PlayerChoices(FocusedValue(command)) },
            });
            return;
        }
        if (kind != InteractionCommand) return;

        if (!IsAllowed(data))
        {
            await Reply(id, token, settings.DiscordRoleIds.Length == 0
                ? "Komutları kimin kullanabileceği ayarlanmamış. Host panelsettings.json içindeki DiscordRoleIds'e bir rol ID'si yazmalı."
                : "Bu komut için gereken role sahip değilsin.", ephemeral: true);
            return;
        }

        string user = DisplayName(data) + " (Discord)";
        switch (name)
        {
            case "durum":
                await Reply(id, token, DescribeStatus());
                break;
            case "baslat":
                await Reply(id, token, Start(user));
                break;
            case "yeniden-baslat":
            case "kapat":
                await Reply(id, token, Stop(user, IntOption(command, "uyari") ?? settings.WarningSeconds, name == "yeniden-baslat"));
                break;
            case "zorla-kapat":
                await Reply(id, token, supervisor.TryForceStop(user, out var killError)
                    ? "Sunucu kayıt yapmadan kapatılıyor."
                    : Explain(killError));
                break;
            case "iptal":
                await Deferred(id, token, async () =>
                {
                    var (ok, code, message) = await supervisor.CancelShutdownAsync(user, stopping);
                    return ok ? "Kapatma iptal edildi, sunucu açık kalıyor." : Failure(code, message);
                });
                break;
            case "unstuck":
                string stuck = StringOption(command, "oyuncu");
                await Deferred(id, token, async () =>
                {
                    var (ok, code, message) = await supervisor.UnstuckAsync(user, stuck, stopping);
                    return ok ? $"**{stuck}** kurtarıldı.\n{Quote(message)}" : Failure(code, message);
                });
                break;
            case "at":
                string kicked = StringOption(command, "oyuncu");
                LivePlayer player = supervisor.Snapshot.Players
                    .FirstOrDefault(p => string.Equals(p.Name, kicked, StringComparison.OrdinalIgnoreCase));
                if (player == null)
                {
                    await Reply(id, token, $"**{kicked}** şu an sunucuda değil.", ephemeral: true);
                    break;
                }
                await Deferred(id, token, async () =>
                {
                    var (ok, code, message) = await supervisor.KickAsync(user, player.Id, player.Name, stopping);
                    return ok ? $"**{player.Name}** sunucudan atıldı." : Failure(code, message);
                });
                break;
            case "duyuru":
                string text = new string((StringOption(command, "mesaj") ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
                if (text.Length == 0)
                {
                    await Reply(id, token, "Mesaj boş olamaz.", ephemeral: true);
                    break;
                }
                await Deferred(id, token, async () =>
                {
                    var (ok, code, message) = await supervisor.SayAsync(user, text, stopping);
                    return ok ? $"Duyuru gönderildi: {text}" : Failure(code, message);
                });
                break;
            case "kaydet":
                await Deferred(id, token, async () =>
                {
                    var (ok, code, message) = await supervisor.SaveAsync(user, stopping);
                    return ok ? "Dünya kaydedildi." : Failure(code, message);
                });
                break;
        }
    }

    private string Start(string user)
    {
        ServerSnapshot snapshot = supervisor.Snapshot;
        if (snapshot.State != ServerState.Stopped) return $"Sunucu zaten {StateText(snapshot.State)}.";
        return supervisor.TryStart(user, out var error)
            ? "Sunucu başlatılıyor. Açılınca bu kanala haber vereceğim."
            : Explain(error);
    }

    private string Stop(string user, int seconds, bool restart)
    {
        ServerSnapshot snapshot = supervisor.Snapshot;
        if (snapshot.State != ServerState.Running)
            return snapshot.State == ServerState.NotResponding
                ? "Sunucu yanıt vermiyor. Takıldıysa `/zorla-kapat` kullan."
                : $"Sunucu şu an {StateText(snapshot.State)}, kapatılacak durumda değil.";

        bool started = restart
            ? supervisor.TryRestart(user, seconds, out var error)
            : supervisor.TryStop(user, seconds, out error);
        if (!started) return Explain(error);

        string warning = seconds == 0 || snapshot.Players.Count == 0
            ? "Oyuncu uyarısı olmadan"
            : $"Oyuncular {seconds} saniye önceden uyarılarak";
        return restart
            ? $"{warning} yeniden başlatılıyor. Kayıt alınıp tekrar açılınca haber vereceğim."
            : $"{warning} kapatılıyor. Önce dünya kaydedilecek.";
    }

    private string DescribeStatus()
    {
        ServerSnapshot snapshot = supervisor.Snapshot;
        var text = new StringBuilder();
        text.Append("**Sunucu ").Append(StateText(snapshot.State)).Append("**");
        string save = snapshot.Save ?? supervisor.ConfiguredSave;
        if (save != null) text.Append("\nKayıt: ").Append(save);

        if (snapshot.State == ServerState.Running)
        {
            if (snapshot.Players.Count == 0)
                text.Append("\nŞu an kimse oynamıyor.");
            else
                text.Append($"\nOyuncular ({snapshot.Players.Count}): ")
                    .Append(string.Join(", ", snapshot.Players.Select(p => $"{p.Name} ({PlayerStateText(p.State)})")));
        }

        OperationInfo operation = supervisor.Operation;
        if (operation is { Running: true })
            text.Append("\nŞu an: ").Append(ProgressText(operation));
        return text.ToString();
    }

    private void OnActivity(ActivityEntry entry)
    {
        if (!HasChannel) return;

        string text = entry.Code switch
        {
            "started" => $"🟢 Sunucu açıldı ({entry.Detail}).",
            "stopped" => "Sunucu kaydedildi ve kapatıldı.",
            "killed" => "Sunucu kayıt yapmadan zorla kapatıldı.",
            "stopped_outside" => "Sunucu kendi penceresinden kapatıldı.",
            "restart_requested" => $"{entry.User} sunucuyu yeniden başlatıyor ({entry.Detail} sn uyarı).",
            "stop_requested" => $"{entry.User} sunucuyu kapatıyor ({entry.Detail} sn uyarı).",
            "kill_requested" => $"{entry.User} sunucuyu zorla kapatıyor.",
            "start_requested" when entry.User == "panel" => "Sunucu çöktükten sonra tekrar başlatılıyor.",
            "crash_detected" => $"⚠️ Sunucu beklenmedik şekilde kapandı (çıkış kodu {entry.Detail})."
                                + (settings.RestartOnCrash ? " 15 saniye sonra tekrar açılacak." : ""),
            "crash_giveup" => "⚠️ Sunucu 15 dakikada 3 kez çöktü, otomatik başlatma durduruldu. Host'un bakması lazım.",
            "start_verification" => "⚠️ Sunucu açılamadı: Coop modu doğrulanamadı (çıkış kodu 4). Mod dosyaları Steam'den doğrulanmalı.",
            "start_exited" => $"⚠️ Sunucu açılırken kapandı (çıkış kodu {entry.Detail}).",
            "start_timeout" => "⚠️ Sunucu 10 dakikada açılmadı.",
            "start_failed" or "exe_missing" => $"⚠️ Sunucu başlatılamadı: {entry.Detail}",
            "save_failed" or "shutdown_timeout" or "stop_timeout" => $"⚠️ Kapatma tamamlanamadı, sunucu açık kaldı. {entry.Detail}",
            _ => null,
        };
        if (text == null) return;

        _ = Task.Run(() => PostAsync(text));
    }

    /// <summary>Posts joins and leaves by comparing the player list every few seconds.</summary>
    private async Task WatchPlayersAsync(CancellationToken token)
    {
        HashSet<string> previous = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ServerSnapshot snapshot = supervisor.Snapshot;
            if (snapshot.State != ServerState.Running)
            {
                // A stop or restart drops everyone at once; the stop notice already says so.
                previous = null;
                continue;
            }

            var current = snapshot.Players.Where(p => p.State == "on map").Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (previous != null)
            {
                foreach (string joined in current.Except(previous, StringComparer.OrdinalIgnoreCase))
                    await PostAsync($"**{joined}** oyuna girdi.");
                foreach (string left in previous.Except(current, StringComparer.OrdinalIgnoreCase))
                    await PostAsync($"**{left}** oyundan çıktı.");
            }
            previous = current;
        }
    }

    private async Task PostAsync(string text)
    {
        try
        {
            await rest.SendAsync(HttpMethod.Post, $"/channels/{settings.DiscordChannelId.Trim()}/messages", Message(text), stopping);
        }
        catch (Exception exception) when (exception is DiscordException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Discord notice failed: {Message}", exception.Message);
        }
    }

    private bool IsAllowed(JsonElement interaction)
    {
        if (settings.DiscordRoleIds.Length == 0) return false;
        if (!interaction.TryGetProperty("member", out var member) || !member.TryGetProperty("roles", out var roles))
            return false;

        var allowed = settings.DiscordRoleIds.Select(r => r.Trim()).ToHashSet();
        return roles.EnumerateArray().Any(role => allowed.Contains(role.GetString()));
    }

    private JsonArray PlayerChoices(string typed)
    {
        typed ??= "";
        var choices = new JsonArray();
        foreach (LivePlayer player in supervisor.Snapshot.Players
                     .Where(p => p.Name != "(joining)" && p.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
                     .Take(25))
        {
            choices.Add(new JsonObject { ["name"] = $"{player.Name} ({PlayerStateText(player.State)})", ["value"] = player.Name });
        }
        return choices;
    }

    private Task Reply(string id, string token, string text, bool ephemeral = false)
    {
        JsonObject data = Message(text);
        if (ephemeral) data["flags"] = FlagEphemeral;
        return Callback(id, token, new JsonObject { ["type"] = ReplyMessage, ["data"] = data });
    }

    /// <summary>Answers "thinking…" at once, because commands typed into the server can take longer than Discord's 3 s.</summary>
    private async Task Deferred(string id, string token, Func<Task<string>> work)
    {
        await Callback(id, token, new JsonObject { ["type"] = ReplyDeferred });

        string text;
        try
        {
            text = await work();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Discord command failed");
            text = "Beklenmeyen bir hata oldu: " + exception.Message;
        }

        try
        {
            await rest.SendAsync(HttpMethod.Patch, $"/webhooks/{applicationId}/{token}/messages/@original", Message(text), stopping);
        }
        catch (Exception exception) when (exception is DiscordException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Discord reply failed: {Message}", exception.Message);
        }
    }

    private async Task Callback(string id, string token, JsonObject body)
    {
        try
        {
            await rest.SendAsync(HttpMethod.Post, $"/interactions/{id}/{token}/callback", body, stopping);
        }
        catch (Exception exception) when (exception is DiscordException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Discord reply failed: {Message}", exception.Message);
        }
    }

    /// <summary>A message that never pings anyone, even if a player name looks like a mention.</summary>
    private static JsonObject Message(string text) => new()
    {
        ["content"] = text.Length > 1900 ? text.Substring(0, 1900) + "…" : text,
        ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
    };

    private static string Quote(string text) =>
        string.IsNullOrWhiteSpace(text) ? "" : "> " + text.Replace("\n", "\n> ");

    private static string Failure(string code, string message)
    {
        string reason = Explain(code);
        return string.IsNullOrWhiteSpace(message) ? reason : reason + "\n" + Quote(message);
    }

    private static string Explain(string code) => code switch
    {
        "busy" => "Şu an başka bir işlem sürüyor, bitince tekrar dene.",
        "needs_admin" => "Sunucu yönetici olarak çalışıyor ama panel değil. Host paneli yönetici olarak açmalı.",
        "type_failed" => "Sunucu penceresine komut yazılamadı.",
        "no_echo" => "Komut yazıldı ama sunucu tepki vermedi. Sunucu donmuş olabilir.",
        "not_running" => "Sunucu çalışmıyor.",
        "player_not_found" => "Bu oyuncu sunucunun kayıtlı oyuncuları arasında yok.",
        "unstuck_refused" => "Sunucu kurtarmayı reddetti.",
        "kick_refused" => "Sunucu atmayı reddetti.",
        "cancel_refused" => "İptal edilecek bir kapatma yok.",
        "save_refused" => "Kayıt başarısız oldu.",
        "say_refused" => "Duyuru gönderilemedi.",
        null => "Bir şey ters gitti.",
        _ => "Olmadı: " + code,
    };

    private static string StateText(ServerState state) => state switch
    {
        ServerState.Running => "açık",
        ServerState.Starting => "açılıyor",
        ServerState.Stopping => "kapanıyor",
        ServerState.NotResponding => "yanıt vermiyor",
        _ => "kapalı",
    };

    private static string PlayerStateText(string state) => state switch
    {
        "on map" => "oyunda",
        "loading" => "yükleniyor",
        "creating character" => "karakter oluşturuyor",
        "handshake" => "bağlanıyor",
        _ => state,
    };

    private static string ProgressText(OperationInfo operation) => operation.Code switch
    {
        "countdown" => $"oyuncular uyarıldı, {operation.Detail} sn sonra kapanacak",
        "draining" => "oyuncular çıkarılıyor",
        "saving" => "kaydediliyor",
        "closing" => "sunucu kapanıyor",
        "starting" or "start_requested" => "sunucu açılıyor",
        _ => operation.Name,
    };

    private static string DisplayName(JsonElement interaction)
    {
        if (interaction.TryGetProperty("member", out var member))
        {
            string nick = GetString(member, "nick");
            if (!string.IsNullOrEmpty(nick)) return nick;
            if (member.TryGetProperty("user", out var user))
                return GetString(user, "global_name") ?? GetString(user, "username") ?? "?";
        }
        return "?";
    }

    private static string FocusedValue(JsonElement command)
    {
        if (!command.TryGetProperty("options", out var options)) return "";
        foreach (JsonElement option in options.EnumerateArray())
        {
            if (option.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True)
                return option.TryGetProperty("value", out var value) ? value.ToString() : "";
        }
        return "";
    }

    private static string StringOption(JsonElement command, string name)
    {
        if (!command.TryGetProperty("options", out var options)) return null;
        foreach (JsonElement option in options.EnumerateArray())
            if (GetString(option, "name") == name) return option.TryGetProperty("value", out var value) ? value.ToString() : null;
        return null;
    }

    private static int? IntOption(JsonElement command, string name)
    {
        if (!command.TryGetProperty("options", out var options)) return null;
        foreach (JsonElement option in options.EnumerateArray())
        {
            if (GetString(option, "name") == name && option.TryGetProperty("value", out var value) &&
                value.TryGetInt32(out int number))
                return number;
        }
        return null;
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public override void Dispose()
    {
        rest?.Dispose();
        base.Dispose();
    }
}
