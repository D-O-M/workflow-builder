# WorkflowBuilder architecture

Start with the question you need to answer. The C4 views progressively narrow from the whole platform to one part of the Engine where a mistake can duplicate or lose work.

| Question | View |
| --- | --- |
| Who uses WorkflowBuilder, and what connects to it? | [System context · Level 1](context.md) |
| Which applications run, and where does each kind of data live? | [Containers · Level 2](containers.md) |
| What are the responsibilities inside the API, Engine and Progress Broadcaster? | [Components · Level 3](components.md) |
| How does a first Step create its children, and where can execution resume after failure? | [Engine execution · Level 4](engine-execution.md) |
| How do Process start, Plugin registration and live updates cross service boundaries? | [Runtime interactions](interactions.md) |
| What runs locally and in the Azure demo? | [Deployment](deployment.md) |

**Reading the diagrams.** A system is the product a person or another system deals with. A container is an application or data store, including a browser application or a child executable. A component groups responsibilities within one container. The code view names concrete types and methods. Arrows carry a verb or message. Follow the arrow to see the recipient. The diagrams use Mermaid so changes can be reviewed alongside the code. [C4 abstractions](https://c4model.com/abstractions)

For exact queue names, partition keys and realtime events, use the topology catalog. For enabled services and deployment defaults, use the environment wiring. For a stalled Process or a stale view, follow the operations guide. [Topology catalog](../../Backend.Topology/WorkflowBuilderTopology.cs) · [Local wiring](../../Backend.AppHost/Program.cs) · [Azure demo wiring](../../Infra/Terraform/main.tf) · [Operations](../how-to/operations.md)
