# System context · WorkflowBuilder

**C4 Level 1.** A Blueprint author describes repeatable work, a caller starts a Process, and an operator follows the Steps that execute. A Phase Series connects multiple Blueprints. A Data Bridge maps the outputs of one phase into the next.

```mermaid
flowchart TB
  author(["Blueprint author · person"])
  ops(["Process operator · person"])
  caller["Calling application · external system"]
  wb["WorkflowBuilder · software system<br/>Authors Blueprints and executes Processes"]
  idp["Keycloak · identity system"]
  provider["Domain service · external system"]
  author -->|Authors Blueprints and Phase Series| wb
  ops -->|Starts Processes and inspects Steps| wb
  caller -->|Starts a Process or triggers a phase via HTTP| wb
  wb -->|Uses OIDC sign-in and token validation when enabled| idp
  wb -->|Calls domain APIs through configured Plugins| provider
```

A Plugin developer supplies a packaged task implementation. Platform engineers deploy it and run the services. A business process owner may also act as author or operator. The stakeholder profiles describe needs beyond the currently implemented interfaces. [Stakeholder profiles](../stakeholder-roles/README.md) · [Plugin development](../agent-context/plugin-development.md)

```mermaid
flowchart TB
  dev(["Plugin developer · person"])
  platform(["Platform operator · person"])
  system["WorkflowBuilder · software system"]
  dev -->|Supplies versioned Plugin bundles| system
  platform -->|Configures, deploys and monitors the services| system
```

The public Blueprint start endpoint resolves the latest version for a Blueprint definition. Phase Series can select a pinned Blueprint version or resolve the latest version for a phase. The Phase Series API also accepts external triggers for eligible phase transitions. [Blueprint start service](../../Backend.API/Services/BlueprintProcessService.cs) · [Phase start and trigger service](../../Backend.API/Services/PhasedBlueprintProcessService.cs)

[Explore the containers](containers.md) · [Architecture index](README.md)
