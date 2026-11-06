# UP-06 Plugin developer

## Perspective

Plugin developers turn a task into a capability that Blueprint authors can use. The shared harness handles engine communication consistently and securely so they can deliver new capabilities quickly. They help core engine developers improve it when an interface is awkward or a task needs functionality it cannot yet express.

## Working knowledge

- They know **the task and its dependencies**, including valid inputs, what each result promises, how failures surface and which effects a second attempt might repeat.
- They understand **how authors use the catalogue**. They can name a task in business terms and recognise which variations belong together before the options become confusing.
- They know **the harness contract**, including which communication and security concerns it handles and when a missing facility would help developers of other Plugins.
- They understand **testing and compatibility**. They can isolate task logic, investigate a failure in a complete Blueprint and explain how a fix affects existing Blueprints and their authors.

## What guides their decisions

A reusable task still needs a precise meaning. If an external system accepts a request for later processing, the Plugin's result should let an author tell when it is safe to start work that depends on completion.

They also judge the harness from the perspective of someone writing a Plugin for the first time. Can that developer recognise the interface they need and try part of their implementation early enough to learn from the result? [Clarke on evaluating API usability](https://www.cl.cam.ac.uk/~afb21/CognitiveDimensions/workshop2005/Clarke_position_paper.pdf)

## What they need

- Clear harness interfaces that let them concentrate on task logic and provide descriptions, configuration guidance and error messages that Blueprint authors can act on.
- Local tests for an individual Plugin and the ability to step through its code while testing a complete Blueprint.
- An exact history for investigating deployed issues and a way to reproduce them locally where possible. When a fix is urgent, they need to test its effect on existing Blueprints and explain any change an author will have to make. [SFIA 9 Programming and software development](https://sfia-online.org/en/sfia-9/skills/programming-software-development)
