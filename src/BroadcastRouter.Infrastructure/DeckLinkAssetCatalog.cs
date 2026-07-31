using System.Text.Json;
using System.Text.RegularExpressions;

namespace BroadcastRouter.Infrastructure;

public sealed record DeckLinkAssetReference(string Kind, string RelativePath, int Width, int Height);

public sealed record DeckLinkAssetMatch(
    string ModelName,
    string Slug,
    string Category,
    string ConnectionSummary,
    IReadOnlyDictionary<string, DeckLinkAssetReference> Assets)
{
    public bool HasAsset(string kind) => Assets.ContainsKey(kind);
    public string AssetUrl(string kind) => $"/hardware-assets/decklink/{Uri.EscapeDataString(Slug)}/{Uri.EscapeDataString(kind)}";
}

public sealed record DeckLinkAssetCatalogStatus(bool Installed, int ModelCount, string Message);

public sealed record DeckLinkAssetFile(string FullPath, string ContentType);

/// <summary>
/// Loads an optional, operator-supplied DeckLink visual asset pack from the application's data directory.
/// Asset metadata is presentation-only and never participates in device identity or routing decisions.
/// </summary>
public sealed partial class DeckLinkAssetCatalog
{
    private const long MaximumManifestBytes = 512 * 1024;
    private static readonly HashSet<string> SupportedKinds =
        new(StringComparer.OrdinalIgnoreCase) { "product", "connections", "physical", "accessories" };

    private readonly object gate = new();
    private readonly string assetRoot;
    private readonly string manifestPath;
    private DateTime lastManifestWriteUtc = DateTime.MinValue;
    private CatalogSnapshot snapshot = CatalogSnapshot.Missing("DeckLink visual asset pack is not installed.");

    public DeckLinkAssetCatalog(string assetRoot)
    {
        if (string.IsNullOrWhiteSpace(assetRoot)) throw new ArgumentException("An asset root is required.", nameof(assetRoot));
        this.assetRoot = Path.GetFullPath(assetRoot);
        manifestPath = Path.Combine(this.assetRoot, "manifest.min.json");
    }

    public DeckLinkAssetCatalogStatus Status
    {
        get
        {
            EnsureLoaded();
            lock (gate) return snapshot.Status;
        }
    }

    public DeckLinkAssetMatch? Match(string? detectedModelName)
    {
        if (string.IsNullOrWhiteSpace(detectedModelName)) return null;
        EnsureLoaded();
        var normalized = NormalizeModelName(detectedModelName);
        lock (gate) return snapshot.ByNormalizedName.GetValueOrDefault(normalized);
    }

