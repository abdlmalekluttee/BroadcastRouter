using System.Text.Json;
using BroadcastRouter.Infrastructure;

var options = args.Chunk(2)
    .Where(pair => pair.Length == 2)
    .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);

if (!options.TryGetValue("--database", out var databasePath) || string.IsNullOrWhiteSpace(databasePath))
    throw new ArgumentException("Missing required option --database.");

var store = new SqliteDataStore(databasePath);
await store.InitializeAsync();
var settings = await store.LoadSettingsAsync();

SetExisting(options, "--ffmpeg", value => settings.MediaTools.FfmpegPath = value);
SetExisting(options, "--ffprobe", value => settings.MediaTools.FfprobePath = value);
SetExisting(options, "--ffplay", value => settings.MediaTools.FfplayPath = value);

await store.SaveSettingsAsync(settings);
Console.WriteLine(JsonSerializer.Serialize(new
{
    configurationSaved = true,
    ffmpegConfigured = File.Exists(settings.MediaTools.FfmpegPath),
    ffprobeConfigured = File.Exists(settings.MediaTools.FfprobePath),
    ffplayConfigured = string.IsNullOrWhiteSpace(settings.MediaTools.FfplayPath) || File.Exists(settings.MediaTools.FfplayPath)
}, new JsonSerializerOptions { WriteIndented = true }));

static void SetExisting(IReadOnlyDictionary<string, string> options, string option, Action<string> setter)
{
    if (!options.TryGetValue(option, out var value)) return;
    var fullPath = Path.GetFullPath(value);
    if (!File.Exists(fullPath)) throw new FileNotFoundException($"The file supplied for {option} does not exist.", fullPath);
    setter(fullPath);
}
