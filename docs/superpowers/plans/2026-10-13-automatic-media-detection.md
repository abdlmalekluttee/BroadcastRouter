# Automatic Media Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admit video+audio, video-only and audio-only sources automatically, with bounded evidence-based qualification and one controlled richer-profile handoff per publisher epoch.

**Architecture:** Separate observed input tracks, the immutable selected session graph, and optional operator overrides. A bounded asynchronous detector collects decoded evidence; a per-source epoch/session policy proposes admission or a planned upgrade; the existing serialized route/port handoff remains the only output writer. Management uncertainty, stale probes and profile hints cannot authorize a stop.

**Tech stack:** Existing .NET 8, Blazor, SQLite, FFprobe/FFmpeg, xUnit fake clock/process harness. No new media framework, GPU code or FFmpeg patch.

**Spec:** Owner checkpoint of 13 October 2026, sections 1–6, retained privately with the Phase 2 evidence. This sanitized plan contains no operational source names, addresses, credentials or physical handles.

## Global constraints

- New PR on `fix/P2-24-auto-media-detection`, stacked on #81 at `4f07c16e2faa97b183dd39df4c7739a9951f398a`. Do not amend accepted PRs #75/#76/#77/#81. Owner alone merges.
- No production deployment. Production remains read-only except explicitly approved bench-connector release/restore sessions.
- Failing behavior tests first, then implementation commits; preserve unrelated tests and the original 136 case functions. Replace only expectations explicitly superseded by this design, with a documented before/after list.
- Normal suite and exact strict KnownDefect contract remain separate. Both R18 defects remain OPEN until their owning task fixes them.
- Detection budget: 5 seconds monotonic elapsed time; do not decide on the first track. Qualification: at least 1 second of advancing decoded video PTS / audio samples for every detected track. Count metadata-only tracks separately, not as qualified media.
- A decoded single video frame is presence evidence, never proof of one second of advancement. Retain standby and bounded retry if such detected video stalls; do not silently discard it to admit audio-only.
- Freeze selected tracks for the exact owner session. A later observation cannot change that owner's watchdog contract or selected graph.
- Richer-profile evidence must cover 10 seconds of sustained decoded advancement, not two disconnected snapshots ten seconds apart. At most one planned upgrade attempt per publisher epoch; faults retain the normal backoff policy.
- Still images with advancing PTS and silence with advancing PCM are healthy. Ambiguous sparse video is timestamped Unknown, not silently reclassified as audio-only or green.
- No static declaration, Undeclared blocker, declaration migration, or predeployment declaration confirmation step. Last detected profile is a nonbinding hint only.
- Manual override is Auto / force video+audio / force video-only / force audio-only, administrator-only under advanced source details, default Auto.
- No process-name kills, dual DeckLink writers, raw URI/credential logging, driver/profile changes, or new main-screen settings.
- Phase D is authorized on fakes, one PR per task. Dependabot and deferred general README/architecture/preview work remain assigned to their later phases.

## Review focus

1. Old detector completions racing publisher reconnect, override save, reassignment, emergency stop or owner replacement must not act on the newer session. Tests in tasks 1–3 invalidate each token before handoff.
2. A track advertised without decoded samples, a single keyframe, timestamp resets, repeated/nonfinite PTS and a stalled detector must never masquerade as a qualified missing-track profile. Task 1 tests each boundary.
3. Ten-second enrichment cannot be assembled from unrelated probe sessions or gaps; a failed planned start cannot repeatedly interrupt a healthy session. Task 2 exercises missing coverage, expiry and a consumed epoch budget.
4. Corroboration must remain independent: generated silence/black and progress heartbeat do not establish source health; one management/probe failure is not a destructive vote. Tasks 2–3 exercise these combinations.
5. Compatibility must include old JSON/DB state, muted presets, authorization, stale editor revisions and graph/UI disagreement. Tasks 3–4 cover round trips and rendered status.

## Task 1 — Auto defaults and bounded track detection

