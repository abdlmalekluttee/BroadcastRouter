using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Infrastructure;

public sealed record PreparedStandbySlate(string Path, string Key, int Width, int Height, long ConfigurationGeneration = 0);

public sealed class StandbySlateCache
{
    private const int MaxImageBytes = 32 * 1024 * 1024;
    private readonly string _root;
    private readonly long _maximumBytes;
    private readonly Func<ProcessStartInfo, CancellationToken, Task> _render;
    private readonly SemaphoreSlim _preparation = new(1, 1);
    private readonly Dictionary<string, (long Length, long Modified, string Hash)> _toolHashes = new(StringComparer.OrdinalIgnoreCase);
    public StandbySlateCache(string root) : this(root, RenderAsync) { }
    internal StandbySlateCache(string root, Func<ProcessStartInfo, CancellationToken, Task> render, long maximumBytes = 268435456)
    { _root = Path.GetFullPath(root); _render = render; _maximumBytes = maximumBytes; }

    public Task<PreparedStandbySlate> PrepareAsync(FfmpegRouteOptions options, DeckLinkPort port, OutputPreset preset,
        PortStandbyConfiguration configuration, CancellationToken token) =>
        // Do not capture an operator circuit's synchronization context, even on
        // cache hits whose file reads can complete synchronously.
        Task.Run(() => PrepareCoreAsync(options, port, preset, configuration, token), token);

