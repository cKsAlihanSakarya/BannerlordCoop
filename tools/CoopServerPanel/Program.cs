using CoopServerPanel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length > 0 && args[0] == WindowsConsoleTyper.HelperArgument)
    return WindowsConsoleTyper.RunHelper(args);

string settingsPath = args.Length > 0 ? Path.GetFullPath(args[0]) : PanelSettings.DefaultPath;
PanelSettings settings;
try
{
    settings = PanelSettings.LoadOrCreate(settingsPath, out bool created, out bool passwordGenerated);
    if (passwordGenerated)
    {
        Console.WriteLine($"Panel password: {settings.PanelPassword}  (saved in {settingsPath})");
    }
    if (created)
    {
        Console.WriteLine($"Created {settingsPath}. Check ServerExe there, then start the panel again.");
        return 0;
    }
}
catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Could not read {settingsPath}: {exception.Message}");
    return 1;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = Array.Empty<string>(),
    ContentRootPath = AppContext.BaseDirectory,
});
builder.WebHost.UseUrls(settings.ListenUrl);
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// Keys live in the user profile; the unencrypted-at-rest notice is expected for a local tool.
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(new ServerLog(settings.EffectiveDataDir));
builder.Services.AddSingleton<IConsoleTyper>(OperatingSystem.IsWindows()
    ? new WindowsConsoleTyper()
    : new FileConsoleTyper(settings.EffectiveDataDir));
builder.Services.AddSingleton<ActivityLog>();
builder.Services.AddSingleton<ServerSupervisor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ServerSupervisor>());
builder.Services.AddSingleton<DiscordBot>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<DiscordBot>());
builder.Services.AddSingleton<LoginThrottle>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "coop_panel";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        // The page handles a 401 itself; never redirect API calls to a login URL.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    // State-changing calls must carry a header a cross-site form cannot set.
    if (HttpMethods.IsPost(context.Request.Method) &&
        context.Request.Headers["X-Coop-Panel"] != "1")
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();

string page = ReadEmbeddedPage();
app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));

app.MapPost("/api/login", async (LoginRequest request, HttpContext context, LoginThrottle throttle, ActivityLog log) =>
{
    string address = ClientAddress(context);
    if (!throttle.IsAllowed(address))
        return Results.Json(new { error = "too_many_attempts" }, statusCode: StatusCodes.Status429TooManyRequests);

    string name = (request?.Name ?? "").Trim();
    if (name.Length is < 1 or > 24)
        return Results.Json(new { error = "name_required" }, statusCode: StatusCodes.Status400BadRequest);

    if (!PasswordMatches(request.Password ?? "", settings.PanelPassword))
    {
        throttle.RecordFailure(address);
        return Results.Json(new { error = "wrong_password" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    throttle.Reset(address);
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, name) },
        CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });
    log.Add(name, "signed_in");
    return Results.Json(new { name });
});

app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});

var api = app.MapGroup("/api").RequireAuthorization();

api.MapGet("/status", (HttpContext context, ServerSupervisor supervisor, ActivityLog log, DiscordBot discord) =>
{
    ServerSnapshot snapshot = supervisor.Snapshot;
    return Results.Json(new
    {
        user = context.User.Identity?.Name,
        state = snapshot.State,
        problem = snapshot.Problem,
        checkedAt = snapshot.CheckedAt,
        save = snapshot.Save,
        passwordSet = snapshot.PasswordSet,
        players = snapshot.Players,
        operation = supervisor.Operation,
        nextSave = supervisor.ConfiguredSave,
        logFileEnabled = supervisor.LogFileEnabled,
        warningSeconds = settings.WarningSeconds,
        discord = discord.Status,
        activity = log.Recent(30),
    });
});

api.MapPost("/start", (HttpContext context, ServerSupervisor supervisor) =>
    Accepted(supervisor.TryStart(UserOf(context), out var error), error));

api.MapPost("/stop", (DelayRequest request, HttpContext context, ServerSupervisor supervisor) =>
    ValidSeconds(request, out int seconds)
        ? Accepted(supervisor.TryStop(UserOf(context), seconds, out var error), error)
        : Results.Json(new { error = "bad_seconds" }, statusCode: StatusCodes.Status400BadRequest));

api.MapPost("/restart", (DelayRequest request, HttpContext context, ServerSupervisor supervisor) =>
    ValidSeconds(request, out int seconds)
        ? Accepted(supervisor.TryRestart(UserOf(context), seconds, out var error), error)
        : Results.Json(new { error = "bad_seconds" }, statusCode: StatusCodes.Status400BadRequest));

api.MapPost("/kill", (HttpContext context, ServerSupervisor supervisor) =>
    Accepted(supervisor.TryForceStop(UserOf(context), out var error), error));

