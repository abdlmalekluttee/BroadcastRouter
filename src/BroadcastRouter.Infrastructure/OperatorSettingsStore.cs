using System.Text.Json;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed class OperatorSettingsStore(string? path = null)
{
    public string FilePath { get; } = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BroadcastRouter", "operator-settings.json");

    public async Task<OperatorSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return new OperatorSettings();
        await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<OperatorSettings>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? new OperatorSettings();
    }

    public async Task SaveAsync(OperatorSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(FilePath) ?? throw new InvalidOperationException("Settings path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        var backup = FilePath + ".bak";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true }, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, backup, ignoreMetadataErrors: true);
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
