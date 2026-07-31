using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using BroadcastRouter.Infrastructure;
using BroadcastRouter.Web.Components;
using BroadcastRouter.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "BroadcastRouter");
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
    ConfigureWindowsEventLog(builder);

var dataSetting = builder.Configuration["DataDirectory"] ?? "data";
var dataDirectory = Path.IsPathRooted(dataSetting) ? dataSetting : Path.Combine(AppContext.BaseDirectory, dataSetting);
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "broadcast-router.db");
var deckLinkAssetDirectory = Path.Combine(dataDirectory, "decklink-assets");
var bootstrapStore = new SqliteDataStore(databasePath);
await bootstrapStore.InitializeAsync();
var persistedSettings = await bootstrapStore.LoadSettingsAsync();
var bindAddress = string.IsNullOrWhiteSpace(persistedSettings.Security.BindAddress) ? "127.0.0.1" : persistedSettings.Security.BindAddress.Trim();
var bindPort = Math.Clamp(persistedSettings.Security.Port, 1, 65535);
var scheme = persistedSettings.Security.HttpsEnabled ? "https" : "http";
builder.WebHost.UseUrls($"{scheme}://{bindAddress}:{bindPort}");
var requireAuthentication = persistedSettings.Security.RequireAuthentication || builder.Configuration.GetValue("Security:RequireAuthentication", false);
NetworkAccessPolicy.ValidateExposure(bindAddress, requireAuthentication);
var trustedProxies = NetworkAccessPolicy.ParseTrustedProxies(persistedSettings.Security.TrustedProxies);
var sessionMinutes = Math.Clamp(persistedSettings.Security.SessionTimeoutMinutes, 5, 1440);
if (requireAuthentication && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BROADCASTROUTER_ADMIN_PASSWORD")))
    throw new InvalidOperationException("Authentication is enabled, but BROADCASTROUTER_ADMIN_PASSWORD is not configured. Startup is refused.");

builder.Services.AddSingleton(bootstrapStore);
builder.Services.AddSingleton(new DeckLinkAssetCatalog(deckLinkAssetDirectory));
builder.Services.AddHttpClient<DeckLinkSoftwareInformationProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddSingleton<RouterCoordinator>();
builder.Services.AddSingleton<BrowserPreviewSupervisor>();
builder.Services.AddHostedService<ServiceLifecycleReporter>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RouterCoordinator>());
builder.Services.AddScoped<AuthorizedRouterCommands>();
builder.Services.AddScoped<AuthorizedPreviewCommands>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => options.DetailedErrors = false);
builder.Services.AddSignalR();
builder.Services.AddHttpClient("WowzaValidated");
builder.Services.AddHttpClient("WowzaInsecure")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/error", createScopeForErrors: true);
if (!app.Environment.IsDevelopment() && persistedSettings.Security.HttpsEnabled) app.UseHsts();
const string RawPeerAddressKey = "BroadcastRouter.RawPeerAddress";
app.Use(async (context, next) =>
{
    context.Items[RawPeerAddressKey] = context.Connection.RemoteIpAddress;
    await next();
});
if (trustedProxies.Count > 0)
{
    var forwardedOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    };
    forwardedOptions.KnownNetworks.Clear();
    forwardedOptions.KnownProxies.Clear();
    foreach (var proxy in trustedProxies) forwardedOptions.KnownProxies.Add(proxy);
    app.UseForwardedHeaders(forwardedOptions);
}
app.Use(async (context, next) =>
{
    var rawPeer = context.Items[RawPeerAddressKey] as IPAddress;
    var effectiveClient = context.Connection.RemoteIpAddress;
    if (rawPeer is null || effectiveClient is null || !NetworkAccessPolicy.IsClientAllowed(rawPeer, effectiveClient, persistedSettings.Security.AllowedNetworks))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Client network is not allowed.");
        return;
    }
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
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

var previewStreamEndpoint = app.MapGet("/preview/stream/{token}", async (
    HttpContext context,
    string token,
    BrowserPreviewSupervisor preview,
    CancellationToken cancellationToken) =>
{
    context.Response.ContentType = "video/mp4";
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    try
    {
        await preview.CopyStreamToAsync(token, context.Response.Body, cancellationToken);
    }
    catch (InvalidOperationException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsync(LogRedactor.Redact(exception.Message), cancellationToken);
    }
});
previewStreamEndpoint.RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });

