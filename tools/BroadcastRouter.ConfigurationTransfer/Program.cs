using System.Text;
using BroadcastRouter.Infrastructure;

if (args.Length != 3 || args[0] is not ("export" or "import"))
{
    Console.Error.WriteLine("Usage: ConfigurationTransfer export SOURCE.db PRIVATE.json | import PRIVATE.json NEW.db");
    return 64;
}
try
{
    if (args[0] == "export")
    {
        var json = await ConfigurationTransfer.ExportAsync(args[1]);
        // No accidental overwrite of a prior operator-reviewed inventory.
        await using var file = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        await file.FlushAsync();
        Console.WriteLine("Private configuration exported. Review the file before import. No passwords or runtime history included.");
    }
    else
    {
        var file = new FileInfo(args[1]);
        if (file.Length > 16 * 1024 * 1024) throw new InvalidDataException();
        await ConfigurationTransfer.ImportAsync(await File.ReadAllTextAsync(args[1]), args[2]);
        Console.WriteLine("Fresh configuration database created. Connections and automatic routing are disabled; media paths are unset.");
        Console.WriteLine("Re-enter passwords in the new installation, verify hardware identities and assets, then enable intended connections.");
    }
    return 0;
}
catch (Exception error)
{
    // Exception messages may include configuration values or local paths.
    Console.Error.WriteLine($"Configuration transfer failed ({error.GetType().Name}). No existing database was replaced.");
    return 1;
}
