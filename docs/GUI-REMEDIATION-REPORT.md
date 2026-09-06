# GUI remediation report

Release: 1.6.0

Review date: 2026-09-06

Scope: `BroadcastRouter.Web` presentation layer

The routing domain, application policies, FFmpeg/FFprobe supervision, DeckLink ownership, coordinator behavior, and persistence semantics were not changed.

## Work packages

| Work package | Result | Principal files |
|---|---|---|
| WP1 — authored styles | Merged the three application stylesheets into one versioned `broadcastrouter.css`; removed the decorative page grid/glow and retained local vendored Tabler assets. | `Components/App.razor`, `wwwroot/broadcastrouter.css` |
| WP2 — runtime state | Moved the environment label to the top bar, kept On-air visible at every tested width, and replaced the 992–1050 icon rail with a collapsed full-label menu. | `Components/Layout/MainLayout.razor`, `wwwroot/broadcastrouter.css` |
| WP3 — accessibility | Added a contrast-safe focus ring, fixed live-region timestamps and heading levels, added non-color occupancy cues, and increased small action targets. | Layout/page Razor components, shared card visuals, `broadcastrouter.css` |
| WP4 — notices/errors | Added dismissible, severity-aware notices; actionable exception mapping with real correlation IDs; retry validation; failed-login feedback; and role-correct denial pages. | `Components/Shared/NoticeBoard.razor`, `Services/OperatorErrorMessage.cs`, login/denied/page components |
| WP5 — edit safety | Added independent settings drafts, dirty markers, sticky save controls, busy guards, and working internal/external navigation protection on Settings, Rules, and Wowza editors. | `Services/OperatorSettingsDraft.cs`, three settings page components, `Components/App.razor` |
| WP6 — confirmation tiers | Replaced browser confirms with an accessible application dialog and restricted interruption to destructive operations. | `Components/Shared/ConfirmDialog.razor`, affected page components |
| WP7 — command feedback | Added scoped busy labels and errors, hardware-rescan confirmation, and actionable empty states. | Dashboard, Outputs, Routes, and Sources components |
| WP8 — operator efficiency | Added query-persisted filters, bulk route controls, global shortcuts, form-based log search, live tail, virtualized tables, and explicit new-tab treatment. | Sources, Routes, Logs, layout, `wwwroot/broadcastrouter.js` |
| WP9 — routing matrix | Added the source-by-output matrix, physical-card column groups, state patterns, sticky axes, roving keyboard focus, drag-to-prefill, density persistence, and responsive detail panel. | `Components/Pages/RoutesPage.razor`, `Components/Shared/RouteDetailPanel.razor`, CSS/JS |
| WP10 — copy/state labels | Standardized recency/absolute timestamps, removed hedged plurals and skin/edition copy, mapped every known state, and humanized unknown PascalCase states. | Page/layout components, `StateBadge.razor`, `OperatorTime.cs` |
| WP11 — undo | Added a persistent 30-second countdown bar to clear Emergency stop and restore a removed assignment with its prior output, preset, priority, and reservation policy. | `Components/Shared/UndoBar.razor`, Dashboard, Routes, route detail, CSS |
| WP12 — verification | Completed build, regression, responsive, authentication, keyboard, error-path, dirty-navigation, matrix-scale, and simulation command checks. | This report and release metadata |

## Verification evidence

- Release build: zero warnings and zero errors.
- Regression runner: 130 of 130 tests passed.
- Browser interaction suite: 76 of 76 checks passed with no console or page errors.
- Authenticated suite: 16 of 16 checks passed, including wrong-password feedback and Operator-role denial.
- Keyboard traversal: every enabled visible control was reached on all eight pages (272 controls total); every focused control had a visible two-pixel ring.
- Viewports: 360, 640, 768, 1024, 1280, and 1920 CSS pixels.
- Matrix stress layouts: 4, 8, and 16 output columns; zero, five, and 30 source rows. The 8/16 and 30 variants were DOM layout stress fixtures based on the real rendered matrix; live command wiring was exercised against the normal four-output simulation fixture.
- Interaction paths: discovery refresh, hardware rescan, filtering, density, keyboard cell movement, log form submit, live tail, all three dirty-navigation dialogs, simulation stall/recover, Emergency-stop undo, and removed-assignment undo.
- Contrast: all primary token foregrounds measured at least 4.76:1 on all three authored surfaces; the blue focus ring measured 5.41:1 or better.
- Static audit: one authored CSS file; no `window.confirm`, raw `ex.Message`, component inline styles, old edition/sidebar state, icon-only breakpoint, or retired stylesheet references.
- `impeccable detect` could not be executed because that executable is not installed on this workstation. The authored patterns named by the supplied detector report were checked directly and removed; vendored Tabler was not modified.

## Environment limits

The GUI work was deliberately verified in isolated simulation instances. No production routing process or physical DeckLink output was exercised by this presentation-layer remediation. Physical SDI picture/audio validation remains a separate hardware acceptance activity.
