# Qwen Studio — design system

Every size, spacing, colour and weight in the UI comes from a role below. The roles live as
resources in `App.xaml` (styles and tokens) and `Core/Theme.cs` (colours for light and dark).
If nothing fits, add a role here first — do not put a raw number into a view.

## Colour roles (`Theme.cs`, both themes)

| Role | Use |
|---|---|
| `Bg` | window background |
| `Surface` | cards, sidebar cards |
| `Surface2` | inputs, segmented controls, secondary buttons, chips |
| `Hover` | hover on secondary buttons |
| `Line` | card borders, dividers, idle chart baseline |
| `Text` / `Muted` / `Faint` | primary text / labels and secondary text / captions, timestamps, disabled |
| `Accent` / `AccentSoft` / `OnAccent` | primary action, selection, chart bars / selected card and busy chip fill / text on Accent |
| `Good` / `Warn` / `Bad` | running, success / starting, attention / crash, error, stop action |
| `BadSoft` | hover fill of the danger button |
| `LogText` | raw server lines in the journal |
| `Series1`–`Series5` | chart series that are *categories*, not states: one per profile in profile order (`Series1`–`4`), `Series5` for a server started elsewhere. The old model is its profile's hue at `SeriesAltOpacity` (0.45). |

State colours are used for dots, state words, short numbers and chart markers — never as large fills
and never for a sentence of text: `Warn` on a light surface is hard to read. A warning or success line
stays `Text` / `LogText` and gets a `Dot` in the state colour; only `Bad` may colour an error line.
Light `Warn` is a dark amber (#8F5B00, ~6:1 on white) so a short figure in it stays legible.

Palette (26.09): neutrals are untinted graphite, dark theme lifted to charcoal (Bg #222222, Surface #2B2B2B, like Windows 11 dark) rather than near-black; `Accent` is the Windows 11 accent blue (#4CC2FF dark / #005FB8 light) and is the only hue for action and selection. No violet anywhere. Series run blue, rose, teal, amber (Series1 = `Accent`); they mark chart categories, never states. Series3 (Parallel, the busiest mode) is teal because rose at `SeriesAltOpacity` turns mauve on the dark surface. Values live in `Core/Theme.cs`; `App.xaml` repeats the dark set as defaults.

## Type roles (styles in `App.xaml`)

| Style | Size / weight | Use |
|---|---|---|
| `Display` | 22 SemiBold | server state word in the hero card |
| `Title` | 15 SemiBold | card titles in Settings, app name |
| `Metric` | 20 SemiBold | the big number of a metric tile |
| `Value` | 15 SemiBold | secondary figures (GPU load, power, temperature) |
| `Body` (default) | 13 Regular | ordinary text, buttons |
| `BodyStrong` | 13 SemiBold | names: profiles, tools, row titles |
| `Label` | 12 Regular, `Muted` | field labels, sub-lines, hints |
| `Caption` | 11 SemiBold, `Faint`, upper case | card eyebrows (СЕРВЕР, ВИДЕОКАРТА) |
| `MonoText` | 12 Cascadia Mono | URLs, keys, journal, timestamps |

Fonts: `UiFont` (Segoe UI Variable Text), `Icons` (Segoe Fluent Icons), `Mono` (Cascadia Mono).
Icon sizes: `IconS` 12 (inside buttons), `IconM` 14 (icon buttons), `IconL` 16 (tool rows, header).

## Spacing (4-pt scale)

| Token | Value | Use |
|---|---|---|
| `S1` | 4 | label → value, icon → text in tight places |
| `S2` | 8 | between related controls, button icon → text, list rows |
| `S3` | 12 | between cards, between groups inside a card |
| `S4` | 16 | card padding, sidebar ↔ content gutter |
| `S5` | 24 | page padding, space between hero sections |

Thickness resources built from the scale: `PagePad` (24), `CardPad` (16), `HeaderPad` (24,16),
`BodyPad` (24,0,24,24), `DividerGap` (0,16), `GapBelow` (0,0,0,12), `GapBelowS` (0,0,0,8), `CaptionGap` (0,0,0,8),
`GapTop` (0,12,0,0), `GapTopS` (0,8,0,0), `GapLeft` / `GapLeftS` (12 / 8 left), `GapRight` / `GapRightS` (12 / 8 right),
`SegmentPad` (4). Column gaps: `ColGapS` 8, `ColGap` 12, `ColGapL` 24.

## Shape and sizes

| Token | Value | Use |
|---|---|---|
| `RControl` | 8 | buttons, inputs, segmented controls |
| `RCard` | 12 | cards, profile cards |
| `RPill` | 12 | status pill and activity chip (height 24) |
| `HCompact` | 28 | icon buttons, ghost buttons |
| `HControl` | 32 | regular button / input height |
| `HAction` | 40 | the main action in the sidebar |
| `SidebarWidth` | 288 | left column |
| `ChartHeight` | 120 | 24-hour load chart, 30-day chart |
| `ContentMaxWidth` | 880 | full-width pages (Statistics, Settings) |
| `KeyColumn` / `FieldColumn` | 72 / 160 | key column of the connection list / label column of settings rows |
| `RowHeight` / `FieldRow` | 32 / 40 | table and list rows / settings rows with a control |
| `DotSize` / `LogoSize` | 8 / 20 | status dots / app logo |
| `BarGap` | 1,0 | gap between chart bars |
| `ChartBaseline` / `ChartTick` / `ChartMinBar` | 2 / 3 / 3 | idle baseline, crash tick, smallest visible bar |
| `RBar` | 2,2,0,0 | top corners of a bar (top segment of a stacked bar) |
| `ChartStroke` | 2 | line of the live area chart (approved by the owner 26.09) |

## Components

- **Card** — `Surface`, 1 px `Line`, `RCard`, `CardPad`. Starts with a `Caption`.
- **Buttons** — `Primary` (Accent fill, one per screen: start / switch), default (Surface2),
  `DangerOutline` (Bad text and border, BadSoft on hover — stop), `Ghost` (links, icon buttons).
  Stop is never the most prominent element.
- **Metric tile** — `Label` title, `Metric` number, `Label` sub-line; tiles sit in one row, divided by `S5`.
- **Chip** — `RPill`, `Surface2` (idle) or `AccentSoft` (busy), dot + `Body`.
- **Chart** — 96 bars of 15 minutes. Bar = `Accent` (requests), baseline 2 px `Line` when the server
  was up but idle, empty when it was down, `Bad` tick on top for a crash.
- **Live chart** (same card, `Segments` «Сейчас | 24 часа» in the header, choice remembered) — area chart,
  100 points of 3 seconds = last 5 minutes of GPU load from NVML, so it shows any work on the card (benchmarks,
  ComfyUI). Fixed 0–100 % scale, `ChartHeight`; line `Accent` at `ChartStroke`, area under it `AccentSoft`.
  Holes of up to 2 steps (a slow poll) are bridged; a longer hole breaks the line. Hover: `Dot` in `Accent`
  on the line + the step's tooltip. Summary line `Label` (now % · GB · W · t/s · 5-minute peak).
  Points are aligned to wall-clock steps so the line slides, not jitters.
- **Tool row** (Overview → Инструменты) — `IconL` glyph in `Accent`, `BodyStrong` name, `Dot` + `Label`
  state, default button on the right, `IconBtn` stop before it while the tool runs; rows split by `Divider`.

- **Stacked chart** (Statistics, 30 days) — one bar per day, segments per profile/model in `Series*`,
  profile order bottom-up; legend of `Dot` + `Label` under the axis; metric switch as `Segments`.
- **Table** (Statistics) — header row of `Caption`, row names `Label` (with a series `Dot` when the row is a
  mode), figures `BodyStrong`; rows `RowHeight`; name column two figure columns wide. Failures in `Bad`,
  rejected requests in `Warn`, only when non-zero.
- **Event row** (Overview events, journal) — `MonoText` time in `Faint`, then a `Dot` for Good / Warn / Error
  (hidden otherwise, keeps alignment), then the text.
- **Segments** — `Surface2` track with `SegmentPad`, `RControl`; items use the `Tab` style (selected = `Surface`).
  Used for the page tabs, model switch, chart metric switch and the live / 24-hour switch.
- **Dot** — `DotSize` ellipse, `Faint` by default; colour always from a role.
- **Hint** — `Label` that wraps, under a settings row.
