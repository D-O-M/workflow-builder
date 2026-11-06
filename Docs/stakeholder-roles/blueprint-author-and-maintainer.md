# UP-02 Blueprint author and maintainer

## Perspective

Usually a business analyst, project or product manager, or product owner, the author translates the business process owner's requirements into a visual Blueprint. Both can read and discuss the design without programming experience.

## Working knowledge

- They can **model a business requirement** by drawing the sequence, identifying upstream and downstream dependencies and walking the owner through ordinary cases and exceptions.
- They understand **how tasks fit together**. Following a value from one task's output to another's input reveals missing data and whether an existing task can be reused.
- They know how to **test the business intent** by choosing representative inputs, predicting the result and tracing where a test first diverges from that expectation.
- They understand **version history and compatibility** well enough to distinguish a draft from the design tested for release and anticipate who still relies on an earlier version.

## What guides their decisions

Given “contact the customer”, an author asks whose details to use and what should happen if they are missing. Those answers give the author and owner a way to test the design together. [Government Digital and Data Profession Capability Framework – Business analyst](https://understand-digital-data-roles-skills.service.gov.uk/role/business-analyst/)

They value being able to try an idea without waiting for a developer, whether that means adapting an existing Blueprint or creating a new one.

## What they need

- Task descriptions in business terms and a visual way to compose, inspect and change their connections.
- A sandbox for experimentation, with clear explanations of design errors and of what went wrong in a test.
- A reliable, secure way to carry the design they tested into production.
- Recent version history they can compare and revisit. They need to be able to return to an earlier design and develop a new version while live Processes and external callers continue to use the versions they depend on.
