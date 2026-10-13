using BroadcastRouter.Domain;
using Xunit;
using Lab = BroadcastRouter.Tests.SupervisorAudioRecoveryTests.Lab;

namespace BroadcastRouter.Tests;

public sealed class SupervisorVideoRecoveryTests
{
    private const string Warning = "There are not enough buffered video frames. Video may misbehave!";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(32)]
    public async Task HealthyDecodedVideoCannotBeClassifiedFailedByQueueWarnings(int burst)
    {
        await using var lab = await Lab.Create();
        lab.Time.Advance(TimeSpan.FromSeconds(6));
        await lab.Qualify();
        var session = lab.Owner.SessionId;
        for (var i = 0; i < burst; i++)
        {
            await lab.Warning("Synthetic unrelated diagnostic");
            await lab.Warning(Warning);
            Assert.Null(lab.ClassifiedFailure);
        }
        Assert.Equal(session, lab.Owner.SessionId);
        Assert.True(lab.Owner.Running);
        Assert.All(lab.Owner.IndependentMedia!.Signals, s => Assert.Equal(MediaSignalState.Healthy, s.State));
    }

    [Fact]
    public async Task IndependentlyFailedVideoClassifiesAndQualifiedRecoveryClearsItsFault()
    {
        await using var lab = await Lab.Create();
        await lab.Qualify();
        lab.Time.Advance(TimeSpan.FromSeconds(23));
        Assert.True(lab.Owner.IndependentMedia!.ConfirmedMediaFailure);
        await lab.Warning(Warning);
        Assert.Equal("DeckLinkVideoStarved", lab.ClassifiedFailure?.Category);
        for (var step = 0; step <= 4; step++)
        {
            if (step > 0) lab.Time.Advance(TimeSpan.FromMilliseconds(500));
            await lab.Media();
            if (step < 4) Assert.NotNull(lab.Owner.InputFailure);
        }
        Assert.Null(lab.Owner.InputFailure);
        Assert.True(lab.Owner.Running);
    }
}
