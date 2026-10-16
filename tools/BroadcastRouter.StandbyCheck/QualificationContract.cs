namespace BroadcastRouter.StandbyCheck;

public sealed record StopReceipt(Guid Owner, bool Ready, bool Forced, int? ExitCode,
    double QuitMilliseconds, double ExitMilliseconds, double DrainMilliseconds, double ReapedMilliseconds, bool QuitDelivered = true);

public static class QualificationContract
{
    public static void VerifyBaselineInput(string expected, IReadOnlyList<string> tokens)
    {
        var input = Enumerable.Range(0, tokens.Count - 1).FirstOrDefault(i => tokens[i] == "-i", -1);
        if (string.IsNullOrWhiteSpace(expected) || input < 0 || tokens[input + 1] != expected)
            throw new InvalidDataException("Baseline input differs from the recorded original; filter-graph equality alone is insufficient.");
    }
    public static double ReadCpuSeconds(Action refresh, Func<double> read) { refresh(); return read(); }
    public static void ValidateSupervisorGroup(int expected, int started, int cleanExited, int forced, int remaining, double stoppedMilliseconds)
    {
        if (!AcceptSupervisorGroup(expected, started, cleanExited, forced, remaining, stoppedMilliseconds))
            throw new InvalidDataException("Supervisor cohort exceeds the unchanged stop target.");
    }
    public static bool AcceptSupervisorGroup(int expected, int started, int cleanExited, int forced, int remaining, double stoppedMilliseconds)
    {
        if (expected <= 0 || started != expected || cleanExited != expected || forced != 0 || remaining != 0 || !double.IsFinite(stoppedMilliseconds) || stoppedMilliseconds < 0)
            throw new InvalidDataException("Operational failure: supervisor cohort lacks clean terminal evidence. Abort subsequent groups.");
        return stoppedMilliseconds < 1000;
    }
    public static void ValidatePlan(int concurrency, string sink)
    {
        if (concurrency is not (5 or 8) || sink is not ("null" or "capture"))
            throw new ArgumentException("Only five/eight local null or capture sinks are permitted.");
    }
    public static void ValidateReceipts(int expected, IReadOnlyList<StopReceipt> receipts)
    {
        if (!AcceptReceipts(expected, receipts)) throw new InvalidDataException("Clean stop receipts exceed the unchanged stop target.");
    }
    public static bool AcceptReceipts(int expected, IReadOnlyList<StopReceipt> receipts)
    {
        if (expected <= 0 || receipts.Count != expected || receipts.Select(r => r.Owner).Distinct().Count() != expected
            || receipts.Any(r => r.Owner == Guid.Empty || !r.Ready || r.Forced || !r.QuitDelivered || r.ExitCode != 0
                || !double.IsFinite(r.ReapedMilliseconds)
                || r.QuitMilliseconds < 0 || !(r.ExitMilliseconds >= r.QuitMilliseconds)
                || !(r.DrainMilliseconds >= r.ExitMilliseconds) || !(r.ReapedMilliseconds >= r.DrainMilliseconds)))
            throw new InvalidDataException("Operational failure: missing, invalid or forced stop receipt. Abort subsequent groups.");
        return receipts.All(r => r.ReapedMilliseconds < 1000);
    }
    public static async Task StopTogetherAsync(IEnumerable<Func<Task>> stops)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = stops.Select(stop => Task.Run(async () => { await barrier.Task; await stop(); })).ToArray();
        barrier.SetResult();
        await Task.WhenAll(tasks);
    }
}
