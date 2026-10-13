using BroadcastRouter.Domain;

namespace BroadcastRouter.Application;

public sealed record SourceMediaMigrationProposal(string SourceId, SourceMediaExpectation Expectation,
    bool RequireAudio, string Evidence, DateTimeOffset? ObservedAt);

public static class SourceMediaMigration
{
    // Upgrade only. Later discoveries need an explicit operator declaration.
    // No source-name heuristics and no reinterpretation of existing declarations.
    public static IReadOnlyList<SourceMediaMigrationProposal> Propose(OperatorSettings settings,
        IReadOnlyList<DiscoveredSource> sources) => sources
        .Where(s => !settings.SourceMediaProfiles.Any(p => p.SourceId == s.Identity.Value))
        .Select(s =>
        {
            var expectation = s.Media switch
            {
                { HasUsableVideo: false, AudioCodec: not null } => SourceMediaExpectation.AudioOnly,
                { HasUsableVideo: true, Expectation: SourceMediaExpectation.SparseVideo } => SourceMediaExpectation.SparseVideo,
                { HasUsableVideo: true } => SourceMediaExpectation.ContinuousVideo,
                _ => SourceMediaExpectation.Undeclared
            };
            var evidence = expectation switch
            {
                SourceMediaExpectation.AudioOnly => "Persisted audio-led behavior: audio present, source video not selected.",
                SourceMediaExpectation.ContinuousVideo => "Persisted usable source video; continuous expectation retained.",
                SourceMediaExpectation.SparseVideo => "Persisted usable source video with explicit sparse expectation.",
                _ => "No persisted usable media evidence. Operator declaration required; standby retained."
            };
            // Observed audio metadata does not mean an existing video output
            // selects it. Optional audio is qualified at handoff if the preset
            // includes it; only audio-led behavior proves audio is indispensable.
            return new SourceMediaMigrationProposal(s.Identity.Value, expectation,
                expectation == SourceMediaExpectation.AudioOnly, evidence, s.LastObservedAt);
        }).ToArray();
}
