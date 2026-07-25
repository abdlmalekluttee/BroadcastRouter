using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Components;
using BroadcastRouter.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "BroadcastRouter");

var dataSetting = builder.Configuration["DataDirectory"] ?? "data";
var dataDirectory = Path.IsPathRooted(dataSetting) ? dataSetting : Path.Combine(AppContext.BaseDirectory, dataSetting);
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "broadcast-router.db");
var bootstrapStore = new SqliteDataStore(databasePath);
await bootstrapStore.InitializeAsync();
var persistedSettings = await bootstrapStore.LoadSettingsAsync();
var bindAddress = string.IsNullOrWhiteSpace(persistedSettings.Security.BindAddress) ? "127.0.0.1" : persistedSettings.Security.BindAddress.Trim();
var bindPort = Math.Clamp(persistedSettings.Security.Port, 1, 65535);
var scheme = persistedSettings.Security.HttpsEnabled ? "https" : "http";
builder.WebHost.UseUrls($"{scheme}://{bindAddress}:{bindPort}");
var requireAuthentication = persistedSettings.Security.RequireAuthentication || builder.Configuration.GetValue("Security:RequireAuthentication", false);
var sessionMinutes = Math.Clamp(persistedSettings.Security.SessionTimeoutMinutes, 5, 1440);
if (requireAuthentication && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BROADCASTROUTER_ADMIN_PASSWORD")))
    throw new InvalidOperationException("Authentication is enabled, but BROADCASTROUTER_ADMIN_PASSWORD is not configured. Startup is refused.");

builder.Services.AddSingleton(bootstrapStore);
builder.Services.AddSingleton<RouterCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RouterCoordinator>());
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DetailedErrors = false);
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromMinutes(sessionMinutes);
    options.SlidingExpiration = true;
    options.Cookie.Name = "BroadcastRouter.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAuthorization(options =>
{
    if (requireAuthentication)
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    if (remote is not null && !IPAddress.IsLoopback(remote) && !IsAllowed(remote, persistedSettings.Security.AllowedNetworks))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Client network is not allowed.");
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseAntiforgery();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (!requireAuthentication && context.User.Identity?.IsAuthenticated != true)
    {
        var claims = new[] { new Claim(ClaimTypes.Name, "Local operator"), new Claim(ClaimTypes.Role, "Administrator") };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Localhost"));
    }
    await next();
});
app.UseAuthorization();

app.MapGet("/health", async (SqliteDataStore store, RouterCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var integrity = await store.IntegrityCheckAsync(cancellationToken);
    var snapshot = coordinator.Snapshot;
    return Results.Ok(new { status = integrity == "ok" ? "healthy" : "degraded", database = integrity, snapshot.UpdatedAt, snapshot.SimulationMode, snapshot.EmergencyStopped });
}).AllowAnonymous();

app.MapPost("/auth/login", async (HttpContext context) =>
{
    if (!requireAuthentication) return Results.Redirect("/");
    var form = await context.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var configuredPassword = Environment.GetEnvironmentVariable("BROADCASTROUTER_ADMIN_PASSWORD");
    var configuredReadOnlyPassword = Environment.GetEnvironmentVariable("BROADCASTROUTER_OPERATOR_PASSWORD");
    var isAdmin = username.Equals("admin", StringComparison.OrdinalIgnoreCase) && FixedEquals(password, configuredPassword);
    var isOperator = username.Equals("operator", StringComparison.OrdinalIgnoreCase) && FixedEquals(password, configuredReadOnlyPassword);
    if (!isAdmin && !isOperator) return Results.Redirect("/login?failed=1");
    var claims = new[] { new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, isAdmin ? "Administrator" : "Operator") };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Redirect("/");
}).AllowAnonymous().DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

var diagnosticsEndpoint = app.MapGet("/diagnostics/package", async (SqliteDataStore store, RouterCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"BroadcastRouter-diagnostics-{Guid.NewGuid():N}.zip");
    var backup = Path.Combine(Path.GetTempPath(), $"BroadcastRouter-db-{Guid.NewGuid():N}.db");
    await store.BackupAsync(backup, cancellationToken);
    try
    {
        await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
            WriteJson(archive, "runtime-snapshot.json", coordinator.Snapshot);
            WriteJson(archive, "sanitized-settings.json", coordinator.GetSanitizedSettings());
            WriteJson(archive, "recent-logs.json", await store.ReadLogsAsync(limit: 1000, cancellationToken: cancellationToken));
            WriteText(archive, "database-integrity.txt", await store.IntegrityCheckAsync(cancellationToken));
            archive.CreateEntryFromFile(backup, "broadcast-router.db");
        }
        var bytes = await File.ReadAllBytesAsync(temp, cancellationToken);
        return Results.File(bytes, "application/zip", $"BroadcastRouter-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
    }
    finally
    {
        TryDelete(temp);
        TryDelete(backup);
    }
});
if (requireAuthentication) diagnosticsEndpoint.RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });

app.MapHub<StatusHub>("/hubs/status");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

static bool FixedEquals(string supplied, string? configured)
{
    if (string.IsNullOrEmpty(configured)) return false;
    var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var right = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    return CryptographicOperations.FixedTimeEquals(left, right);
}

static void WriteJson<T>(ZipArchive archive, string name, T value) =>
    WriteText(archive, name, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

static void WriteText(ZipArchive archive, string name, string value)
{
    using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open());
    writer.Write(value);
}

static void TryDelete(string path) { try { File.Delete(path); } catch { } }

static bool IsAllowed(IPAddress address, string configured)
{
    foreach (var token in configured.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var parts = token.Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var network)) continue;
        var prefix = parts.Length == 2 && int.TryParse(parts[1], out var value) ? value : network.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var normalizedNetwork = network.IsIPv4MappedToIPv6 ? network.MapToIPv4() : network;
        var left = candidate.GetAddressBytes();
        var right = normalizedNetwork.GetAddressBytes();
        if (left.Length != right.Length || prefix < 0 || prefix > left.Length * 8) continue;
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;
        if (!left.AsSpan(0, fullBytes).SequenceEqual(right.AsSpan(0, fullBytes))) continue;
        if (remainingBits == 0 || (left[fullBytes] & (byte)(0xff << (8 - remainingBits))) == (right[fullBytes] & (byte)(0xff << (8 - remainingBits)))) return true;
    }
    return false;
}
