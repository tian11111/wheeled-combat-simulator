# Settings UI Guidelines

Conventions for `godot/src/SettingsPanel.cs` and the settings domain
(`DesktopSettings`, `SettingsUiText`, `SettingsBundle`, `SettingsOptionIds`,
`SensorChannelEdits`, `DesktopSettingsDiff`). Established in task
`10-06-settings-ui-optimization` (2026-10-06); each rule exists because a real
bug or wasted cycle proved it. Evidence screenshots live in
`godot/docs/qa-10-06/`.

## Convention: Note labels must not combine autowrap with ClipText

**What**: Explanation/warning lines are built with `AddNoteLabel` (autowrap
enabled, `ClipText=false`). Fixed-width single-line labels that must truncate
use `AddLabel(..., ClipText=true)` — never turn autowrap on for them.

**Why**: On Godot 4.7, a `Label` with `AutowrapMode != OFF` **and**
`ClipText=true` degrades `get_minimum_size` to (1,1). Inside containers that
trust minimum size the label lays out at zero height — the text is silently
invisible (this hid every per-page explanation line in the settings dialog for
months; see `b4-01-display-720.png` vs the pre-fix baselines in the task's
research audit).

**Wrong vs Correct**

```csharp
// Wrong: renders at zero height in containers
var l = AddLabel(row, text);
l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
l.ClipText = true;

// Correct: multi-line note that always claims its height
AddNoteLabel(parent, text);
```

## Convention: OptionButton semantics come from ids, never indexes

**What**: Every option list is declared as `MakeOption(("label", id), ...)`.
Reading uses `GetSelectedId(optionButton)`, writing uses
`SelectById(optionButton, id)` (helpers in `SettingsOptionIds.cs` —
Godot-free so `Sim.Tests` can pin them). Never compare or switch on
`Selected` indexes. New mode ids go into named constant classes (e.g.
`BlockPlacementModes`), and the settings-file values keep their historical
strings.

**Why**: `MakeOption` originally ignored ids and every consumer switched on
insertion order; adding, removing, or reordering options silently remapped
meaning with zero compiler protection. A source-scan test additionally
forbids index-based reads and asserts id uniqueness per control (a duplicate
id would silently fall back to the first option).

## Convention: Path inputs get a browse button over one shared FileDialog

**What**: Path/command rows use `MakePathRow` with a "浏览…" button; the panel
owns a single `FileDialog` parameterized for directory vs file mode (bundle
save/open dialogs stay separate instances because they need SaveFile/
CurrentFile semantics). All file dialogs set `UseNativeDialog = true`
(acceptance decision 2026-10-06): the OS-native dialog is the product look,
so don't re-theme the built-in one — note this also means dialog appearances
won't show up in `--capture` screenshots (they are OS windows, not viewport
content).

**Why**: Five dialogs used to show Godot's built-in English titles ("Save a
File"): **`FileDialog.FileMode` setter overwrites `Title`**, so `Title` must
be set *after* the file mode (pinned in code comments; evidence
`b4-10/11-browse-dialog-*.png`). One shared instance also keeps dialog
filters and titles consistent instead of diverging per feature.

## Convention: Dialog size must be scale-aware and double-scroll safe

**What**: Modal size is `min(designed, viewport × 0.9 / uiScale)` on both
axes, recomputed for uiScale/viewport changes; each page scrolls inside its
tab and an outer `ScrollContainer` is the overflow backstop; the tab area's
minimum height is a compression floor, not a layout constant.

**Why**: The settings dialog was a fixed 980×620 and got clipped at
uiScale 1.4 (1372×868 effective > 1280×720 default viewport) and on small
windows (min window 640×360). Pushing page minimums too high instead pushes
the footer buttons off-screen under the outer scroll — footer visibility at
1.4 is the acceptance check (`b4-01..08-*-s14.png`).

## Convention: User-visible settings copy lives in SettingsText; catalog entries carry Description

**What**: Fixed UI copy (labels, placeholders, filters, dialog titles, footer
notes) is a `const` in `SettingsUiText.SettingsText`; dynamic explanations are
methods there. Every `SimulationParameterCatalog` entry has a Chinese
`Description` (direction of effect + unit), rendered as the control tooltip
together with its range/default; the unit is shown once (SpinBox `Suffix`),
never as a second label column.

**Why**: Tooltips used to expose raw machine keys (`EDGE_THRESHOLD`) and
units were displayed twice per row; hardcoded copy scattered across
`SettingsPanel` drifted from actual behavior (stale "下一场或 F5" claims).
`DesktopSettingsTests` pins that every catalog entry has a non-empty
description, so new parameters cannot land undocumented.

## Convention: Validation messages are single literals; localization is display-layer only

**What**: `DesktopSettings.Validate()` emits each message as one string
literal (no two-part concatenation across lines). The English templates are
matched 1:1 by `SettingsValidationMessages.Localize` in `SettingsUiText.cs`,
which owns the Chinese rendering; unrecognized messages pass through
verbatim. Adding/changing a template requires updating the mapping table and
`ValidateTemplateCount` in `SettingsInteractionTests` (a source-scan test
counts literals inside the two validate regions and fails on truncated
templates).

**Why**: `Validate()` stays structured and testable in English, while users
see Chinese; the scan keeps the mapping table from silently missing new
templates (a mapping miss shows raw English inside an otherwise Chinese UI).
`Validate()` itself must never embed Chinese.

## Contract: Extending DesktopSettings / SettingsBundle

**What**: New `DesktopSettings` fields are nullable records with defaults that
reproduce current behavior exactly ("跟随场景" semantics); serialization omits
them until non-default, so old settings files round-trip without gaining keys
and behave bitwise-identically. `SettingsBundle` has its own
`bundleSchemaVersion` — bump it only for breaking bundle-shape changes;
importing rejects other versions with a Chinese error and never migrates.
Behavior-relevant setting groups must participate in
`DesktopSettingsDiff.MatchRelevantEqual` (which decides "apply → auto-reset
the live match"); a group missing from that comparison produces silent
"next-match" semantics and a lying "display settings applied" log line.

**Tests required** (patterns in `SettingsInteractionTests` /
`MatchOverrideSettingsTests` / `SettingsBundleTests`):
- default-pinning: legacy JSON without the new fields → defaults asserted
  field-by-field, and `ProtocolJson.Serialize` output must not contain the
  new keys;
- diff coverage: changing each new field alone flips `MatchRelevantEqual`;
- bundle: round-trip each section verbatim, tampered version rejected.