**Files:** `Domain/Models.cs`, `Domain/OperatorSettings.cs`, `Application/Contracts.cs`, `Application/SourceMediaPolicy.cs`, new `Application/AutomaticMediaDetection.cs`, `Infrastructure/FfprobeStreamProbe.cs`, `Application/SourceMediaMigration.cs`, coordinator startup; corresponding tests under `tests/BroadcastRouter.Tests`.

**Interfaces:** `SourceMediaOverride { Auto, ForceAudioVideo, ForceVideoOnly, ForceAudioOnly }`; `DetectedMediaProfile { Unknown, AudioVideo, VideoOnly, AudioOnly }`; immutable `MediaDetectionResult` carries detected tracks, decoded advancement, observation/coverage interval, profile, qualification and reason. `AutomaticMediaDetection.DetectAsync(Uri, SourceMediaOverride, CancellationToken)` consumes an injected probe and monotonic clock. Existing `StreamProbeResult` remains the raw parser contract.

- [ ] Add compiling regressions against today's policy/startup: unconfigured audio-only/video-only/AV must no longer be blocked by declarations; old DB sources start in Auto without a settings write solely to classify them. Run and retain the expected assertion failures before implementation.
- [ ] Add fake-clock cases for audio arriving at t=3 after video, video at t=3 after audio, complete five-second window, metadata-only tracks, one keyframe/stall, bad/backward PTS, digital silence and unchanged-image advancing video. Expected: no early handoff; both late tracks qualify only with >=1 second evidence; stalled detected track keeps standby.
- [ ] Implement bounded detection with fresh probe sessions during the window, because an already-open FFmpeg-family input may not attach a newly introduced track. Use short completed observations with actual decoded evidence; never concatenate stale durations to fabricate qualification. The final evidence must still cover every detected track. Cancel/contain unfinished helpers at the deadline; timeout is Unknown, not inferred track absence.
- [ ] Separate discovery-window completion from helper cleanup, retain bounded cleanup deadlines, and prevent overlapping per-source detection. Route/reconciliation loops poll results without awaiting the detection window.
- [ ] Remove declaration admission/migration and its production confirmation requirement. Use a new override field defaulting to Auto rather than numerically reinterpreting previously serialized declaration enums. Preserve unrelated settings/route intent. Superseded declaration records may be read for compatibility but cannot block Auto.
- [ ] Run focused tests and commit GREEN after the recorded RED. Record parser timing limits; real delayed-track acquisition remains a bench requirement.

## Task 2 — Immutable session contract and one richer-profile upgrade

**Files:** new `Application/MediaSessionProfilePolicy.cs`; publisher epoch integration in `Web/Services/RouterCoordinator.cs`; `Application/RecoveryReadinessGate.cs`; `Infrastructure/FfmpegMediaSession.cs`; `Infrastructure/FfmpegProcessSupervisor.cs`; focused policy/supervisor tests.

**Interfaces:** immutable `MediaSessionProfile` includes publisher epoch, owner session ID, detected profile and selected graph; `MediaSessionProfilePolicy.Observe(...)` returns None / QualifyRicherProfile / ConfirmedTrackLoss with evidence. Claim a planned-upgrade token atomically per epoch before destructive handoff; no reset on ordinary poll, API Unknown or process retry.

- [ ] RED: audio-only never fails for absent video; video-only never fails for absent audio; later discovery cannot modify the current owner's required signals; generated tracks cannot vote as real input.
- [ ] RED: audio-only gains video for ten continuous seconds -> exactly one planned handoff; under-ten-second gain, interrupted coverage, API failure, stale epoch and stale owner -> none. Repeat observations and failed upgrade start cannot consume a second planned handoff in the same epoch. A proven new epoch gets a new budget.
- [ ] RED: video loss from AV first raises matching-track suspicion; destructive recovery still requires independent corroboration or process exit. On recovery, fresh detection may select audio-only; no old AV requirement is carried forward. Silence with advancing samples never votes failure.
- [ ] Implement session-bound required tracks and epoch-bound enrichment. Use a continuous bounded growth-confirmation sample; isolated short probe snapshots are not ten-second continuity proof. Media probe faults alone leave a healthy owner intact.
- [ ] Preserve sparse ambiguity: sparse input gaps alone do not establish failure; report Unknown with last observation. Do not weaken the one-second initial qualification rule for a single keyframe.
- [ ] Run tests, document any unobservable case, commit GREEN. No unmeasured threshold tuning.

