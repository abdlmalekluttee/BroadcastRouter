# Tabler production GUI validation

## Scope

The shared Blazor layout, navigation markup, and CSS asset loading were adapted to Tabler. The production promotion also adds preset-label validation; routing, process supervision, Wowza, FFmpeg, DeckLink, and authentication behavior remain unchanged.

## Architecture boundary

The browser remains a control surface over the existing long-running ASP.NET Core host. Runtime state continues to be owned by `RouterCoordinator`; the experimental layout subscribes to the same snapshot event and invokes the same page handlers as the production interface.

## Baseline

Before the interface changes:

- .NET SDK: 8.0.424
- Release build: passed with zero warnings and zero errors
- Regression suite: 114/114 passed

## Presentation changes

- Added a responsive Tabler vertical navigation shell and top runtime bar.
- Replaced text glyph navigation with accessible Tabler SVG icons.
- Added a clear **Production** marker and Tabler operator-interface identification.
- Added responsive desktop, compact desktop, tablet, and mobile layouts.
- Added consistent Tabler-inspired cards, forms, tables, buttons, badges, notices, and focus styles.
- Added reduced-motion handling and visible keyboard focus.
- Bundled pinned Tabler assets locally; no CDN dependency was introduced.

## Production invariants

- Production mode remains fail-closed.
- Simulation remains opt-in.
- No network scanning, route startup, hardware access, or database migration was added.
- No output-ownership or process-supervision code was changed.
- No proprietary FFmpeg or Blackmagic binaries or visual assets were added.

## Validation matrix

| Check | Status |
|---|---|
| Release restore/build | Verified: zero warnings and zero errors |
| Complete automated regression suite | Verified: 114/114 passed before and after the UI work |
| Local browser navigation | Verified: all eight primary pages reached with the expected heading |
| Safe editor interaction | Verified: add/remove Wowza editor flow and confirmation |
| Responsive layout | Verified at the default desktop viewport, 768×1024, and 480×900 |
| Document-level horizontal overflow | Verified absent on all eight pages at 480 px |
| Browser console | Verified: no warnings or errors during the navigation pass |
| Local installation | Verified healthy on loopback from `C:\BroadcastRouter-Experimental-GUI` |
| Local simulation only | Not enabled; production-safe empty defaults retained |
| Wowza integration | Not required; behavior unchanged |
| DeckLink/driver/physical SDI | Not required; behavior unchanged |
| Production server deployment | Required for release 1.5.21 |

## Remaining manual validation

Every production deployment must repeat the production validation matrix, including server health, all configured output modes, route recovery, and a physical SDI receiver check.
