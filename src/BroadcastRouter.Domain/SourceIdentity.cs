namespace BroadcastRouter.Domain;

public sealed record SourceIdentity
{
    public string ServerId { get; }
    public string Application { get; }
    public string ApplicationInstance { get; }
    public string StreamName { get; }

    public string Value => string.Join('/', Escape(ServerId), Escape(Application), Escape(ApplicationInstance), Escape(StreamName));

    public SourceIdentity(string serverId, string application, string applicationInstance, string streamName)
    {
        ServerId = Validate(serverId, nameof(serverId));
        Application = Validate(application, nameof(application));
        ApplicationInstance = Validate(applicationInstance, nameof(applicationInstance));
        StreamName = Validate(streamName, nameof(streamName));
    }

    public override string ToString() => Value;

    private static string Validate(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Identity components cannot be empty.", paramName);
        if (value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Identity components must be at most 256 characters and contain no control characters.", paramName);
        return value.Trim();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
