# WorkflowBuilder codebase audit · 6 November 2026

**Fix the boundaries that can change a customer’s outcome before reorganizing the code around them.** A claimed Step can survive its worker without a recoverable checkpoint. A Phase Series trigger can acknowledge an uncreated child. A Plugin release name can identify different executable bytes. Each problem crosses components that look reasonable in isolation.

Naming and readability matter most at these boundaries. The same start operation uses `BlueprintId` for both a definition and a version, a queue message stands in for recovery work that has no queue, and the Phases editor conflates the version on screen with the policy the author saved. The audit records **37 findings**. Sixteen are high priority under the conditions described, and 21 are medium priority. Five focused probes reproduced specific behaviors.

| Read this section | Questions it answers |
| --- | --- |
| [Process execution, recovery and reporting](runtime-and-data.md) | What can happen to accepted work after a crash? Can operators account for every case and trust a terminal update? |
| [Plugin contracts, access and deployment](plugins-and-deployment.md) | Who chooses a credential destination or filesystem target? Can two Engines reconstruct the same release, and will idle workers wake? |
| [Blueprint authoring, Phase Series and catalog](authoring-and-catalog.md) | Does opening and saving preserve intent? Can overlapping edits, validation and catalog reads be understood from the UI? |
| [Naming, structure and development practice](engineering-practice.md) | Which names mislead, which responsibilities belong together, and which tests and build conventions would catch regressions? |
| [Stakeholder review](stakeholder-review.md) | How did all seven role reviewers challenge the priorities, and what would persuade each that a fix works? |

## Recommended order of work

| When and who | First change | Evidence needed to close it |
| --- | --- | --- |
| Before additional callers get a credentialed Plugin or CI/CD workspace · Plugin and platform owners | Constrain DCC destinations and checkout paths. Apply role checks at ingestion mutations. Separate service secret access before relying on workload isolation. **P1–P3, P8** | Approved tasks keep working. An unapproved origin receives no request, a sibling directory survives cleanup and a read-only identity cannot alter quarantine or fetch unrelated credentials. |
| Before a busy Engine rollout or a scale-to-zero release · Engine and cloud teams | Give claims a recoverable owner, keep runtime resolution Step-local, reconcile a reserved phase child and wake the Dispatcher from durable work. **R1–R3, R7, P7** | Kill a worker after claim and after an observed side effect. Trace the original IDs through recovery. Check cold recovery and old records during rollout. |
| Before publishing another Plugin patch · Release and Plugin owners | Give published content an immutable identity and make every eligible Engine reconcile its catalog revision. **P4, P5** | Publish from two clean checkouts with conflicting payloads, then roll a legitimate patch across two live replicas. Verify bytes, catalog version and historical Step provenance. |
| In the same release wherever people rely on the affected screens · API and frontend teams | Preserve Latest/Pinned, prevent stale draft writes, repair indexed search, and make final status and log coverage dependable. **A1–A4, R4–R6, R8–R9** | Reopen a mixed-policy series, find every affected iterator case across pages, and reconcile a stale Explorer session to the durable outcome. |
| Start alongside those fixes · Maintainers | Run fast backend/UI checks on PRs. Then extract one tested boundary at a time, beginning with the duplicated start operation, overloaded IDs and queue-free recovery. **E1–E10, A5–A6, P6** | Existing callers, old documents, ABI contracts and observable behavior survive each change. Reviewers can locate and change one policy without editing several copies. |

**Priority convention.** High indicates a credible path to unsafe effects, lost or misleading work, exposure, or an important release failure in the stated configuration. Medium indicates recurring change cost, reduced confidence or a narrower operational problem. These are engineering priorities. Frequency, production incidence and exposure depend on the deployment.

## What was examined

The baseline was a private source copy of the working tree on 6 November, based on `fa791253a1f4d234ddd76d7e46afc01c14787458`. The inventory used `git ls-files --cached --others --exclude-standard` and existing files. It covered **1,451 files**, including **717 C# files, 161 TypeScript files, 110 C# projects, 63 Terraform files and 11 PowerShell scripts**. Ignored build products, dependency trees, local credentials and generated Plugin ZIPs were excluded. Conditional-branch work was present. Engine Management and other edits continued to arrive while the review ran. Build and test results below refer to the copied snapshot. Source links were checked against the working tree during write-up.

The review combined a repository-wide file and project-reference inventory with manual traces of representative entry points and their failure paths. Tests, docs and configuration were checked against the code where they could challenge the findings. File-size counts guided inspection. Generated EF migration code was excluded from the size comparison. This was a source and local test audit, with no line-by-line attestation or production traffic measurement.

| Area | Traces and boundaries examined |
| --- | --- |
| Design and launch | SQL entities and version constraints, repository contracts, validation pipeline, portable Blueprint data, API start, Phase Series triggers and the Angular load/save path. |
| Engine and state | Graph cache, claim, Plugin dispatch, native iterator/conditional integration points, finalization, Cosmos records and repositories, Dispatcher, Maintenance and backfill entry points. |
| Reads and operations | Search and cursor compatibility, projection workers, summary/detail/delta reads, broadcaster, SignalR subscription registry, Explorer, Loki queries and Dashboard totals. |
| Plugin ecosystem | ABI and capability declarations, generator and harness, typed configuration, concrete DCC and CI/CD tasks, bundle creation, seeding, registration, launch and reload. |
| Delivery and maintenance | AppHost and shared service defaults, Azure demo Terraform, identity/configuration and worker scaling, GitHub workflows, integration fixtures, performance tooling, test entry points and repository documentation. |