api.MapPost("/cancel", async (HttpContext context, ServerSupervisor supervisor) =>
{
    var (ok, code, message) = await supervisor.CancelShutdownAsync(UserOf(context), context.RequestAborted);
    return Results.Json(new { ok, error = code, message });
});

api.MapPost("/unstuck", async (UnstuckRequest request, HttpContext context, ServerSupervisor supervisor) =>
{
    string name = request?.Name?.Trim();
    if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl))
        return Results.Json(new { error = "bad_player" }, statusCode: StatusCodes.Status400BadRequest);

    var (ok, code, message) = await supervisor.UnstuckAsync(UserOf(context), name, context.RequestAborted);
    return Results.Json(new { ok, error = code, message });
});

api.MapPost("/kick", async (KickRequest request, HttpContext context, ServerSupervisor supervisor) =>
{
    string name = request?.Name?.Trim();
    if (request?.Id is not int id || id < 0 || string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl))
        return Results.Json(new { error = "bad_player" }, statusCode: StatusCodes.Status400BadRequest);

    var (ok, code, message) = await supervisor.KickAsync(UserOf(context), id, name, context.RequestAborted);
    return Results.Json(new { ok, error = code, message });
});

api.MapPost("/say", async (SayRequest request, HttpContext context, ServerSupervisor supervisor) =>
{
    // One line only: a newline would end the command and type the rest as a second one.
    string text = new string((request?.Text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
    if (text.Length is < 1 or > 200)
        return Results.Json(new { error = "bad_message" }, statusCode: StatusCodes.Status400BadRequest);

    var (ok, code, message) = await supervisor.SayAsync(UserOf(context), text, context.RequestAborted);
    return Results.Json(new { ok, error = code, message });
});

api.MapPost("/save", async (HttpContext context, ServerSupervisor supervisor) =>
{
    var (ok, code, message) = await supervisor.SaveAsync(UserOf(context), context.RequestAborted);
    return Results.Json(new { ok, error = code, message });
});

app.Logger.LogInformation("Panel listening on {Url}", settings.ListenUrl);
await app.RunAsync();
return 0;

static string UserOf(HttpContext context) => context.User.Identity?.Name ?? "?";

// Through a Cloudflare tunnel every request arrives from this computer, so wrong-password
// lockouts would hit everyone at once. The tunnel names the real visitor in CF-Connecting-IP;
// only a local connection may claim that, otherwise anyone could pick their own address.
static string ClientAddress(HttpContext context)
{
    System.Net.IPAddress remote = context.Connection.RemoteIpAddress;
    string forwarded = context.Request.Headers["CF-Connecting-IP"];
    if (remote != null && System.Net.IPAddress.IsLoopback(remote) && !string.IsNullOrWhiteSpace(forwarded))
        return forwarded.Trim();
    return remote?.ToString() ?? "unknown";
}

static IResult Accepted(bool started, string error) => started
    ? Results.Json(new { ok = true }, statusCode: StatusCodes.Status202Accepted)
    : Results.Json(new { error }, statusCode: StatusCodes.Status409Conflict);

static bool ValidSeconds(DelayRequest request, out int seconds)
{
    seconds = request?.Seconds ?? -1;
    return seconds is >= 0 and <= 3600;
}

static bool PasswordMatches(string given, string expected)
{
    // Hashing first makes the comparison length-independent as well as constant-time.
    byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(given));
    byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
    return CryptographicOperations.FixedTimeEquals(a, b);
}

static string ReadEmbeddedPage()
{
    using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")
        ?? throw new InvalidOperationException("The panel page is missing from the build.");
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return reader.ReadToEnd();
}

record LoginRequest(string Name, string Password);
record DelayRequest(int? Seconds);
record UnstuckRequest(string Name);
record KickRequest(int? Id, string Name);
record SayRequest(string Text);

/// <summary>Five wrong passwords from one address lock it out for ten minutes.</summary>
sealed class LoginThrottle
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset First)> failures = new();

    public bool IsAllowed(string address)
    {
        if (!failures.TryGetValue(address, out var entry)) return true;
        if (DateTimeOffset.Now - entry.First > Window)
        {
            failures.TryRemove(address, out _);
            return true;
        }
        return entry.Count < MaxFailures;
    }

    public void RecordFailure(string address) =>
        failures.AddOrUpdate(address, _ => (1, DateTimeOffset.Now),
            (_, entry) => DateTimeOffset.Now - entry.First > Window ? (1, DateTimeOffset.Now) : (entry.Count + 1, entry.First));

    public void Reset(string address) => failures.TryRemove(address, out _);
}
