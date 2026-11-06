# 0001 · Document the architecture with C4 and Mermaid

- **Status** — Accepted
- **Date** — 2026-11-06
- **Decision maker** — WorkflowBuilder project owner

## Context

A Process crosses the API, queue, Engine, stores and live-update services. The existing runtime flow helped engineers follow a message, but gave newcomers little orientation and operators little guidance about who recovers a partially completed Step.

## Decision

Publish linked C4 views in repository Markdown and write diagrams as fenced Mermaid. Give each structural diagram a boundary, element responsibilities and labelled relationships. Use the context and container views for the whole platform, component views for the Process API and Engine, and a code-level sequence and checkpoint diagram for Engine execution and recovery. Keep deployment choices and runtime interactions in their own views. [C4 diagram types](https://c4model.com/diagrams) · [Notation](https://c4model.com/diagrams/notation)

Readers can view the diagrams on GitHub and contributors can update them alongside the code. The team considered the existing single runtime flow and a separately published documentation site. A hierarchy of Markdown pages provides multiple levels of detail while using the repository's existing review and reading surface. [Mermaid on GitHub](https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/creating-diagrams)

## Consequences

The same component may appear at several levels. Keep its name and responsibility consistent and link each view to source or tests. Render diagrams during pull requests and review them at repository reading width when their content changes. Revisit the selection of component and code views when readers need to diagnose a different subsystem. [Architecture index](../architecture/README.md)