## Task 3 — Coordinator, selected graphs and durable evidence

**Files:** `Web/Services/RouterCoordinator.cs`, `Infrastructure/FfmpegCommandBuilder.cs`, runtime/telemetry contracts, `Application/RecoveryReadinessGate.cs`; integration tests using real coordinator plus fake children and temporary SQLite.

**Interfaces:** initial/manual/recovery admission consumes the same fresh `MediaDetectionResult`; planned enrichment uses the existing per-route and per-port serialized ownership path, with generation checks after awaits. Decision records include detected profile, selected graph, publisher/session epoch, qualification intervals, reason, action and result.

- [ ] RED coordinator cases for all three profiles, both three-second delayed-track cases, one-keyframe retained standby, exactly-one planned upgrade, and fault/re-detection downgrade. Assert actual process starts/stops, port lease and argv, not only policy return values.
- [ ] RED handoff races at owner stop, standby stop, persistence and launch; publisher/config/override changes invalidate old proof and restore standby safely. No second writer and no unrelated owner stop.
- [ ] Implement AV -> both live tracks; video-only -> source video + generated silence; audio-only -> generated black/slate + source audio. No source track can be mapped merely because metadata advertises it. Preserve explicit output-preset audio intent; resolve the muted-preset point below before changing that behavior.
- [ ] Planned upgrade is logged as planned, not a decoder fault; it cannot bypass proof freshness, the exclusive lease, shutdown/reaping or emergency stop. At failure, use normal bounded recovery and fresh detection without resetting the planned-upgrade budget.
- [ ] Remove obsolete observation-only promotion/declaration paths only where replaced by the new contract. Preserve configured inventory, source identity, reservations and source-independent behavior.
- [ ] Run integrated fault tests plus all normal shards and strict KnownDefects. Commit after GREEN; retain failing attempts and reasons privately.

## Task 4 — Advanced override, truthful UI, review and publication

**Files:** `Web/Components/Pages/Sources.razor`, `Web/Components/Shared/SourceMediaStatus.razor`, a small advanced source-details component if needed, `Web/Services/AuthorizedRouterCommands.cs`, settings validation, `docs/phase2/P2-24-AUTO.md`, `docs/PHASE2_PROGRESS.md`.

- [ ] RED rendered/UI-command tests for default Auto, the four exact override options only under advanced details, administrator authorization, optimistic revision checks, save failure preservation, detected vs selected profiles and timestamped sparse Unknown.
- [ ] Implement advanced override using existing authorized settings/audit path; it affects the next qualified session, never silently edits the current owner's graph. Display pending override separately if current graph differs.
- [ ] Update task-specific docs and the acceptance matrix. Retain historic reports as superseded evidence, not current declaration requirements. Add test-source cleanup to the private future deployment runbook; delete nothing on production.
- [ ] Run Release build, all disjoint normal-test shards, strict KnownDefect, CI/provenance guards, package privacy and clean-build comparison. New clock decisions use the existing monotonic abstraction. No private fixtures or physical handles in the public diff.
- [ ] Exercise the affected interface in local simulation only. Record browser limits instead of claiming unperformed UI tests.
- [ ] Fresh-context read-only reviewer checks the whole new PR against owner section 1; fix Important/Critical findings test-first and rerun the full suite. Deferred minors remain explicit.
- [ ] Publish the new PR stacked on #81, attach it to this chat, wait for exact-head required CI, mark ready only with evidence, and send the section-1 report. Never merge.

