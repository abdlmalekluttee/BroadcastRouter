namespace BroadcastRouter.Web.Services;

public sealed class OperatorErrorMessage(ILogger<OperatorErrorMessage> logger)
{
    public string Describe(Exception exception, string context)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..12];
        logger.LogError(exception, "Operator command failed in {Context}; correlation {CorrelationId}", context, correlationId);

        return exception switch
        {
            FormatException => $"{context} contains an invalid value. Correct the highlighted field and try again.",
            ArgumentOutOfRangeException => $"{context} is outside the permitted range. Correct the highlighted field and try again.",
            ArgumentException => $"{context} was not accepted. Review the field values and try again.",
            UnauthorizedAccessException => $"{context} requires Administrator access. Sign in with an administrator account and try again.",
            TimeoutException => $"{context} timed out. Confirm the server connection, then retry the command.",
            OperationCanceledException => $"{context} was cancelled before the server confirmed it. Refresh the page before retrying.",
            InvalidOperationException => $"The server could not complete {context}. Refresh the confirmed state, review Logs & Diagnostics, and try again. Correlation ID {correlationId}.",
            _ => $"The server rejected this command. Check Logs & Diagnostics for correlation ID {correlationId}."
        };
    }
}
