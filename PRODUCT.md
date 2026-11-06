# Product

## Register

product

## Users

The UI serves Blueprint authors, Process operators and engineers investigating Step progress and failures. For the full set of roles, including business process owners, Plugin developers, platform teams and product owners, see the [stakeholder profiles](Docs/stakeholder-roles/README.md).

## Product Purpose

WorkflowBuilder is a blueprint-driven orchestration tool. It helps users define versioned blueprint graphs, launch processes from those blueprints, and understand process and step health in near real time.

Success means a user can see where they are, identify the next operational action, and trust that status, history, logs, and canvas signals are telling one coherent story.

## Brand Personality

Calm, technical, and accountable. The interface should feel like a reliable operational workbench: dense enough for repeated use, quiet enough for debugging, and precise enough for runtime state.

## Anti-references

Avoid marketing-page drama, decorative gradients, glass effects for their own sake, oversized card grids, and novelty controls that obscure standard workflows. Avoid reintroducing old domain language such as workflow, execution, or run for new domain-facing UI.

## Design Principles

1. Preserve the mental model: Blueprint is design time, Process is runtime, Step is execution evidence.
2. Make status legible before details: users should understand health, progress, and failure attention quickly.
3. Keep the canvas central: surrounding panels and controls should support graph work without competing with it.
4. Prefer earned density: repeated operational surfaces should scan well without feeling cramped.
5. Use familiar product patterns: Angular Material controls, predictable navigation, clear focus states, and semantic actions.

## Accessibility & Inclusion

Target WCAG AA contrast for text and semantic state indicators. Do not rely on color alone for process or step status. Keep icon-only controls labeled, preserve keyboard focus visibility, support reduced motion, and avoid hover-only workflows.
