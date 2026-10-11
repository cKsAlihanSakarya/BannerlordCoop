using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoopServerPanel;

/// <summary>
/// panelsettings.json beside the exe. Created with defaults and a random panel password on the
/// first run, so the panel never starts without a password.
/// </summary>
public sealed class PanelSettings
{
    public const string FileName = "panelsettings.json";

    /// <summary>Password friends type to sign in to the panel.</summary>
    public string PanelPassword { get; set; } = "";

    /// <summary>Address the panel listens on. 0.0.0.0 accepts other computers.</summary>
    public string ListenUrl { get; set; } = "http://0.0.0.0:8090";

    /// <summary>Full path of Bannerlord.exe (bin\Win64_Shipping_Client).</summary>
    public string BannerlordExe { get; set; } =
        @"C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe";

    /// <summary>Modules the server loads, in load order. Must match what players run.</summary>
    public string[] Modules { get; set; } = { "Native", "SandBoxCore", "Sandbox", "CustomBattle", "StoryMode", "Coop" };

    /// <summary>
    /// Save the server hosts, as shown in the save list (no .sav). Empty: reuse the save the
    /// running server reported, if the panel has seen one.
    /// </summary>
    public string SaveName { get; set; } = "";

    /// <summary>Join password for players. Empty for none.</summary>
    public string ServerPassword { get; set; } = "";

    /// <summary>public, friends_only or none.</summary>
    public string Visibility { get; set; } = "friends_only";

    /// <summary>The server's UDP port; it also names the admin pipe.</summary>
    public int GamePort { get; set; } = 4200;

    /// <summary>Default warning players get before a stop or restart.</summary>
    public int WarningSeconds { get; set; } = 60;

    /// <summary>Start the server again when it exits without being stopped from the panel.</summary>
    public bool RestartOnCrash { get; set; } = true;

    /// <summary>Extra launch arguments appended verbatim, for advanced setups.</summary>
    public string ExtraArguments { get; set; } = "";

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
        if (GamePort < 1 || GamePort > 65535)
            throw new InvalidDataException("GamePort must be from 1 through 65535.");
        if (WarningSeconds < 0 || WarningSeconds > 3600)
            throw new InvalidDataException("WarningSeconds must be from 0 through 3600.");
        if (Visibility is not ("public" or "friends_only" or "none"))
            throw new InvalidDataException("Visibility must be public, friends_only or none.");
        if (Modules == null || Modules.Length == 0)
            throw new InvalidDataException("Modules must list at least one module.");
        if (PanelPassword.Length < 6)
            throw new InvalidDataException("PanelPassword must be at least 6 characters.");
    }

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return RandomNumberGenerator.GetString(alphabet, 10);
    }
}
