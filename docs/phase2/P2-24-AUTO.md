# P2-24 follow-up — automatic media selection

Status: candidate under verification, stacked on #81. Not deployed. Only the
repository owner merges. This supersedes the declaration/migration design in
P2-24, not its historical evidence or the earlier recovery safety rules.

## Admission and output contract

Existing settings deserialize with `Override = Auto`. Legacy declaration fields
remain readable, but do not block a source or trigger startup settings writes.
There is no source-name special case or required declaration confirmation list.

A per-source detector observes a five-second monotonic window, reopening at
approximately 0, 1.25, 2.5 and 3.75 seconds. It remembers every decoded track seen.
The final observation must prove at least one second of advancing decoded PTS
for video and advancing decoded samples for audio, for every detected track.
Durations from unrelated opens are never added together. A single keyframe is
video presence, not qualification, and cannot be discarded to admit audio-only.
An open failure before any track is observed preserves its classified failure
(including RTSP 404), returns immediately, and uses the existing retry backoff.
An observation/cleanup timeout retains standby and never opens overlapping probes.

| Detected input | Selected graph (unmuted preset) |
| --- | --- |
| Video + audio | Real video + real audio |
| Video only | Real video + generated silence |
| Audio only | Generated black + real audio |

The selected graph and required real-track health signals are frozen for the
owner session. Generated black/silence cannot prove source health. Later metadata
does not change the current graph or silently change the watchdog's contract.

An Auto owner can gain a richer profile through one planned handoff attempt per
confirmed publisher epoch. A fresh continuous probe must cover at least ten
seconds of advancing audio and video, with no video PTS gap over 250 ms. Growth
checks are source-serialized, globally limited to two and spaced at least thirty
seconds after a result. The attempt budget survives process replacement, failed
start and API uncertainty. Only a confirmed publisher epoch resets it.

The handoff rechecks management certainty, epoch, settings revision, override,
proof age, exact owner and port lease across asynchronous boundaries. It is
recorded as planned, not a fault or an additional retry. Selected-track loss
continues through independently corroborated recovery; the next owner detects
again and can legitimately select audio-only.

## Preset mute and operator controls

Preset mute is authoritative. Auto still detects and qualifies all present input
tracks. The graph and UI say **audio disabled by preset**. There is no selected
audio-output health channel when audio is intentionally not mapped.

Audio-only plus muted preset keeps standby and displays:

> incompatible preset: audio-only source on a muted output

No force override defeats mute. Assignment and preset saves persist normally and
return an advisory compatibility warning. Fresh admission enforces compatibility
again; an offline source with no evidence is not invented to be audio-only.

An administrator can choose Auto, Force video + audio, Force video-only or Force
audio-only under the initially closed **Advanced source details** disclosure.
Saving is revision-checked and audited, confirms the backend result, preserves
an unsaved draft on failure and does not restart a healthy owner. An override
different from the current graph is shown as pending the next qualified session.

Observed media and selected output graph are separate UI fields. Sparse video
ambiguity is timestamped **media status unknown**, never a green media verdict.
Here sparse means decoded video with insufficient initial progression or an
observed PTS gap over 250 ms. This conservative label is not a source declaration
or independent restart authority. Still images with advancing PTS and digital
silence with advancing samples remain valid media.

## Verification and superseded assertions

Tests cover Auto defaults and legacy settings without migration, AV/A/V graphs
and actual fake-child arguments, both three-second late-track cases, a single
keyframe followed by stall, hung helper/no overlap, continuous enrichment and its
one-epoch budget, stale epoch/owner/API/settings/emergency/proof, invalidation
after stopping an owner, selected-video loss and audio-only re-detection, preset
mute across overrides, backend save warnings, authorization, revision conflicts,
audit records, draft preservation, actual parent-page source identity binding,
and selected-graph plus qualification telemetry.

The earlier declaration tests are changed only where the owner superseded their
requirements: declaration migration becomes no migration; explicit required
tracks become force overrides; one-frame sparse admission becomes unqualified.
The original 136 case functions are retained. Fake-clock integration fixtures
now drive the detection window and wait for finite fake child processes before
advancing virtual time, avoiding CI scheduling latency masquerading as a probe
deadline. Controlled hung-helper tests separately exercise the actual deadline.

The latest simulated 24-hour normal-output plus offline-source run retained
10,358,865 segment bytes with 5,863 accepted/written records, no losses or write
failures, 2,880 offline probes and exactly two initial owners/no stop requests.
A 10 GB cap holds roughly 965 such days by capacity alone; actual retention is
still 30 days and the existing free-disk guard remains authoritative. This is
one synthetic workload, not a worst-case sizing promise.

Strict KnownDefect continues to require the exact two **OPEN R18** assertions:
`DiagnosticActorIsRedacted` and `DiagnosticReasonIsRedacted`. They are not passing
behavior tests. No test is skipped, inverted or moved out of the normal suite to
hide a regression. Required CI check names and time limits are unchanged.

## Limits and outstanding gates

- A separate probe cannot guarantee the subsequent FFmpeg input sees identical
  packets/tracks. Real delayed-track acquisition and handoff interruption remain
  hardware qualification items, not software-proven promises.
- Reopening creates a controlled output handoff, not seamless insertion into a
  running FFmpeg graph. No FFmpeg patch or newly qualified FFmpeg build is claimed.
- The 250 ms gap bound is deliberately conservative: very sparse sources may
  stay Unknown or not enrich, but are not restarted by that label alone.
- General preview inference remains P2-50. Simulation has no real media owner;
  rendered component/integration tests verify selected-graph output separately.
- The isolated bench handle allowlist/preflight guard and Gate C physical rows
  remain blocked until implemented, verified and run in an approved window.
- No twenty-four-hour hardware soak, Phase D completion or production readiness
  is claimed. P2-31 owns the earlier telemetry flush-timeout investigation.
- Private operational inventories and logs are never repository evidence.
