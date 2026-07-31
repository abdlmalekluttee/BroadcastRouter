using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BroadcastRouter.Infrastructure;

public sealed record DeckLinkSoftwareInformation(
    string? FirmwareVersion,
    string? DriverVersion,
    DateOnly? DriverInstalledOn,
    string? LatestDriverVersion,
    DateOnly? LatestDriverReleasedOn,
    bool? DriverUpdateAvailable,
    bool? FirmwareUpdateAvailable,
    DateTimeOffset? UpdateCheckedAt,
    string? UpdateCheckMessage = null)
{
    public static DeckLinkSoftwareInformation Unavailable { get; } =
        new(null, null, null, null, null, null, null, null);
}

public sealed partial class DeckLinkSoftwareInformationProvider(HttpClient httpClient)
{
    public const string OfficialSoftwareUrl =
        "https://www.blackmagicdesign.com/developer/products/capture-and-playback/sdk-and-software";

    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private OfficialDesktopVideoRelease? cachedRelease;
    private DateTimeOffset cachedAt;

    public DeckLinkSoftwareInformation GetInstalledInformation()
    {
        var installed = DetectInstalledDesktopVideo();
        return new(
            FirmwareVersion: null,
            DriverVersion: installed?.Version,
            DriverInstalledOn: installed?.InstalledOn,
            LatestDriverVersion: null,
            LatestDriverReleasedOn: null,
            DriverUpdateAvailable: null,
            FirmwareUpdateAvailable: null,
            UpdateCheckedAt: null);
    }

    public async Task<DeckLinkSoftwareInformation> GetInformationAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var installed = GetInstalledInformation();
        await refreshGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (forceRefresh || cachedRelease is null || now - cachedAt > TimeSpan.FromHours(6))
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, OfficialSoftwareUrl);
                    request.Headers.UserAgent.ParseAdd("BroadcastRouter/1.0 (DeckLink update check)");
                    request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
                    using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    cachedRelease = ParseOfficialReleasePage(html, now);
                    cachedAt = now;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or RegexMatchTimeoutException)
                {
                    return installed with
                    {
                        UpdateCheckedAt = now,
                        UpdateCheckMessage = "Official update information is temporarily unavailable."
                    };
                }
            }

            var release = cachedRelease;
            return installed with
            {
                LatestDriverVersion = release?.Version,
                LatestDriverReleasedOn = release?.ReleasedOn,
                DriverUpdateAvailable = CompareVersions(installed.DriverVersion, release?.Version),
                UpdateCheckedAt = cachedAt,
                UpdateCheckMessage = release is null ? "Official update information is not available." : null
            };
        }
        finally
        {
            refreshGate.Release();
        }
    }

    internal static OfficialDesktopVideoRelease ParseOfficialReleasePage(string html, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException("The official Desktop Video page returned no content.");

        var match = DesktopVideoReleaseRegex().Match(html);
        if (!match.Success)
            throw new InvalidOperationException("The official Desktop Video release could not be identified.");

        var version = WebUtility.HtmlDecode(match.Groups["version"].Value).Trim();
        var dateText = WebUtility.HtmlDecode(StripTagsRegex().Replace(match.Groups["date"].Value, " ")).Trim();
        return new(version, ParseReleaseDate(dateText, now));
    }

    internal static bool? CompareVersions(string? installed, string? latest)
    {
        if (!TryParseVersion(installed, out var installedVersion) || !TryParseVersion(latest, out var latestVersion))
            return null;
        return latestVersion > installedVersion;
    }

    private static InstalledDesktopVideo? DetectInstalledDesktopVideo()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = localMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subkeyName in uninstall.GetSubKeyNames())
                {
                    using var product = uninstall.OpenSubKey(subkeyName);
                    var name = product?.GetValue("DisplayName") as string;
                    if (name is null || !name.Contains("Blackmagic Desktop Video", StringComparison.OrdinalIgnoreCase)) continue;
                    var version = Normalize(product?.GetValue("DisplayVersion") as string);
                    var installedOn = ParseInstallDate(product?.GetValue("InstallDate") as string);
                    return new(version, installedOn);
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
        return null;
    }

    private static DateOnly? ParseInstallDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static DateOnly? ParseReleaseDate(string value, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (value.Equals("Today", StringComparison.OrdinalIgnoreCase)) return today;
        if (value.Equals("Yesterday", StringComparison.OrdinalIgnoreCase)) return today.AddDays(-1);
        if (value.StartsWith("Last ", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<DayOfWeek>(value[5..].Trim(), true, out var weekday))
        {
            var daysBack = ((int)today.DayOfWeek - (int)weekday + 7) % 7;
            return today.AddDays(-(daysBack == 0 ? 7 : daysBack));
        }

        string[] formats = ["dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy", "MMMM d, yyyy"];
        return DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = VersionRegex().Match(value);
        return match.Success && Version.TryParse(match.Value, out version!);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("file-download-title[^>]*>\\s*Desktop\\s+Video\\s+(?<version>\\d+(?:\\.\\d+){1,3})\\s*</h4>\\s*<p[^>]*class=\\\"release-date\\\"[^>]*>(?<date>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DesktopVideoReleaseRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StripTagsRegex();

    [GeneratedRegex("\\d+(?:\\.\\d+){1,3}", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex VersionRegex();

    private sealed record InstalledDesktopVideo(string? Version, DateOnly? InstalledOn);
}

public sealed record OfficialDesktopVideoRelease(string Version, DateOnly? ReleasedOn);
