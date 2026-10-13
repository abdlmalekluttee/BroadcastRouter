# Gate C — software checkpoint, qualification OPEN

This is not a gate pass or deployment approval. The corrected software checkpoint
is documented below; unresolved hardware/full-matrix requirements remain open.
Only the owner merges. No production changes were made for Phase C software.

## 13 October correction checkpoint

Owner merged #66–74 to main091f686. Feature PRs #75→#76→#77→#81 were rebased in
that order after retaining their old tips, with owner-approved branch-specific
force-with-lease. Main protection and required check names were not changed.
Normal CI shards are exhaustive/disjoint and each retains a10min job timeout.
Hosted #75/#76/#77 are green; longest jobs7m27s/7m15s/5m48s, respectively.
The final #81 head needs its own hosted verification, recorded in the PR.

Upgrade declarations now follow persisted behavior, retain explicit profiles,
and never infer mandatory audio merely from metadata. Unknown sources keep
standby with a visible reason. A private per-source evidence proposal awaits
owner confirmation before deployment. Continuous video requires>=1s advancing
decoded PTS; selected/required audio requires>=1s advancing samples. Sparse
video-only shows timestamped media-status-unknown, not green. Async handoff
invalidation is checked through teardown/persistence/launch and restores standby.

Independent correction review:0Critical/3Important fixed in one test-first pass,
9/9 intended RED assertions then9/9 GREEN and84/84 focused. Recovery warning gap:
3assertion RED then24/24 GREEN. Two exact R18 KnownDefects remain OPEN. Original
136 case functions retained; one older Phase C display label intentionally differs
from main, but this correction changes no baseline case identities or labels.
Normal full/head evidence is recorded in P2-24 and PR81; no rerun alone is a fix.

Bench extension:400 additional standby stops on four assigned connectors,0forced/
nonzero exits,worst520.4383ms; known-loop10tone+10silent captures,20cleanstops,
max1366.019ms. All original assignments/port settings restored; service/protected
outputs unchanged; maxCPU26%,no Windows driver errors; no bench helpers remained.
Two dropped input frames were logged during capture startup. Physical fifth-port
slate remains unverified. These are Phase B candidate results, NOT qualification
of the newly corrected Phase C admission pipeline. Disk above reserve; full exact
disk/Wowza-size/on-disk-retention report and physical evidence stay private.

Phase D, deployment and merges are not authorized here. P2-31 must investigate
the earlier Flush timeout before Gate D; P2-50 owns preview inference. No FFmpeg
late-track patch. Dependabot #78–80 remain untouched. Older sections below retain
the historical checkpoint, not the current merge/CI state.

## PRs and findings

P2-23#74, P2-22#75, P2-20#76, P2-21#77 and P2-24#81 await owner review/merge.
P2-24 is draft. Prior Phase B stack#66–72 was not amended. Account remains the owner's
GitHub account; separate bot/code-owner review configuration remains pending.

R04/R05/R07 targeted software defects have regression coverage and their exact
correctness assertions moved to the normal suite. R06 is partial pending complete
runtime/bench qualification. R13 is deliberately bounded: declared expectations,
fresh required-track proof and observation-only reclassification; seamless true
late-track insertion remains open. Two R18 actor/reason tests remain strict OPEN
known defects. A green KnownDefect job means those exact defects still reproduce.

## P2-02 acceptance rows — nothing silently dropped

| Row | Software evidence | Still open |
|---|---|---|
| Continuous AV, still/black+audio, audio-only, silence | Independent PCM/video observations; healthy-warning/duplicate fixtures retain owners; declared sparse/AudioOnly selection | Full representative decoder/SDI fixture matrix and duration |
|0.5/1/3-second interruptions, publisher epoch | Complete publisher observations, evidence expiry, epoch acceleration and cached-state preservation | Real RTSP interruption and physical cutover timing |
| Long GOP, late audio/video | Missing/metadata-only/packet-only qualification; continuous needs>=1s advancing PTS, sparse permits one valid picture; healthy graph not restarted | Seamless new-track insertion; historical cause discrimination; complete timestamp reset pipeline |
| Corrupt packets then recovery | Failed matching-media and elapsed-window classification; qualified recovery clears only observed failure | Exact runtime/network corruption and physical AV recovery timing |
| REST403/500/timeout/invalid200/partial app | Strict provider and healthy-owned-route retention tests | Production/load qualification, not additional schema inference |
| RTSP404, child exit | One-hour bounded real fake-probe test; offline fallback retention; OS exit remains definitive | Busy/unavailable native matrix and on-air timing |
| Stale progress/nonadvancing media | Heartbeat is not media; early-stalled and corroborated failure recovery, bounded hold | Overhead and timing on representative hardware/load |
| DeckLink busy/reference loss | Classifier and existing retry tests | BLOCKED-ON-BENCH; historical native failure cause unresolved |
| Slow DB/concurrent settings,reassign,stop | Existing atomic settings/leases and stale-proof veto | P2-30/31 combined stress and bounded persistence |
| Preview+host crash/service restart | Existing containment/persistence foundation | P2-32 complete preview/probe containment and reboot |
|48–72h soak | Monotonic fake-time foundation | No soak claimed; release gate remains open |

## Metrics and residual failures

P2-21 local530/5308.2222min; final hosted Main job13m08s, exceeding10min target.
Earlier local529/530 failed `TelemetryStorageTests.RotationAndAgeRetentionAreBoundedAndIgnoreForeignFiles`
at its first5s Flush. Four isolated repeats, class20/20 and unchanged full530/530
passed; cause remains unestablished. No test/timeout was weakened.

P2-24 local/reviewer/hosted outcomes are recorded in [P2-24](P2-24.md), including
all failures, intentional legacy changes and limitations. No production incident
rate, recovery latency, CPU benefit or universal zero-cut claim follows from fakes.
Final local normal suite569/569,0 failed/skipped,8m41s;39 new cases vs P2-21.
All136 original case identities retained,2 strict R18 defects OPEN, clean Release
0 warnings/errors, Python provenance12/12+CI12/12. First full558/560 failed stale
test reflection (corrected) and an unchanged slow-probe test (cause unresolved;
isolated/related/final-full reruns passed). No timing assertion was weakened.
That slow-probe failure recurred on the first hosted run (568/569, main12m25s,
job13m22s). Further deterministic tests established shared fake-probe marker
writer collisions, including13/16 child crashes. The test-only marker correction
passed46 focused cases; subsequent full/head verification is recorded on PR#81.
The unrelated earlier telemetry Flush delay is still unresolved.

Previously completed bench cohort was500 controlled stops,0 forced exits,
p99~1199ms/max~1266ms; one loopback had100 tones/100 silent standby captures.
That does not certify other connectors, live-source failures, Session0 or72h.

## Next phase (not started)

Stop for owner gate review. Close remaining bench/full matrix items before Gate C
can pass. Only after authorization proceed with P2-30 per-output controllers,
bounded persistence P2-31, process containment P2-32 and clock-step P2-33. No new
production test window or deployment authority is inferred from this checkpoint.