    public bool TryGetAsset(string? slug, string? kind, out DeckLinkAssetFile? file)
    {
        file = null;
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(kind) || !SupportedKinds.Contains(kind)) return false;
        EnsureLoaded();
        DeckLinkAssetMatch? model;
        lock (gate) model = snapshot.BySlug.GetValueOrDefault(slug.Trim());
        if (model is null || !model.Assets.TryGetValue(kind, out var asset)) return false;
        var fullPath = ResolveSafeAssetPath(asset.RelativePath);
        if (fullPath is null || !File.Exists(fullPath)) return false;
        file = new(fullPath, ContentTypeFor(fullPath));
        return true;
    }

    public static string NormalizeModelName(string value)
    {
        var withoutSimulationSuffix = SimulationSuffix().Replace(value.Trim(), "");
        var withoutConnectorSuffix = ConnectorSuffix().Replace(withoutSimulationSuffix, "");
        var withoutVendor = VendorPrefix().Replace(withoutConnectorSuffix, "");
        return NonAlphaNumeric().Replace(withoutVendor, "").ToLowerInvariant();
    }

    private void EnsureLoaded()
    {
        DateTime writeTime;
        try { writeTime = File.Exists(manifestPath) ? File.GetLastWriteTimeUtc(manifestPath) : DateTime.MinValue; }
        catch { writeTime = DateTime.MinValue; }
        lock (gate)
        {
            if (writeTime == lastManifestWriteUtc && (writeTime != DateTime.MinValue || !snapshot.Status.Installed)) return;
            snapshot = LoadSnapshot();
            lastManifestWriteUtc = writeTime;
        }
    }

    private CatalogSnapshot LoadSnapshot()
    {
        if (!File.Exists(manifestPath)) return CatalogSnapshot.Missing("DeckLink visual asset pack is not installed.");
        try
        {
            var manifestInfo = new FileInfo(manifestPath);
            if (manifestInfo.Length is <= 0 or > MaximumManifestBytes)
                return CatalogSnapshot.Missing("DeckLink asset manifest has an invalid size.");
            using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var manifest = JsonSerializer.Deserialize<ManifestDocument>(stream, JsonOptions);
            if (manifest?.Models is null || manifest.Models.Count == 0)
                return CatalogSnapshot.Missing("DeckLink asset manifest contains no models.");

            var byName = new Dictionary<string, DeckLinkAssetMatch>(StringComparer.Ordinal);
            var bySlug = new Dictionary<string, DeckLinkAssetMatch>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.Models)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || !IsSafeSlug(item.Slug)) continue;
                var assets = new Dictionary<string, DeckLinkAssetReference>(StringComparer.OrdinalIgnoreCase);
                AddAsset(assets, "product", item.Assets?.Product);
                AddAsset(assets, "connections", item.Assets?.Connections);
                AddAsset(assets, "physical", item.Assets?.Physical);
                AddAsset(assets, "accessories", item.Assets?.Accessories);
                var match = new DeckLinkAssetMatch(item.Name.Trim(), item.Slug.Trim(), item.Category?.Trim() ?? "DeckLink",
                    item.Ports?.Trim() ?? "Connection details are not listed in the installed manifest.", assets);
                var normalized = NormalizeModelName(match.ModelName);
                if (normalized.Length == 0 || byName.ContainsKey(normalized) || bySlug.ContainsKey(match.Slug)) continue;
                byName.Add(normalized, match);
                bySlug.Add(match.Slug, match);
            }
            if (byName.Count == 0) return CatalogSnapshot.Missing("DeckLink asset manifest contains no valid model entries.");
            return new(byName, bySlug, new(true, byName.Count, $"DeckLink visual asset pack loaded ({byName.Count} models)."));
        }
        catch (JsonException)
        {
            return CatalogSnapshot.Missing("DeckLink asset manifest is not valid JSON.");
        }
        catch (IOException)
        {
            return CatalogSnapshot.Missing("DeckLink asset manifest could not be read.");
        }
        catch (UnauthorizedAccessException)
        {
            return CatalogSnapshot.Missing("DeckLink asset manifest is not readable by the application account.");
        }
    }

    private void AddAsset(Dictionary<string, DeckLinkAssetReference> target, string kind, ManifestAsset? candidate)
    {
        if (candidate is null || string.IsNullOrWhiteSpace(candidate.Path)) return;
        var fullPath = ResolveSafeAssetPath(candidate.Path);
        if (fullPath is null || !File.Exists(fullPath) || !IsSupportedImageExtension(fullPath)) return;
        target[kind] = new(kind, NormalizeRelativePath(candidate.Path), Math.Max(0, candidate.Width), Math.Max(0, candidate.Height));
    }

    private string? ResolveSafeAssetPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return null;
        var normalized = NormalizeRelativePath(relativePath);
        var fullPath = Path.GetFullPath(Path.Combine(assetRoot, normalized));
        var rootPrefix = assetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    private static string NormalizeRelativePath(string value) => value.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    private static bool IsSafeSlug(string? value) => !string.IsNullOrWhiteSpace(value) && SafeSlug().IsMatch(value.Trim());
    private static bool IsSupportedImageExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".webp";
    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private sealed record CatalogSnapshot(
        IReadOnlyDictionary<string, DeckLinkAssetMatch> ByNormalizedName,
        IReadOnlyDictionary<string, DeckLinkAssetMatch> BySlug,
        DeckLinkAssetCatalogStatus Status)
    {
        public static CatalogSnapshot Missing(string message) => new(
            new Dictionary<string, DeckLinkAssetMatch>(StringComparer.Ordinal),
            new Dictionary<string, DeckLinkAssetMatch>(StringComparer.OrdinalIgnoreCase),
            new(false, 0, message));
    }

    private sealed class ManifestDocument { public List<ManifestModel>? Models { get; set; } }
    private sealed class ManifestModel
    {
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? Category { get; set; }
        public string? Ports { get; set; }
        public ManifestAssets? Assets { get; set; }
    }
    private sealed class ManifestAssets
    {
        public ManifestAsset? Product { get; set; }
        public ManifestAsset? Connections { get; set; }
        public ManifestAsset? Physical { get; set; }
        public ManifestAsset? Accessories { get; set; }
    }
    private sealed class ManifestAsset
    {
        public string Path { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
    }

    [GeneratedRegex(@"\s*\(\d+\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectorSuffix();
    [GeneratedRegex(@"\s*\(simulation\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SimulationSuffix();
    [GeneratedRegex(@"^\s*(?:blackmagic(?:\s+design)?\s+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VendorPrefix();
    [GeneratedRegex(@"[^a-zA-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphaNumeric();
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeSlug();
}
