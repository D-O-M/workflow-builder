# WorkflowBuilder documentation

## Learn the platform

- **Start with a working result.** Author a Blueprint, run its first Process and inspect the output of a Step. [First Process tutorial](tutorials/first-process.md)
- **Learn the vocabulary.** Understand how Blueprints, Processes, Steps, Plugins and Phase Series fit together. [Platform overview and glossary](agent-context/overview.md)
- **Understand the users.** Read the motivations of authors, operators, Plugin developers and platform owners. The profiles also cover needs identified in discovery. [Stakeholder roles](stakeholder-roles/README.md) · [Product discovery](product/discovery-notes.md)

## Accomplish a task

| Task | Guide |
| --- | --- |
| Configure capability settings, rotate secrets, or disable a Plugin | [Engine management](how-to/engine-management.md) |
| Route a Process using a typed rule | [Conditional Branch](how-to/conditional-branch.md) |
| Pass data between phases and resume a waiting Phase Series | [Phase Series and Data Bridge](how-to/phase-series.md) |
| Authenticate a caller, start a Process, and retrieve its Steps | [Process API](how-to/process-api.md) |
| Build and register a reusable task | [Plugin authoring](how-to/plugin-authoring.md) |
| Diagnose a pending Step, a failed handoff, or a stale screen | [Operations runbooks](how-to/operations.md) |
| Develop, build, debug and test locally | [Developer workflows](agent-context/developer-workflows.md) · [UI commands](../UI/README.md) · [Integration tests](../IntegrationTests/README.md) |
| Provision the Azure demo or measure throughput | [Terraform setup](../Infra/Terraform/README.md) · [Demo environment](../Infra/Terraform/environments/demo/README.md) · [Performance tests](../PerformanceTests/README.md) |

## Explore the architecture

Follow the C4 views from the system boundary to the Engine's recovery checkpoints. Deployment views identify which services run in each environment. [Architecture index](architecture/README.md) · [Local and Azure deployment](architecture/deployment.md) · [Architecture decisions](decisions/README.md)

## Look up a contract

| Subject | Reference |
| --- | --- |
| HTTP routes and example requests | [Process API and development OpenAPI](how-to/process-api.md) · [Runnable request file](../Backend.API/Backend.API.http) |
| Browser authentication, service credentials and the internal publisher | [Authentication](authentication-keycloak.md) |
| Data ownership, queues, projections and recovery options | [Topology and runtime](agent-context/topology-and-runtime.md) · [Topology catalog used by scripts](../BuildScripts/workflowbuilder-topology.json) |
| Bundle contents, ABI, capabilities and runtime readiness | [Plugin contracts](agent-context/plugin-development.md) |
| UI routes, canvas and runtime subscriptions | [Frontend map](agent-context/frontend-map.md) · [Realtime and observability](agent-context/realtime-and-observability.md) |

When changing a contract, update its reference and the guide that exercises it. The [documentation review register](reconciliation.md) records the evidence behind this refresh, open decisions and commands for checking links and diagrams.

## Engineering audits

The [November 2026 codebase audit](audits/2026-11-codebase/README.md) records source findings, reproduced defects, verification results and feedback from all seven stakeholder-role reviews.
