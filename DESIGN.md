# Design

## Visual Theme

WorkflowBuilder uses a restrained light product theme for focused orchestration work on desktop and laptop screens. The scene is an engineer or operator reviewing process state on a bright office monitor or during a debugging session; the UI should reduce glare, stay calm, and keep runtime evidence readable.

## Color

Use OKLCH-tinted neutrals instead of pure black or white. The default surface is a cool near-white, the canvas is a slightly bluer workbench plane, and the primary accent is a restrained operational blue used for navigation, selected state, and primary actions.

Semantic status colors are reserved for state:

- Success: completed processes and healthy steps.
- Danger: failed processes, failed steps, blocking validation.
- Warning: design-time validation or configuration attention.
- Info: in-progress, selected, streaming, or inspectable state.
- Neutral: pending, not executed, disabled, and inactive state.

## Typography

Use the existing product sans stack for all UI. Keep headings compact, labels precise, and data rows readable. Avoid fluid viewport-scaled product type. Use monospace only for identifiers, payloads, logs, and code-like values.

## Layout

The app uses a persistent left navigation rail, sticky top toolbar, and route-specific work surfaces. Canvas pages should feel full-bleed and tool-like, with overlays that are light, aligned, and easy to dismiss. Dashboard and forms may use cards, but cards should not nest visually or become decorative.

## Components

Buttons, fields, chips, rails, drawers, cards, and tabs should share one visual vocabulary:

- Small-to-medium radii, not oversized pills except for search and compact icon rails.
- Subtle borders and shadows that separate work areas without floating everything.
- Clear hover, focus, active, disabled, loading, empty, and error states.
- Material icons for actions, with visible labels where the action is not obvious.

## Motion

Motion should communicate state changes only: panel reveal, hover lift, progress, loading, or focus. Keep transitions in the 120-200 ms range, avoid layout-property animation where practical, and respect reduced motion.

## Responsive Behavior

Desktop is the primary work mode, but every route must remain coherent on narrow viewports. Panels should collapse or stack structurally, controls should wrap without overlapping, and long blueprint/process names should truncate or clamp intentionally.