## Verification

The isolated snapshot prevented Plugin build targets from rewriting the live checkout’s manifests. The frontend comparison used locked dependencies and the same Node installation. Suggested checks in individual findings are future regression cases unless described as reproduced here.

| Check | Observed result |
| --- | --- |
| `dotnet build Backend.slnx -m:3` on .NET SDK 10.0.401 | Succeeded with **0 errors and 372 warnings**. NuGet emitted repeated NU1902/NU1903 package advisory warnings. See E10. |
| `dotnet test --project <project> --no-build` for all ten projects under the solution’s Tests folder | **423 passed, 1 skipped, 0 failed**. The skipped Engine test checks Linux executable permissions. API, DCC, Plugin Loader, Plugin Harness, Security, Service Defaults, Showcase Town, SignalR and Topology all passed. |
| `npm ci` and `npm run build` from UI, Node 24.18.1 | Production build succeeded in the snapshot. Initial output was **1.98 MB**, exceeding the **1.80 MB** warning budget by **177.36 kB**. A separate live-checkout build encountered an incomplete Engine Management UI during concurrent edits. |
| `npm run test:unit` in the snapshot and again on an isolated archive of the base commit | Snapshot had **267 passed, 28 failed** and one additional suite failed at import. The editor fixture lacked `RuntimeClientConfigService` and the Process canvas suite hit an Angular JIT import error. Base commit had **293 of 293 passing**. These failures describe the in-progress snapshot. |
| Four mocked Playwright projects on Windows using two workers and a private local server port | **16 passed, 2 failed**. The conditional-rule authoring test timed out in a select control. The Explorer screenshot differed by 655 pixels. The Linux CI baseline issue in E7 was assessed separately from these Windows results. |
| Indexed Process search probe · **R4** | The compiled query service, SQLite definition and existing index stub returned **2/4** and **4/14** matching Processes after consuming all cursors with limit two. |
| Phase Series round-trip probe · **A1** | Actual open/save handlers with mocked transport converted `Latest, null` to `Pinned, 102` while the other phase stayed pinned to 201. |
| DCC destination probe · **P1** | The actual Plugin sent the same synthetic bearer and full XML to the configured and alternate origins through an in-process capture handler. |
| Per-Step runtime probe · **R2** | With two distinct Step contexts sharing a graph node, selecting `102 / 1.0.2` on B changed A’s previously selected `101 / 1.0.1` to the same new value. Agent invocation and slot accounting were traced separately in source. |
| ZIP extraction probe · **P9** | A marker escaped the designated extraction directory into a prefixed sibling within a disposable sandbox. The probe supplied the workspace path directly. |

**Closing check on the evolving checkout.** Both Angular production targets (WorkflowBuilder and Engine Management) subsequently built, and all **304 UI unit tests passed**. The WorkflowBuilder bundle still exceeded its warning budget. Rerunning the two previously failing Chromium cases on a private server port passed conditional-rule authoring. The Explorer screenshot still differed by 655 pixels, confined to the subtitle below “Weather Lookup” in the task details drawer. Review that text against the intended design before updating the baseline.

**Validation limits.** Distributed AppHost, live Azure resources, Terraform apply, runtime Playwright and sustained performance runs were outside this audit. The integration runner stops existing processes, so it was inspected without invoking it on the shared machine. Crash, scale and multi-replica scenarios are source-traced and have explicit acceptance exercises. Role feedback came from seven separate review agents using the repository’s profiles.

## Practices worth retaining

- **Durable identities and explicit recovery stages.** Deterministic child IDs, stored inputs and named finalization stages make it possible to design a meaningful crash test and replay older work. [Engine recovery walkthrough](../../architecture/engine-execution.md)
- **Clear seams in authoring.** The grouped canvas contract, passive Playwright inspection surface and separate graph/schema validators allow focused tests. They are suitable boundaries for incremental reorganization. [Canvas contract](../../../UI/src/app/components/blueprint-canvas-contract.ts) · [Validator pipeline](../../../Backend.API/Validation/Blueprints/BlueprintVersionValidatorPipeline.cs)
- **Executable contracts.** The generated topology comparison, Plugin ABI tests, typed configuration and generator diagnostics catch disagreements close to their source. Extend this approach to Terraform parity and immutable release content. [Topology tests](../../../Backend.Topology.Tests) · [Plugin Harness tests](../../../Backend.Plugins.Harness.Tests)
- **Documentation tied to operational questions.** The architecture views and operations runbook already trace the same Process through independent services. Add the release drills and authoritative outcome checks where operators already look. [Architecture](../../architecture/README.md) · [Operations](../../how-to/operations.md)