var deckLinkAssetEndpoint = app.MapGet("/hardware-assets/decklink/{slug}/{kind}", (
    HttpContext context,
    string slug,
    string kind,
    DeckLinkAssetCatalog assets) =>
{
    if (!assets.TryGetAsset(slug, kind, out var asset) || asset is null) return Results.NotFound();
    context.Response.Headers.CacheControl = "private, max-age=3600";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return Results.File(asset.FullPath, asset.ContentType, enableRangeProcessing: false);
});
deckLinkAssetEndpoint.RequireAuthorization();

app.MapGet("/health", async (SqliteDataStore store, CancellationToken cancellationToken) =>
{
    var integrity = await store.IntegrityCheckAsync(cancellationToken);
    return Results.Ok(new { status = integrity == "ok" ? "healthy" : "degraded" });
}).AllowAnonymous();

app.MapPost("/auth/login", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsAntiforgeryValidAsync(context, antiforgery)) return Results.BadRequest();
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
}).AllowAnonymous().RequireRateLimiting("login");

app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await IsAntiforgeryValidAsync(context, antiforgery)) return Results.BadRequest();
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

var diagnosticsEndpoint = app.MapGet("/diagnostics/package", async (SqliteDataStore store, RouterCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"BroadcastRouter-diagnostics-{Guid.NewGuid():N}.zip");
    FileStream? file = null;
    try
    {
        file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJson(archive, "runtime-snapshot.json", DiagnosticSanitizer.SanitizeSnapshot(coordinator.Snapshot));
            WriteJson(archive, "sanitized-settings.json", DiagnosticSanitizer.SanitizeSettings(coordinator.GetSettings()));
            WriteJson(archive, "recent-logs.json", DiagnosticSanitizer.SanitizeLogs(await store.ReadLogsAsync(limit: 1000, cancellationToken: cancellationToken)));
            WriteJson(archive, "configuration-audit.json", DiagnosticSanitizer.SanitizeConfigurationAudit(
                await store.ReadConfigurationAuditAsync(limit: 1000, cancellationToken: cancellationToken)));
            WriteText(archive, "database-integrity.txt", await store.IntegrityCheckAsync(cancellationToken));
        }
        file.Position = 0;
        return Results.File(file, "application/zip", $"BroadcastRouter-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
    }
    catch
    {
        if (file is not null) await file.DisposeAsync();
        TryDelete(temp);
        throw;
    }
});
diagnosticsEndpoint.RequireAuthorization(new AuthorizeAttribute { Roles = "Administrator" });

app.MapHub<StatusHub>("/hubs/status").RequireAuthorization();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

static bool FixedEquals(string supplied, string? configured)
{
    if (string.IsNullOrEmpty(configured)) return false;
    var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
    var right = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
    return CryptographicOperations.FixedTimeEquals(left, right);
}

[SupportedOSPlatform("windows")]
static void ConfigureWindowsEventLog(WebApplicationBuilder builder)
{
#pragma warning disable CA1416 // Guarded by OperatingSystem.IsWindows and service-mode detection at the only call site.
    builder.Logging.AddEventLog(settings =>
    {
        settings.LogName = "Application";
        settings.SourceName = "BroadcastRouter";
        settings.Filter = (category, level) =>
            category.StartsWith("BroadcastRouter", StringComparison.Ordinal)
            && level >= LogLevel.Information;
    });
#pragma warning restore CA1416
}

static void WriteJson<T>(ZipArchive archive, string name, T value) =>
    WriteText(archive, name, JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

static void WriteText(ZipArchive archive, string name, string value)
{
    using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Optimal).Open());
    writer.Write(value);
}

static void TryDelete(string path) { try { File.Delete(path); } catch { } }

static async Task<bool> IsAntiforgeryValidAsync(HttpContext context, IAntiforgery antiforgery)
{
    try { await antiforgery.ValidateRequestAsync(context); return true; }
    catch (AntiforgeryValidationException) { return false; }
}
