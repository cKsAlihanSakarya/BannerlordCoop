using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoopServerPanel;

/// <summary>
/// panelsettings.json beside the exe. Created with defaults and a random panel password on the
/// first run, so the panel never starts without a password. What the server hosts (save, join
/// password, port) stays in the dedicated server's own server-config.json.
/// </summary>
public sealed class PanelSettings
{
    public const string FileName = "panelsettings.json";

    /// <summary>Password friends type to sign in to the panel.</summary>
    public string PanelPassword { get; set; } = "";

    /// <summary>Address the panel listens on. 0.0.0.0 accepts other computers.</summary>
    public string ListenUrl { get; set; } = "http://0.0.0.0:8090";

    /// <summary>Full path of the dedicated server's BannerlordCoopServer.exe.</summary>
    public string ServerExe { get; set; } =
        @"C:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3770450698\DedicatedServer\BannerlordCoopServer.exe";

    /// <summary>
    /// The server's data folder (server-config.json, saves, logs). Empty: the server's default,
    /// Documents\Mount and Blade II Bannerlord\CoopData\DedicatedServer.
    /// </summary>
    public string DataDir { get; set; } = "";

    /// <summary>Default warning players get before a stop or restart.</summary>
    public int WarningSeconds { get; set; } = 60;

    /// <summary>Start the server again when it exits on its own after it was serving.</summary>
    public bool RestartOnCrash { get; set; } = true;

    /// <summary>Extra launch arguments appended verbatim, for advanced setups.</summary>
    public string ExtraArguments { get; set; } = "";

    /// <summary>The Discord bot's token. Empty turns the bot off.</summary>
    public string DiscordToken { get; set; } = "";

    /// <summary>The Discord server (guild) the bot's slash commands are registered in.</summary>
    public string DiscordGuildId { get; set; } = "";

    /// <summary>Roles whose members may use the commands. Empty: nobody may.</summary>
    public string[] DiscordRoleIds { get; set; } = Array.Empty<string>();

    /// <summary>Channel for start, stop and crash notices. Empty: no notices.</summary>
    public string DiscordChannelId { get; set; } = "";

    /// <summary>Also post when a player joins or leaves.</summary>
    public bool DiscordNotifyPlayers { get; set; } = true;

    /// <summary>Test hook: the Discord REST base. Leave at the default.</summary>
    public string DiscordApiBase { get; set; } = "https://discord.com/api/v10";

    /// <summary>Test hook: the Discord gateway address. Leave at the default.</summary>
    public string DiscordGatewayUrl { get; set; } = "wss://gateway.discord.gg/?v=10&encoding=json";

    [JsonIgnore]
    public bool DiscordEnabled => !string.IsNullOrWhiteSpace(DiscordToken);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Keeps paths such as "Mount & Blade" readable when the file is edited by hand.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The data folder in use: the configured one, else the server's default.</summary>
    [JsonIgnore]
    public string EffectiveDataDir => !string.IsNullOrWhiteSpace(DataDir)
        ? DataDir.Trim()
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord", "CoopData", "DedicatedServer");

    /// <summary>Loads the file, creating it on first run. <paramref name="created"/> reports that.</summary>
    public static PanelSettings LoadOrCreate(string path, out bool created, out bool passwordGenerated)
    {
        created = false;
        passwordGenerated = false;
        PanelSettings settings;

        if (File.Exists(path))
        {
            settings = JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        else
        {
            settings = new PanelSettings();
            created = true;
        }

        if (string.IsNullOrWhiteSpace(settings.PanelPassword))
        {
            settings.PanelPassword = GeneratePassword();
            passwordGenerated = true;
        }

        if (created || passwordGenerated)
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));

        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        if (WarningSeconds < 0 || WarningSeconds > 3600)
            throw new InvalidDataException("WarningSeconds must be from 0 through 3600.");
        if (string.IsNullOrWhiteSpace(ServerExe))
            throw new InvalidDataException("ServerExe must be the path of BannerlordCoopServer.exe.");
        if (PanelPassword.Length < 6)
            throw new InvalidDataException("PanelPassword must be at least 6 characters.");
        if (DiscordEnabled && !ulong.TryParse(DiscordGuildId, out _))
            throw new InvalidDataException("DiscordGuildId must be the server ID when DiscordToken is set.");
        DiscordRoleIds ??= Array.Empty<string>();
    }

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 10);
    }
}
