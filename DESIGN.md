# Dudu Desktop Companion Design System

## Overview

The physical scene is a Windows 11 laptop used at home and during study, often in soft evening light. The interface should feel warm enough to belong beside an affectionate desktop pet, but quiet enough that settings never compete with the user's work.

## Visual Theme

- Register: product
- Strategy: pastel warm neutrals taken from Dudu himself (cocoa brown, cream, blush, honey) with one rose-coral action accent
- Material: Windows 11 Mica where available, with opaque high-contrast fallbacks
- Character: modern, warm, cute, uncluttered, native

## Color Roles

Use semantic WinUI resources rather than hard-coded colors in pages.

Every key below exists in the `Default`, `Light`, `Dark`, and `HighContrast`
theme dictionaries of `Themes/Colors.xaml`; the high-contrast dictionary maps
each one to a system color brush.

- `WindowSurfaceBrush`: warm cream; `NavigationSurfaceBrush`: a slightly deeper cream
- `CardSurfaceBrush`: near-white cream for ordinary cards
- `BlushSurfaceBrush`: soft blush for the Dudu status card, incoming notes, pairing status and onboarding
- `HoneySurfaceBrush`: pale honey for the UK clock, focus, helpful defaults and section-heading chips
- `SoftBorderBrush`: low-chroma cocoa-tinted border for every card and pill
- `CocoaAccentBrush`: Dudu's brown, used only for small labels such as "time in the UK"
- `CoralActionBrush`: rose-coral primary action and selection, with `PrimaryButtonForegroundBrush` text
- `TextPrimaryBrush`: warm cocoa near-black, never pure black; `TextSecondaryBrush` for helper copy
- Dark theme keeps the same roles as dim cocoa surfaces with cream text
- Success, warning, error, and info: distinct hue plus icon and text, never color alone
- High contrast: defer to system brushes and preserve every control boundary

## Typography

- Family: Segoe UI Variable with Windows system fallback
- Body: 14 effective pixels, comfortable line height
- Labels and navigation: 14 effective pixels, medium weight for selection
- Section headings: 20 effective pixels, semibold
- Page titles: 28 effective pixels, semibold
- Keep prose near 65 characters per line and avoid display typography in controls

## Spacing and Shape

- Base spacing unit: 8 effective pixels
- Compact gaps: 4; standard gaps: 8, 16, 24; page rhythm: 32
- Page content: `MaxWidth="880"`, margin 32,24,32,32, 20 between cards
- Cards (`CardStyle`, `BlushCardStyle`, `HoneyCardStyle`): corner radius 18, padding 20, 1 px soft border
- Confirmation callouts (`ConfirmCardStyle`): radius 16, blush fill, warning outline. This is the one inset allowed inside a card, because it must sit next to the action it confirms.
- Status pills (`StatusPillStyle`): radius 14
- Buttons: pill shaped (radius 20 on the 40 px minimum height), set in the implicit style and repeated in `PrimaryButtonStyle` because an explicit style replaces the implicit one
- Do not otherwise place cards inside cards. Prefer sections, dividers, and whitespace for grouping.

## Components

- Shell: custom title bar plus left compact NavigationView with exactly seven destinations
- Section headers: a Grid with an `Auto` column holding a round 36 px honey chip (`IconChipStyle`) with a decorative emoji (`ChipEmojiStyle`, `AccessibilityView="Raw"`) and a `*` column holding the `SectionHeadingStyle` text. Use a Grid, never a horizontal StackPanel, so the heading wraps instead of clipping.
- Button groups: two equal `*` columns with stretched buttons; an odd primary button or the last button spans both columns. Groups whose labels are longer than about 20 characters, and destructive groups, stay stacked vertically.
- Status feedback: each page is a two-row Grid; the scrolling content is row 0 and a sticky polite live-region footer in row 1 holds the status and error rows, each wrapped in a `StatusPillStyle` border, so feedback stays visible without scrolling
- Home order: UK clock, Dudu status and pet/pause, Dudu actions, comfort, countdowns (the add form is in an Expander), check-in, startup
- Primary buttons: coral accent, concise verb labels, one per decision area
- Secondary actions: standard WinUI buttons or text links
- Forms: native controls, labels above fields, validation adjacent to the field
- Progress: text such as “Step 2 of 6” plus accessible progress semantics
- Empty states: a short explanation and one useful action, optionally accompanied by a small Dudu pose
- Status: icon, title, and concise explanation; never hue alone

## UK Partner Clock

- Zone: Europe/London through `Dudu.Core.Time.PartnerClock`, which takes the injected `IClock` and falls back to "GMT Standard Time" or a built-in rule. The offset label reads "BST · GMT+1" in summer and "GMT · GMT+0" in winter.
- Home card: honey card at the top of Home with a day/night badge, large HH:mm digits and smaller seconds, a date line, an offset and difference line ("7 h behind you"), and a short mood line. A 500 ms DispatcherTimer runs only while the page is loaded. The spoken name is on the "time in the UK" label.
- Overlay pill: "UK 14:05" plus a drawn sun or crescent, top-centre above Dudu, drawn by `PartnerClockPillRenderer` in the overlay palette so it follows light, dark and high contrast. It is rendered once per minute and palette/size change and then only blitted, and it fades with Dudu's opacity. A timer re-armed at each minute boundary requests a repaint, so static poses update too. It is skipped when the canvas is under 96 px or the pill would not fit, so it is never clipped.
- The pill's opaque pixels are part of Dudu's alpha hit area, so it drags and clicks like Dudu rather than being click-through.
- Copy stays gender-neutral. Do not use flag emoji; Windows renders them as letter pairs.

## Onboarding

- Six short steps with one main decision per view
- Back is always available after the first step
- Pairing is optional and has an explicit “Skip for now” action
- Recommended defaults emphasize quiet hours, reduced interruption, and fullscreen suppression
- Final completion is atomic and enters Home without opening duplicate windows
- Target fewer than 20 actions with keyboard focus retained in the settings window

## Motion

- UI state transitions: 150 to 250 ms, ease-out, no bounce
- Motion communicates navigation or state only
- Reduced motion replaces movement with static poses and short opacity changes
- No orchestrated page-load animation

## Accessibility

- Stable AutomationProperties.AutomationId on every onboarding action and navigation destination
- Logical tab order and visible keyboard focus
- Touch targets follow Windows guidance
- Text and controls support Windows scaling without clipping
- High-contrast resources override decorative surfaces
- Overlay never steals keyboard focus from another application

## Microcopy

- Lowercase, warm and a little playful ("hi hi, dudu missed u"), with at most one emoji per label
- Settings, privacy, pairing and destructive confirmations stay literal: say what is stored, where, and what an action deletes
- Emoji are decoration only; every emoji-led label must still read clearly without the emoji

## Prohibited Patterns

- Decorative glass cards, gradient text, side-stripe callouts, identical card grids
- Nonstandard toggles, scrollbars, menus, or dialogs used for visual novelty
- Pure white or pure black surfaces
- Excessive coral on inactive or decorative elements
