# Route state machine

The state machine separates discovery, media readiness, resource allocation, process startup, steady output, fallback, and recovery.

```mermaid
stateDiagram-v2
    [*] --> Known
    Known --> PublisherActive
    PublisherActive --> Probing
    Probing --> Ready: frames received
    Probing --> Unavailable: RTSP/media failure
    Ready --> WaitingForPort: no compatible free port
    Ready --> Reserved: atomic reservation succeeds
    WaitingForPort --> Reserved: port becomes available
    Reserved --> Starting
    Starting --> Running: output progress healthy
    Starting --> Reconnecting: startup failure
    Running --> Stalled: progress deadline exceeded
    Running --> Fallback: publisher/media unavailable
    Stalled --> Reconnecting
    Reconnecting --> Probing: retry due
    Reconnecting --> Fallback: grace policy
    Fallback --> Probing: primary returns
    Fallback --> Released: grace expires and unlocked
    Starting --> Failed: permanent or retry-exhausted startup failure
    Running --> Failed: permanent output failure
    Reconnecting --> Failed: retry cap exhausted
    Failed --> Reconnecting: explicit recovery
    Failed --> Released: operator stop or unlocked retry exhaustion
    Unavailable --> Probing: publisher/API observation recovers
    Released --> Ready: source still eligible
    Known --> Disabled
    Disabled --> Known
```

API-unreachable is server health, not a destructive route transition. Healthy `Running` routes remain running until RTSP/progress or process evidence says otherwise.

Locked assignments retain `Reserved` ownership indefinitely. Unlocked offline routes retain it until their grace deadline, then transition through `Released`. Every transition is validated; invalid jumps are rejected and logged with source identity and correlation ID.
