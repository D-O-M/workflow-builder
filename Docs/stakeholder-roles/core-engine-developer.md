# UP-05 Core engine developer

## Perspective

Core engine developers maintain the engine that invokes Plugins, transforms their outputs into inputs for later Steps, persists state and reports on progress and engine health. They share responsibility for the Plugin harness with Plugin developers and care about its usability and overhead.

## Working knowledge

- They understand **orchestration across domains**. Following control and data through a Blueprint tells them which behaviour belongs in the engine, a Plugin or the authored sequence.
- They know **how concurrent work and durable state interact**. A Plugin can finish before its result is recorded, which affects what a restart could repeat.
- They can **locate execution overhead** by separating time spent doing the task from time spent passing data, recording progress or waiting under load.
- They understand **how to evolve and debug the system**. Versions, state and timing help them reproduce failures. Regression tests and experience using the harness help them judge changes to its contract.

## What guides their decisions

When a new requirement arrives, they ask whether a different business could use the underlying mechanism. When an optimisation promises faster processing, they weigh the time saved against correctness and their ability to reconstruct a failure. [SFIA 9 Software design](https://sfia-online.org/en/sfia-9/skills/software-design)

## What they need

- Code they can navigate and a local environment where they can step through the whole system and inspect state. This matters especially for intermittent failures that are difficult to explain from logs alone.
- An exact history of deployed failures, a clean local reset, and control over component, Blueprint and Plugin versions so they can reproduce the original conditions wherever dependencies can run locally or be emulated.
- Automated regression tests, representative performance measurements, and access to a deployed environment for tests that require real network integrations.
