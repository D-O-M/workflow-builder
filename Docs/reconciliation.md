# Documentation reconciliation

This register records the review of the implementation at `16a2105` on 6 November 2026. Use the linked guides for current procedures. When behaviour changes, update the affected reference and its worked example in the same pull request.

## Findings and dispositions

| Area | What the review established | Disposition and evidence |
| --- | --- | --- |
| Internal publisher | The public hub and internal publishing routes share a service. The bearer role check, shared-secret fallback and Development exception determine access. | Corrected the [authentication table](authentication-keycloak.md) and [Azure guide](../Infra/Terraform/README.md) against the [authorization function](../Backend.SignalR/Program.cs). |
| Process launch and Phase Series | Direct start resolves the latest version of a Blueprint definition. A Phase Series can pin a version for an individual phase. Its start returns a parent and a child ID. | Updated the [frontend map](agent-context/frontend-map.md) and added [HTTP](how-to/process-api.md) and [phase handoff](how-to/phase-series.md) examples from the [start services](../Backend.API/Services). |
| Engine and Plugin readiness | The Engine starts or reloads reusable agents. A Step selects a runnable task version in its allowed lane. The seeder and detector both enter bundle processing. | Split the [registration interaction](architecture/interactions.md) from [Engine execution](architecture/engine-execution.md). Replaced the auto-attach instructions with [manual attachment](../Backend.Engine/ManualTestInstructions.md). |
| Runtime guarantees | A durable dispatch intent protects queue delivery. A persisted result checkpoint allows finalization to resume. Process-start repair and finalization recovery have separate switches. | The [L4 view](architecture/engine-execution.md) maps interruption points to tests. [Operator procedures](how-to/operations.md) identify the records, workers and settings to check. |
| Dashboard, graph and logs | Dashboard reads HTTP projections. Process Explorer subscribes to SignalR. Logs originate in the observability pipeline and stream through the API. | Corrected [realtime guidance](agent-context/realtime-and-observability.md) against the [Dashboard](../UI/src/app/pages/dashboard-page.component.ts) and [observability endpoints](../Backend.API/Controllers/ObservabilityController.cs). |
| Getting started and tests | Bundles are generated locally. The scaffold UI README and weather request gave newcomers the wrong commands and endpoint. Integration CI is manually dispatched. | Added the [first Process](tutorials/first-process.md), a usable [request file](../Backend.API/Backend.API.http), [UI commands](../UI/README.md) and corrected [integration instructions](../IntegrationTests/README.md). |
| Deployment and performance | Local Aspire, low-cost Azure demo and warm performance profiles enable different services. The Azure Keycloak module bootstraps an admin account. The realm and clients require separate provisioning on Azure. | [Deployment views](architecture/deployment.md), [demo setup](../Infra/Terraform/environments/demo/README.md) and [performance guide](../PerformanceTests/README.md), cross-checked against [Terraform](../Infra/Terraform) and the [deployment workflow](../.github/workflows/deploy-azure-demo.yml). |
| Examples and discovery | The CI/CD seed requires tools and configuration. The town, airport, delivery and municipal examples call external services. Product and design briefs describe intended use. | Kept domain examples alongside their [seed exports](../SeedData/Blueprints) and [town guide](../Backend.Plugins/ShowcaseTownWorkflow.md). The tutorial uses a deterministic local task. [Role profiles](stakeholder-roles/README.md), [discovery notes](product/discovery-notes.md), [product brief](../PRODUCT.md) and [design guidance](../DESIGN.md) retain their distinct purposes. |
| Contributor references | Agents need the same service names, ownership and commands as human contributors. UI conventions and generator release tables have narrower audiences. | Updated [AGENTS.md](../AGENTS.md), the [repository skill](../.codex-plugins/workflow-builder/skills/workflow-builder/SKILL.md) and [platform overview](agent-context/overview.md). Retained the [UI conventions](../UI/best-practices.md) and [analyzer release records](../Backend.Plugins.Harness.Generator/AnalyzerReleases.Shipped.md) with their projects. |

## Historical material and open decisions

