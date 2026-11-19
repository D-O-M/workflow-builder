# Process execution, recovery and reporting

[Audit overview and priorities](README.md) · [Engineering structure](engineering-practice.md)

## R1 · High · A claimed Step can outlive the only worker able to finish it

The repository claims a Step by changing `Pending` to `InProgress`. A second claim gets the same negative result for an active Step and a terminal one. On the Plugin path the Engine interprets that result as `AlreadySettled` and completes the broker message. If the first Engine exited after claiming and before writing a finalization record, redelivery can consume the last opportunity to run or reconcile that Step. Maintenance scans existing finalization records, so it has no such record to resume. [Repository claim](../../../Backend.Repositories/ProcessStepRepository.cs#L153-L171) · [Pending predicate](../../../Backend.Repositories/ProcessStepRepository.cs#L204-L211) · [Broker settlement](../../../Backend.Engine/StepExecutionService.cs#L118-L123) · [Checkpoint recovery](../../../Backend.Maintenance/StepFinalizationRecoveryService.cs#L46-L63)

**Change and verify.** Distinguish live claim, terminal outcome and expired or unknown owner in the claim result. Persist an attempt identity and fence late completions. With a counting Plugin, kill the Engine after claim and again after the external effect but before checkpointing. Redeliver to another Engine. Require one observed effect after safe recovery, or an explicit uncertain outcome for an operator to reconcile. Include a legacy ownerless record and a late result from the first worker. Drain older consumers or deploy compatible settlement logic before enabling takeover.

## R2 · High · A Step can inherit another Step’s resolved Plugin version

The execution cache returns the same graph instance for a Blueprint version. Each Step context writes its selected task version onto a node in that graph, including when restoring the version from an earlier claim. Agent-slot acquisition, invocation and release read that mutable value at different times. Two Processes executing the same node across a patch change can therefore invoke, report or release a different version from the one originally acquired. [Graph cache](../../../Backend.Repositories/BlueprintExecutionCache.cs#L21-L33) · [Runtime selection](../../../Backend.Engine/StepExecutionContext.cs#L69-L95) · [Claim restoration](../../../Backend.Engine/StepExecutionContext.cs#L191-L200) · [Slot use](../../../Backend.Engine/StepExecutionService.cs#L73-L75) · [Slot release](../../../Backend.Engine/StepExecutionService.cs#L281-L287)

**Reproduced.** In the compiled Engine, two distinct Step contexts shared one executable Blueprint node. Context A selected task version 101 (1.0.1). After B selected 102 (1.0.2), A also reported 102 (1.0.2). This probe observed the mutation at the context boundary.

**Change and verify.** Keep the cached graph read-only and capture the resolution on the Step attempt. Use it for dispatch, provenance, checkpoint replay and slot release. Interleave two contexts that share a node, give them different eligible patches, then assert the agent, recorded version and released slot for each. Preserve the existing major/minor patch-selection rules and the exact versions in persisted checkpoints.

## R3 · High · A retried phase trigger can acknowledge a child that was never started

The trigger writes a reserved child ID and marks the parent phase in progress before calling `StartVersionAsync`. An existing child ID is enough for its early success return. A crash between those operations leaves a reservation. A failed start can also leave the ID on a failed phase. A repeated trigger can then receive an acceptance response containing a child ID with no corresponding Process. [Existing-child response and reservation](../../../Backend.API/Services/PhasedBlueprintProcessService.cs#L185-L224) · [Start and failure path](../../../Backend.API/Services/PhasedBlueprintProcessService.cs#L239-L263) · [Failed phase update](../../../Backend.API/Services/PhasedBlueprintProcessService.cs#L437-L464)

**Change and verify.** Make reservation and confirmed acceptance distinct durable states. Reconcile a reservation using its original ID, with idempotent child creation and explicit handling of validation failures. Interrupt immediately before and after child persistence, then send the same trigger twice concurrently. Every accepted replay should resolve to the same retrievable child.

## R4 · High · Indexed search advances past matching Processes it never returns

`SearchProcessesFromIndexAsync` asks for twice the requested limit, with a minimum fetch of ten. It saves the index continuation for the entire fetched page, then stops iterating as soon as the response is full. Matching rows at the end of that page fall between cursors. With four matches in a final index page and an API limit of two, the service can return two and say the search is complete. A business owner paging through affected customers would undercount them. [API fetch](../../../Backend.API/Services/ProcessQueryService.cs#L375-L409) · [Response and cursor](../../../Backend.API/Services/ProcessQueryService.cs#L438-L471) · [Index consumption](../../../Backend.Repositories/Processes/ProcessSummaryProjectionQueryEngine.cs#L138-L158)

**Reproduced.** The compiled search service used a real SQLite Blueprint and the existing tenant-index paging stub. At limit two, following every cursor returned 2 of 4 matches and 4 of 14 matches. All rows matched, with zero fallback or Step hydration.

**Change and verify.** Have one layer own unconsumed candidates. Fetch only the remaining capacity and continue on rejected candidates, or retain the unread buffer in the API continuation. Follow every cursor with limits of one and two, empty intermediate pages, tied timestamps and selective Step filters. Compare the ordered IDs with a single complete search.

## R5 · High · A final Step transition can be published from an older projection or lost on delivery

Two independent paths need separate tests. In asynchronous projection mode, the worker and broadcaster observe source changes independently. The broadcaster reduces the change to node IDs. The projection reader accepts an existing node projection without comparing its revision with the change. If the final event arrives before the worker replaces an in-progress projection, the previous state can be the last state pushed. [Change grouping](../../../Backend.Functions.ProgressBroadcaster/Services/ProcessProgressBroadcaster.cs#L111-L132) · [Worker feed](../../../Backend.ProjectionWorker/ProcessProjectionChangeFeedWorker.cs#L53-L60) · [Projection fallback conditions](../../../Backend.Repositories/Processes/ProcessProjectionService.cs#L367-L401)

Separately, failures to obtain subscriptions or publish a batch can be logged and turned into normal returns. The upstream callback can finish successfully after a failed delivery. A Process with no further changes may leave an already connected operator looking at its old state. [Gateway error handling](../../../Backend.Functions.ProgressBroadcaster/Services/SignalRProcessGateway.cs#L56-L92) · [Broadcaster error handling](../../../Backend.Functions.ProgressBroadcaster/Services/ProcessProgressBroadcaster.cs#L94-L108)

**Change and verify.** Publish after projection commit or carry a source revision and read through to that revision. Give a single layer responsibility for bounded retries, observable failures and safe duplicate delivery. First pause the projector after `InProgress`, complete the source Step and resume the projector. Then return 503 on publish followed by 200 with no later source writes. The same connected Explorer should converge on the durable terminal state in both cases.

## R6 · Medium · Iterator search and detail answer different questions about the same node

Search groups runtime Steps by Blueprint node and keeps only the newest Step for property and status filters. Detail can aggregate all iterations and show a node as failed if any iteration failed. A newer successful item can hide an earlier failed customer from search while detail still shows the failure. [Search predicates and grouping](../../../Backend.API/Services/ProcessQueryService.cs#L970-L1008) · [Runtime Step detail](../../../Backend.Repositories/Processes/ProcessProjectionService.cs#L141-L150) · [Aggregate node status](../../../Backend.Repositories/Processes/ProcessProjectionService.cs#L999-L1010)

**Change and verify.** Specify whether each filter applies to the node aggregate or to one occurrence. Keep related property predicates together on the same occurrence. Use an older failed item A and a newer successful item B in indexed and fallback searches. A search for failed A should lead to A’s occurrence, and filters should never combine A’s status with B’s data.

## R7 · High under load · Start reconciliation can repeatedly skip the same older Process

The reconciler inspects a bounded set of recent active Processes. Its repository query takes the newest rows on every poll. With the defaults of 50 candidates and a 24-hour lookback, 50 newer active rows can keep an older missing initial Step out of every pass until it ages out. [Reconciliation loop](../../../Backend.Maintenance/ProcessStartReconciliationService.cs#L55-L71) · [Candidate selection](../../../Backend.Repositories/ProcessInstanceRepository.cs#L183-L205) · [Defaults](../../../Backend.Maintenance/ProcessStartReconciliationOptions.cs#L9-L19)

**Change and verify.** Track unresolved starts directly, or continue bounded scans from a durable position with a policy for older unresolved work. Keep more than a batch of recent healthy Processes active over several polls and prove the older incomplete start receives its deterministic initial Step once.

## R8 · Medium · Dashboard totals can exclude every event for the selected Blueprint

The summary reader limits the global lifecycle-log result to 20,000 raw entries before applying the Blueprint filter and grouping. Other Blueprints’ activity can fill that window. A scoped Dashboard can therefore show zero failures during an interval containing a failure. [Summary computation](../../../Backend.API/Observability/ObservabilityQueryService.cs#L11-L52) · [Raw log reads](../../../Backend.API/Observability/ObservabilityQueryService.cs#L281-L317)

**Change and verify.** Scope and aggregate at the data source where possible, or return explicit truncation and coverage information. Place a known failure behind 20,000 newer unrelated entries. Require either the correct scoped total or a visible indication that the count is partial.

## R9 · Medium · A log timestamp also serves as event identity and continuation

The log reader combines results by timestamp-derived cursor. Explorer also indexes entries by cursor. Two distinct entries with the same timestamp can collapse, and advancing a nanosecond timestamp through .NET tick precision can skip an adjacent entry. This compromises reconstruction of events around a failure. [Loki timestamps](../../../Backend.API/Observability/LokiQueryClient.cs#L157-L167) · [Continuation and merge](../../../Backend.API/Observability/ObservabilityQueryService.cs#L338-L387) · [Explorer merge](../../../UI/src/app/pages/process-explorer-page.component.ts#L605-L615)

**Change and verify.** Separate display time, stable event identity and continuation. Resume across an overlapping boundary and deduplicate only identical events. Page through distinct messages at the same nanosecond across streams, then through two messages one nanosecond apart.

## R10 · Medium when scaling SignalR · The subscription inventory belongs to one host

The hub keeps connection/group memberships in two process-local dictionaries. Its internal snapshot and publish routes resolve a local registry and hub context. The Azure demo caps the service at one replica. Increasing that limit without changing the delivery design can send the broadcaster’s request to a replica other than the one hosting the interested connection. Operational documentation and deployment validation should make that constraint visible. [Subscription state](../../../Backend.SignalR/Services/ProcessSubscriptionRegistry.cs#L5-L13) · [Host wiring](../../../Backend.SignalR/Program.cs#L38-L40) · [Snapshot and publish](../../../Backend.SignalR/Program.cs#L70-L106) · [Replica limit](../../../Infra/Terraform/variables.tf#L225-L228)

**Change and verify.** Enforce the supported single-host deployment until both group delivery and subscription discovery can operate across replicas. To enable scale-out, connect a client to one host, direct the snapshot/publish requests to another and verify delivery and cleanup after disconnect. Repeat during a rolling replacement.
