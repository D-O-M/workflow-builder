# UP-04 Platform and cloud team

## Perspective

Platform and cloud engineers deploy WorkflowBuilder and keep it available. They manage capacity, network access and credentials through the surrounding infrastructure and balance availability with running costs.

## Working knowledge

- They understand **environment dependencies**, including how network access, credentials from a secure store and application configuration work together to make a deployment usable.
- They know **how demand translates into capacity**. Throughput, resource use and tail latency help them tell when more capacity would help and when a slower dependency is limiting progress.
- They judge **deployment risk** by how a change affects live work. A capability can often be introduced independently. A core upgrade may call for pausing and draining work first.
- They know **how to verify continuity** after disruption by checking that accepted Processes resume and the backlog clears while new work arrives.

## What guides their decisions

Frequent capability releases should be routine. The team values being able to introduce one while live Processes continue, just as it values infrastructure that can absorb a spike and release excess capacity during a quiet period.

Being able to demonstrate recovery under realistic conditions gives them confidence to deploy a change or restart part of the system. [SFIA infrastructure operations](https://sfia-online.org/en/sfia-9/skills/infrastructure-operations) and [SFIA availability management](https://sfia-online.org/en/sfia-9/skills/availability-management)

## What they need

- Reliable deployments and updates with minimal disruption to work in progress.
- Preservation of Process state and continuation after a crash or deliberate restart, with the ability to pause and drain work for major maintenance.
- Efficient horizontal scaling and visibility into health, resource use, throughput and latency, including p95.
- Reliable network connectivity to dependencies and a secure way to supply credentials from stores they manage.
