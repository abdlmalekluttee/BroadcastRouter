using System.Text.Json;
using BroadcastRouter.Domain;

namespace BroadcastRouter.Web.Services;

public static class OperatorSettingsDraft
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static OperatorSettings Copy(OperatorSettings settings) =>
        JsonSerializer.Deserialize<OperatorSettings>(JsonSerializer.Serialize(settings, Options), Options)
        ?? throw new InvalidOperationException("The settings draft could not be copied.");

    public static string Fingerprint(OperatorSettings settings) => JsonSerializer.Serialize(settings, Options);
}
