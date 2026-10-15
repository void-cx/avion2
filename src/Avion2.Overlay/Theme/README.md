# Themes

A theme is one resource dictionary (`Theme/<Name>Theme.xaml`) plus an entry in `ThemeManager.Themes` (id, name,
window icon, Fluent accent ramp, whether the home page may play the key-art video). `ThemeManager.Apply(id)` swaps the
dictionary while the app runs, then reloads and retints the Fluent controls of every attached window. Views use the
keys below as `DynamicResource`. A key used with `StaticResource` would keep the first theme's value. The choice is
stored in `overlay.json` (`Theme`), and `--theme <id>` previews a theme without saving it.

| Theme | Id | Look |
|---|---|---|
| AION2 | `aion2` | Official site and armory: night navy, aether glow, Cinzel, pill buttons, star ornaments, key-art video |
| VOID | `void` | Enter The Void design manual v1.1: Void-Schwarz surfaces, violet → blue, Space Grotesk / JetBrains Mono, `//` motif, VOID mark |

## Keys every theme defines

| Group | Keys |
|---|---|
| Type | `DisplayFont`, `BodyFont`, `MonoFont` |
| Text | `TextBrush`, `TitleBrush`, `SecondaryTextBrush`, `MutedBrush`, `ChipTextBrush`, `HighlightBrush`, `SelfBrush`, `AccentBrush`, `EyebrowBrush` |
| Status dots | `LiveBrush`, `WarnBrush`, `IdleBrush` |
| Surfaces | `BackdropBrush`, `DimBrush`, `HairlineBrush`, `HoverBrush`, `RowHoverBrush`, `DividerBrush`, `TileBackgroundBrush`, `TileBorderBrush`, `GridBackgroundBrush`, `GridBorderBrush`, `SourceChipBackground`, `SourceChipText`, `CardBorderBrush`, `CardBackground` |
| Navigation | `NavBarBrush`, `NavTextBrush`, `StatusPillBackground`, `StatusPillBorder`, `NavLink` (style) |
| Effects | `Glow`, `SoftGlow`, `IconGlow`, `NoticeGlow` (may be `x:Null`) |
| Overlay | `OverlayBackgroundBrush`, `OverlayCornerRadius`, `OverlayCardBrush`, `OverlayCardBorderBrush`, `OverlaySelfBrush`, `OverlaySelfBorderBrush`, `StatTextBrush`, `TimerBoxBackground`, `TimerBoxBorder`, `EmblemFillBrush`, `HpTrackBrush`, `HpBarBrush`, `AlertBorderBrush` |
| Notifications | `NoticeBackground`, `NoticeBorderBrush`, `NoticeChipBrush`, `PlaceholderBackground`, `PlaceholderStripeBrush` |
| Templates | `BackdropTemplate`, `BrandTemplate` (ControlTemplates for a `ContentControl`), `VignetteBrush` |
| Styles | `HeroEyebrow` and `HeroTitle` (these set the hero text too), `CardHeading`, `Card`, `Panel`, `Chip`, `Ornament`, `PrimaryButton`, `SecondaryButton` |
| Shell | `DisplayWeight`, `BrandWeight`, `ShellRadius`, `RailLogo` (image), `RailBrush`, `RailActiveBrush`, `SidebarBrush`, `ContentBrush`, `PaneBrush`, `PaneHeaderBrush`, `StageBrush`, `LineBrush`, `FieldBrush`, `FieldBorderBrush`, `SideSelectedBrush`, `SelectedRowBrush`, `AccentTextBrush`, `AccentHoverBrush`, `OnAccentBrush`, `PillBrush`, `PillTextBrush`, `AccentPillBrush`, `SurfaceBrush`, `SurfaceBorderBrush`, `SectionLabelBrush`, `TrackBrush`, `FloatingPanelBrush`, `FloatingBorderBrush` |

The app shell (icon rail, sidebars, list rows, detail panes, flat buttons, icons) lives in `Theme/Shell.xaml` and is
shared by every theme. It only reads the keys above, so a new theme needs the Shell keys and nothing else.

Stat colours (crit, back, double, perfect) and class colours carry meaning and are the same in every theme.

## VOID assets

- `Assets/void-mark.png`: the official Bildmarke (512 × 512 PNG, transparent).
- `Fonts/SpaceGrotesk-*.ttf` ([floriankarsten/space-grotesk](https://github.com/floriankarsten/space-grotesk)) and
  `Fonts/JetBrainsMono-*.ttf` ([JetBrains/JetBrainsMono](https://github.com/JetBrains/JetBrainsMono)), both under the
  SIL Open Font License 1.1 (license texts next to them, copied to `licenses/` on publish).
- Per the manual, third-party software keeps its own name and is themed with VOID colours, the mark and the favicon.
  So the app is still called AVION2, and the Enter The Void wordmark is not re-set.