## Approved muted-preset behavior

Implementation checkpoint: Tasks 1–3 and the Task 4 UI are implemented on the
follow-up branch. The whole-branch review fix pass is complete, including shared
nonblocking discovery/admission, operator-hold checks and truthful preset-save
warnings. See `docs/phase2/P2-24-AUTO.md` and the stacked PR for execution evidence;
the checklist above preserves the original plan, not hardware acceptance.

The output preset's audio setting is authoritative. Auto detects and qualifies every present input track, while the selected graph and UI say "audio disabled by preset" when muted. An audio-only source with a muted preset retains standby and the visible alarm "incompatible preset: audio-only source on a muted output". Force overrides never defeat mute. Assignment saves and preset edits warn about incompatible combinations without blocking persistence. Collect a read-only, private inventory of all current muted presets and their output usages for owner confirmation before any deployment.

## Authorized follow-on work (not part of the automatic-detection PR)

- P2-30: per-output serialized controllers, configuration generations and no whole-host recovery for a stuck route; one test-first PR with independent review.
- P2-31: bounded/coalesced persistence, durable essential transitions and telemetry Flush timeout root cause; one test-first PR. Reproduce the timeout before attempting a fix; retain the 10 GB and free-disk guard rules.
- P2-32: exact-owned preview/probe containment, host-crash test preserving unrelated children; one test-first PR.
- P2-33: remaining supervision clock audit and injected +/-2-second NTP steps on fakes; one test-first PR. Hardware/OS clock changes are not authorized by the fake test.
- Hardware rows remain open until tested. Gate D is a separate report, never implied by section-1 readiness.

## Next bench proposal — no session started

Approved 14 October, 10:00–12:00 Libya time, conditional on green candidate and guard tests. Otherwise propose a replacement; do not run an overdue session. The 24-hour soak still requires separate approval.

1. Consistent backup and recorded assigned-port configuration; announce build hash, actual persistent-handle allowlist and monitoring baseline privately.
2. Rehearse UI release/restore, recording software restoration separately from physical picture. Only the established loopback mapping is physically verified; never guess others.
3. Side-by-side versioned bench directory, independent DB/data and HTTP port, loopback-only listener. Never copy the production DB into a runnable bench that could inherit routes/secrets. Test-only source config and dedicated media-tool paths, with no overwrite of existing tools.
4. An immutable bench launch guard rejects every nonallowlisted handle for live, fallback and standby starts, even if UI/config is changed or discovery finds extra ports. Preflight compares production ownership and refuses startup unless the entire allowlist is released. Verify negative tests before starting. Publish exact handles only in the private session report.
5. Dedicated synthetic publisher only. Cover AV/audio-only/video-only, short drops, restart, corruption, long GOP, still/black/silence, gained/lost tracks. Throttle >=2 seconds between connector starts/stops; batches <=100. Each row is pass/fail/not-coverable with measured evidence.
6. Continuous production state/events/CPU monitoring; abort on protected-output change, driver error or CPU >70%. Do not touch protected connectors, real channels, production service, Wowza config, drivers or profiles.
7. Stop all exact-owned bench processes, restore original assigned-port settings/routes via UI, verify equality and expected output state, leave bench files. Report in section 10 format.

## 24-hour soak proposal — PLAN ONLY

After Gate D software review and necessary physical preflight, propose 09:00 Libya time on the first owner-approved day through 09:00 the next day. This requires a separate explicit 24-hour window; the current maximum two-hour sessions do NOT authorize it. Hours 0–2 baseline; 2–6 profile/late-track matrix; 6–10 paced interruption/restart schedule; 10–14 bounded corruption/long-GOP cases; 14–22 representative steady mixed load; 22–24 recovery checks and restoration. Continuous protected-output, event, CPU, memory/handle and telemetry-disk monitoring with the same abort rules. No scheduled automation or soak starts from this plan alone.