    private async Task<PreparedStandbySlate> PrepareCoreAsync(FfmpegRouteOptions options, DeckLinkPort port, OutputPreset preset,
        PortStandbyConfiguration configuration, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await _preparation.WaitAsync(deadline.Token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            // Read logo bytes on every request: replacement at the same path, size
            // and timestamp must invalidate the image. Render this exact snapshot.
            byte[]? logo = null;
            if (!string.IsNullOrWhiteSpace(configuration.LogoPath))
            {
                if (new FileInfo(configuration.LogoPath).Length > MaxImageBytes) throw new IOException("Standby logo exceeds the cache input limit.");
                logo = await File.ReadAllBytesAsync(configuration.LogoPath, deadline.Token).ConfigureAwait(false);
                if (logo.Length > MaxImageBytes) throw new IOException("Standby logo exceeds the cache input limit.");
            }
            var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, executable = await ToolHash(options.ExecutablePath, deadline.Token),
                font = await ToolHash(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"), deadline.Token),
                logo = logo is null ? null : Convert.ToHexString(SHA256.HashData(logo)),
                card = DeckLinkDisplayName.Card(port), port.SubdeviceIndex,
                label = string.IsNullOrWhiteSpace(configuration.PortLabel) ? DeckLinkDisplayName.Connector(port) : configuration.PortLabel,
                configuration.Pattern, preset.Mode.Width, preset.Mode.Height
            })));
            Directory.CreateDirectory(_root);
            var published = Path.Combine(_root, key + ".png");
            if (File.Exists(published))
            {
                ValidatePng(await ReadImage(published, deadline.Token), preset.Mode.Width, preset.Mode.Height);
                return new(published, key, preset.Mode.Width, preset.Mode.Height);
            }
            // Published images are immutable and never evicted while an output
            // may hold them. Capacity exhaustion refuses preparation, not playout.
            var occupied = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            if (occupied > _maximumBytes - MaxImageBytes * 2L)
                throw new IOException("Standby image cache is full; keep the current output and clean unused cache files only during maintenance.");
            temporary = Path.Combine(_root, ".render-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
            string? logoSnapshot = null;
            if (logo is not null)
            {
                logoSnapshot = Path.Combine(temporary, "logo" + Path.GetExtension(configuration.LogoPath));
                await File.WriteAllBytesAsync(logoSnapshot, logo, deadline.Token);
            }
            var image = Path.Combine(temporary, "slate.png");
            await _render(FfmpegCommandBuilder.BuildStandbySlateRender(options, port, preset,
                configuration with { LogoPath = logoSnapshot }, image), deadline.Token).ConfigureAwait(false);
            var bytes = await ReadImage(image, deadline.Token);
            ValidatePng(bytes, preset.Mode.Width, preset.Mode.Height);
            deadline.Token.ThrowIfCancellationRequested();
            File.Move(image, published, overwrite: false);
            return new(published, key, preset.Mode.Width, preset.Mode.Height);
        }
        finally
        {
            // Only this invocation's checked child directory; never cached inputs.
            try
            {
                if (temporary is not null && Path.GetDirectoryName(temporary) == _root && Directory.Exists(temporary))
                    Directory.Delete(temporary, recursive: true);
            }
            finally { _preparation.Release(); }
        }
    }

    private async Task<string> ToolHash(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path); var file = new FileInfo(path);
        if (_toolHashes.TryGetValue(path, out var known) && known.Length == file.Length && known.Modified == file.LastWriteTimeUtc.Ticks) return known.Hash;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        _toolHashes[path] = (file.Length, file.LastWriteTimeUtc.Ticks, hash);
        return hash;
    }
    private static async Task<byte[]> ReadImage(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > MaxImageBytes) throw new InvalidDataException("Standby image exceeds the cache limit.");
        return await File.ReadAllBytesAsync(path, token);
    }
    internal static void ValidatePng(byte[] bytes, int width, int height)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192) throw new InvalidDataException("Invalid standby raster.");
        if (bytes.Length < 45 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("Incomplete standby image.");
        var offset = 8; var header = false; var data = false; var end = false;
        using var compressed = new MemoryStream();
        while (offset <= bytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            if (length > bytes.Length - offset - 12) throw new InvalidDataException("Truncated standby image.");
            var count = (int)length; var chunk = bytes.AsSpan(offset + 4, count + 4);
            uint crc = uint.MaxValue;
            foreach (var value in chunk) { crc ^= value; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320U); }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + count, 4)))
                throw new InvalidDataException("Standby image checksum mismatch.");
            var type = System.Text.Encoding.ASCII.GetString(chunk[..4]);
            if (type == "IHDR")
            {
                if (offset != 8 || count != 13 || BinaryPrimitives.ReadInt32BigEndian(chunk[4..]) != width || BinaryPrimitives.ReadInt32BigEndian(chunk[8..]) != height)
                    throw new InvalidDataException("Standby image raster mismatch.");
                if (chunk[12] != 8 || chunk[13] != 2 || chunk[14] != 0 || chunk[15] != 0 || chunk[16] != 0)
                    throw new InvalidDataException("Standby renderer must produce non-interlaced RGB24 PNG.");
                header = true;
            }
            if (type == "IDAT") { data = true; compressed.Write(chunk[4..]); }
            offset += count + 12;
            if (type == "IEND") { end = count == 0 && offset == bytes.Length; break; }
        }
        if (!header || !data || !end) throw new InvalidDataException("Incomplete standby image chunks.");
        compressed.Position = 0;
        using var pixels = new ZLibStream(compressed, CompressionMode.Decompress);
        var row = new byte[checked(width * 3)];
        try
        {
            for (var y = 0; y < height; y++)
            {
                if (pixels.ReadByte() is < 0 or > 4) throw new InvalidDataException("Invalid standby scanline.");
                pixels.ReadExactly(row);
            }
            if (pixels.ReadByte() != -1) throw new InvalidDataException("Unexpected standby pixel data.");
        }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Incomplete standby pixels.", ex); }
    }

    private static async Task RenderAsync(ProcessStartInfo start, CancellationToken token)
    {
        using var child = OwnedChildProcess.Start(start);
        var output = Drain(child.StandardOutput); var errors = Drain(child.StandardError);
        try
        {
            await child.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (child.ExitCode != 0) throw new IOException($"Standby image render failed with exit code {child.ExitCode}.");
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }
    private static async Task Drain(StreamReader reader)
    { var buffer = new char[4096]; while (await reader.ReadAsync(buffer) > 0) { } }
}
