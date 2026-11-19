# Blueprint authoring, Phase Series and catalog

[Audit overview and priorities](README.md) · [Engineering structure](engineering-practice.md)

## A1 · High · Opening and saving a series can pin a phase the author left on Latest

The Phase Series API can persist a `Latest` policy with a null Blueprint version ID. The editor loads a concrete graph for preview, retains that resolved version in its editable model and drops the original policy. When saving, it writes `Pinned` for every phase. Even a Data Bridge change can silently alter how later Processes select a Blueprint. [Stored policy](../../../Backend.Repositories/PhasedBlueprintRepository.cs#L216-L219) · [Editor load](../../../UI/src/app/pages/phases-page.component.ts#L947-L966) · [Save payload](../../../UI/src/app/pages/phases-page.component.ts#L1114-L1122)

**Reproduced.** An isolated Vitest probe used the real Phases component’s open and save handlers with mocked API and graph reads. Opening a series with `[(Latest, null), (Pinned, 201)]` and saving without an edit produced `[(Pinned, 102), (Pinned, 201)]` at the transport boundary.

**Change and verify.** Represent the author’s selection separately from the graph version displayed. Round-trip both policies through edit, API and storage. After a mapping edit, publish another Blueprint version and start a new series Process. Only the phase configured to follow the latest version should move.

## A2 · High · A delayed phase graph load can overwrite a newer edit

`addPhaseFromSelection` and `changePhaseVersion` capture the whole selected series, await a graph read, then replace current state using their captured copy. A slow first request can undo a newer selection or bridge edit. It can also write its result after the author navigates to another series. The defect comes from who owns the draft during the await. [Add phase](../../../UI/src/app/pages/phases-page.component.ts#L493-L520) · [Change version](../../../UI/src/app/pages/phases-page.component.ts#L524-L555)

**Change and verify.** Key outstanding requests by series, phase ID and selection revision. Apply a still-current result to the current draft. Resolve requests for two phases in reverse order, edit a mapping during a load, and switch series before a response. Assert both the displayed graph and the next save payload.

## A3 · Medium · Concurrent saves compete for the same version number

The Blueprint and Phase Series repositories load existing versions, derive the next number from the maximum, then insert. Two concurrent transactions can compute the same number. A unique constraint protects the stored data, but a losing writer needs a recoverable result and must keep its draft. The Blueprint editor also refreshes and reselects a preferred version after saving. A second save in that interval can move the author away from the version they just created. [Blueprint insert](../../../Backend.Repositories/BlueprintRepository.cs#L346-L368) · [Phase Series insert](../../../Backend.Repositories/PhasedBlueprintRepository.cs#L169-L185) · [Relational constraints](../../../Backend.Database.Relational/BlueprintDbContext.cs) · [Editor refresh](../../../UI/src/app/pages/blueprint-editor-page.component.ts#L677-L692)

**Change and verify.** Make allocation atomic or narrowly retry the relevant uniqueness conflict. Define when the API should return a conflict the author can resolve. Select the exact version returned by a successful save. Run two database connections against the same definition, including edits based on an older immutable version, and ensure each author can identify or recover their own draft.

## A4 · Medium · The Phases page discards actionable validation errors

The repository identifies the invalid destination property. The controller passes the explanation to its problem response. The page replaces it with a generic save failure. An author can repeatedly submit the same invalid mapping without knowing which field to change. [Mapping validation](../../../Backend.Repositories/PhasedBlueprintRepository.cs#L457-L466) · [HTTP error](../../../Backend.API/Controllers/PhasedBlueprintsController.cs#L83-L90) · [Save error](../../../UI/src/app/pages/phases-page.component.ts#L687-L689)

**Change and verify.** Preserve safe validation detail and the submitted draft, and attach a phase/property identifier when available. Keep a separate fallback for an unavailable server. Reject one mapping at the API boundary and verify that the editor names its destination and permits a correction without rebuilding the series.

## A5 · Medium · Runtime pages depend on the Blueprint editor to read Process data

`BlueprintEditorService` combines graph serialization and task-version selection with Phase Series HTTP transport and Process and Step queries. Dashboard, Start Process and Process Explorer consequently import an editor service. This obscures which feature owns a contract and makes a runtime-only test or change pull in editor concerns. The workbench already has smaller canvas, geometry and interaction contracts that show where such boundaries can sit. [Editor service](../../../UI/src/app/domain/blueprint-editor.service.ts) · [Explorer dependency](../../../UI/src/app/pages/process-explorer-page.component.ts) · [Grouped canvas contract](../../../UI/src/app/components/blueprint-canvas-contract.ts#L52-L115)

**Change and verify.** Move Process reads and Phase Series transport to their feature boundaries as callers change. Keep graph mapping with authoring and transport models independent of Angular page state. A Process-only client test should exercise the same HTTP routes and payloads without loading an editor or canvas module. Preserve the public canvas contract and its read-only Playwright inspection seam.

## A6 · Medium · Catalog discovery pays for runtime state the authoring UI discards

The task catalog loads definitions, versions, channels and properties from SQL, then awaits a separate runtime-status read per version in sequence. A large catalog or a runtime-status outage can delay or prevent otherwise available design metadata from loading. The authoring client’s version interface omits the availability field and its cards render static metadata. Consumers that use availability still need a supported path to it. [SQL read](../../../Backend.Repositories/TaskDefinitionRepository.cs#L67-L74) · [Status enrichment](../../../Backend.API/Services/TaskCatalogQueryService.cs#L24-L45) · [Cosmos query](../../../Backend.Repositories/PluginTaskVersionRuntimeStatusRepository.cs#L65-L87) · [Client type](../../../UI/src/app/interfaces/ITaskDefinition.ts#L10-L17) · [Builder cards](../../../UI/src/app/pages/blueprint-editor-page.component.html#L518-L542)

**Change and verify.** Give static discovery and runtime availability separate failure and freshness policies. Batch, materialize or cache availability for the requested versions. Keep the existing public response compatible or version it explicitly. Measure storage calls and elapsed time for 100 versions. Simulate status-store failure and verify an author can still inspect a task, with availability shown as unknown where relevant.
