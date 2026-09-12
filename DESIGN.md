# Dudu Desktop Companion Design System

## Overview

The physical scene is a Windows 11 laptop used at home and during study, often in soft evening light. The interface should feel warm enough to belong beside an affectionate desktop pet, but quiet enough that settings never compete with the user's work.

## Visual Theme

- Register: product
- Strategy: restrained warm neutrals with one coral action accent
- Material: Windows 11 Mica where available, with opaque high-contrast fallbacks
- Character: modern, warm, uncluttered, native

## Color Roles

Use semantic WinUI resources rather than hard-coded colors in pages.

- Window surface: warm cream-tinted neutral
- Navigation surface: slightly cooler muted lavender neutral
- Secondary emphasis: soft blush
- Primary action and selection: limited warm coral
- Text: warm near-black, never pure black
- Borders: low-chroma lavender-gray
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
- Control and card corners: 12 effective pixels where the native control does not supply its own radius
- Do not place cards inside cards. Prefer sections, dividers, and whitespace for grouping.

## Components

- Shell: custom title bar plus left compact NavigationView with exactly seven destinations
- Primary buttons: coral accent, concise verb labels, one per decision area
- Secondary actions: standard WinUI buttons or text links
- Forms: native controls, labels above fields, validation adjacent to the field
- Progress: text such as “Step 2 of 6” plus accessible progress semantics
- Empty states: a short explanation and one useful action, optionally accompanied by a small Dudu pose
- Status: icon, title, and concise explanation; never hue alone

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

## Prohibited Patterns

- Decorative glass cards, gradient text, side-stripe callouts, identical card grids
- Nonstandard toggles, scrollbars, menus, or dialogs used for visual novelty
- Pure white or pure black surfaces
- Excessive coral on inactive or decorative elements