- The [DBML proposal](../db-schema-proposal.dbml) and its [diagram layout](../db-schema-proposal.dbdiagram) describe an earlier design. Use the [EF model and migrations](../Backend.Database.Relational) to establish the current SQL schema.
- The [alternative handshake sketch](alternative-comms-handshake-process.coffee) explores switching to memory-mapped files. The shipping Plugin contract uses stdin and framed stdout. [Protocol implementation](../Backend.Plugins.Communication/PluginProcessProtocol.cs) · [Plugin contracts](agent-context/plugin-development.md)
- The project owner can decide whether to tighten internal publisher authentication or remove the Development exception. The documentation describes what is deployed today. The UI accepts two to four phases and the API accepts one to four. Aligning those limits is a separate product decision. [Authentication](authentication-keycloak.md) · [Phase Series guide](how-to/phase-series.md)

## Repeat the documentation check

Choose the form of a new page by the reader's task. Give a first-time user a tutorial with a known result, give an operator a procedure with an observable success condition, and keep configuration and API contracts in reference pages. Architecture pages explain how the pieces fit together. [Diátaxis documentation framework](https://diataxis.fr/)

The documentation workflow checks Markdown links, heading anchors and source line references in tracked and new, non-ignored files. It renders each Mermaid block in Chromium at a reading width of 896 pixels. A diagram must fit at 75% of its natural scale or larger, which keeps a 16-pixel label at least 12 pixels tall. The check runs on every pull request because renaming a source file can break a guide even without a Markdown change.

```powershell
Set-Location BuildScripts/docs-check
npm ci
npx playwright install chromium
npm run check
```

When a diagram changes, also read it at that width in the pull request. Split crowded views by responsibility and keep the diagram descriptions usable as a text alternative. [Documentation workflow](../.github/workflows/documentation.yml) · [C4 review checklist](https://c4model.com/diagrams/checklist)

## Verification on 6 November 2026

| What was checked | Result |
| --- | --- |
| Links and Mermaid views | Chromium rendered all 16 diagrams at 896 pixels. Negative checks detected a missing file, incorrect path case, broken heading and invalid Mermaid. |
| Backend contracts | 319 tests passed across Topology, Engine, API, SignalR, Security and Plugin Harness. One Engine test was skipped. Topology tests included equality with the generated catalog used by scripts. |
| Authoring UI | Eleven Chromium Playwright tests passed with mocked services across Blueprint Builder, Phases, Start Process and Process Explorer. |
| Tutorial Plugin | The canonical bundler produced a Windows x64 bundle. The published Plugin returned health 14 for score 7 and speed 6. In a second invocation, overriding trigger score 1 with mapped score 14 produced health 28. Both returned alive. This exercised the actual executable and protocol with file-backed input. |
| Local Docker prerequisite | After the Docker Desktop update, the Linux engine answered its health probe with HTTP 200. Full-stack walkthroughs were deferred for a separate session. |
| Deployment | Local AppHost built successfully. Azure demo and NFT settings were inspected against Terraform and the deployment workflow. Azure provisioning was outside this verification. |

### Deferred live verification

The tests above cover the authoring UI with mocked services and the actual Plugin executable with file-backed input. The following checks still need an authenticated local AppHost with its services connected.

- [ ] **First Process in the UI.** Save `First score check`, start it with score 7 and speed 6, and verify in Process Explorer that the Step completes with `Success.Health = 14` and `Success.Alive = true`. [First Process tutorial](tutorials/first-process.md)
- [ ] **Authenticated HTTP start and read.** Sign in, use the access token to start the saved Blueprint, and confirm HTTP 202 returns a Process ID. Read that Process and its starting Step with the same token and confirm the completed output matches the UI example. [HTTP walkthrough](how-to/process-api.md)
- [ ] **Phase Series and Data Bridge.** Start the series and record the parent and first child IDs. Verify that the first child returns health 14 and the parent reaches `WaitingForTrigger`. Trigger phase 1 with speed 14 and score 1. Confirm the response identifies the same parent and a different child, the second Step receives score 14 from the bridge and returns health 28 and alive, and the parent reaches `Completed`. [Phase handoff walkthrough](how-to/phase-series.md)
