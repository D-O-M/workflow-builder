# Plugin Development

For a working example with source, build commands, registration and a predictable output, see the [Plugin authoring guide](../how-to/plugin-authoring.md).

## What a Plugin Is

A plugin is the packaged executable implementation behind a `TaskDefinition` version.

At runtime:

- SQL stores the task-definition and task-version metadata
- blob storage stores the uploaded bundle ZIP
- the engine resolves the task version and launches the plugin through the harness
- the plugin hydrates runtime state via identifiers in the invocation envelope
- plugin prerequisites are declared as engine capabilities in the generated manifest

Collection Iterator is not a plugin. It is a native engine operator and should be handled through the engine/frontend node model rather than plugin bundle registration.

## Where Plugin Code Lives

- production plugins: `Backend.Plugins/*`
- integration-test plugins: `IntegrationTests/Plugins/*`
- harness/runtime support:
  - `Backend.Plugins.Harness`
  - `Backend.Plugins.Communication`
- capability contracts: `Backend.Engine.Capabilities`
- shared domain config contracts: `Backend.Plugins.Configuration/*`

## Capabilities and Configuration

Plugins declare prerequisites with `.RequireCapability<TCapability>()`.

Important rules:

- do not add or reintroduce `.RequireConfiguration<TOptions>()`
- plugin-specific config models should not be defined inside plugin projects
- capability types must live under `Backend.Engine.Capabilities`
- capability-owned config contracts must live under `Backend.Plugins.Configuration.<Domain>`
- shared config keys use the canonical `{Domain}:{OptionsWithoutOptionsSuffix}:{PropertyPath}` shape

Capabilities may own config by using `[RequiresConfiguration(typeof(SomeOptions))]`. The generator writes both:

- `capabilities.requirements[]`: primary prerequisite declarations
- `configuration.requirements[]`: flattened config metadata derived from the declared capabilities

The harness registers capability-owned config types in DI and supports constructor injection as `TOptions` or `IOptions<TOptions>`. Plugin runtime processes still receive repeated `--config <file>` arguments; the harness loads those files into normal .NET configuration.

Engine behavior:

- validates capabilities before starting a plugin runtime agent
- checks required software, configured endpoint reachability, and required config/secret presence
- marks versions with missing required capabilities as unrunnable
- groups versions by task definition, major and minor, then routes each lane to its newest runnable patch
- exposes remediation metadata but does not install missing software in v1

Secrets must stay out of logs, argv, API responses, and manifest examples.

## Harness and Communication Contracts

The engine sends `InvocationEnvelope` JSON lines to plugin stdin. Plugin stdout is reserved for the versioned process protocol in `Backend.Plugins.Communication`: every stdout line must be a framed JSON message with `protocolVersion`, `kind`, optional `jobId`, and `payload`.

Current stdout frame kinds are `ready`, `result`, `log`, `error`, and `heartbeat`. Normal plugin execution failures are still `result` frames with `succeeded=false`; `error` frames are for harness/protocol failures. The harness captures plugin `Console.Out` and converts it to `log` frames so accidental writes do not corrupt result parsing. Stderr remains on the existing Engine stderr logging path.

Important implications:

- the plugin bundle must contain the current harness and communication assemblies
- when the envelope or hydration behavior changes, old bundles can become incompatible even if plugin source code is unchanged
- if the harness or communication contracts change, rebuild canonical bundles before relying on seeded or uploaded plugins
- process-step telemetry context is carried in the envelope so plugin logs, spans, and standard HTTP/runtime metrics flow to the same OTLP collector as the engine without plugin-specific setup

## Plugin ABI Compatibility

Plugin bundles must declare explicit ABI metadata in `manifest.json` under `abi`. ABI v2.0 covers the invocation envelope schema, result schema, hydration protocol, process stdout protocol version, harness ABI stamp, supported state-store modes, and supported property kinds. Plugin-global-state metadata and hydration are not part of ABI v2.0; plugins exchange mutable data only through explicit input and output DTOs.

Newly ingested bundles without ABI metadata or with unsupported `processProtocolVersion` metadata are rejected. Existing SQL task versions keep legacy process protocol `0` metadata, so the Engine refuses to route them until they are rebuilt and re-ingested with ABI v2.0 / process protocol v1. The Engine checks SQL ABI metadata during plugin reload and refuses to route incompatible task versions into the runtime catalog.

The canonical bundle script copies `manifest.abi` into `bundle.meta.json` and fails if generated contract output omits ABI metadata or process protocol metadata. Rebuild plugin bundles after changing `Backend.Plugins.Communication`, `Backend.Plugins.Harness`, hydration behavior, state-store assumptions, process protocol behavior, supported property kinds, or any input/output DTO contract.

## Canonical Bundle Build Path

The canonical bundle script is:

- `BuildScripts/bundle-plugins.ps1`

It is responsible for:

- refreshing `manifest.json` and schema artifacts from the generated design-time contract output
- publishing plugin projects
- reading emitted manifest and schema artifacts
- applying the repo’s canonical versioning behavior
- publishing framework-dependent apphost bundles by default so Engine-hosted plugins stay compact
- writing canonical ZIPs to `Backend.Plugins/_bundles`

Do not hand-roll ZIPs or manually repackage plugins.

## Canonical Bundles

- canonical output path: `Backend.Plugins/_bundles`
- `_bundles` is ignored by git
- AppHost Plugin seeding reads this directory when enabled, by default in Development. The seeder skips uploads for existing blob names and still calls the registration service.

The bundle script owns version behavior. Agents should not hand-edit ZIP layout or manually bump bundle versions as a substitute for using the script.

## Integration-Test Bundle Helper

The integration helper script is:

- `IntegrationTests/scripts/build-integration-plugin-bundles.ps1`

It delegates to the canonical bundle script but targets the integration-test plugin projects and a temp output directory.

## Seeding and Registration Flow

1. The seeder uploads missing bundles and calls `PluginBundleProcessingService`. Direct uploads to the Plugins container reach the same service through the Plugin Detector's blob trigger. The detector also scans for unregistered bundles at startup.
2. The processing service checks quarantine and catalog registration, extracts new bundles, validates their ABI and manifest, and registers task version metadata in SQL.
3. A Plugin reload message tells the Engine to refresh its catalog. On each Engine, an ABI, capability or runtime health failure can prevent a registered version from serving work.
4. The ingestion API exposes recorded status and bundle quarantine operations under `/api/admin/plugin-ingestions`. This is separate from an Engine instance's health decision about a running Plugin agent. [Diagnose a Plugin](../how-to/plugin-authoring.md#register-and-exercise-the-task)

## When to Rebuild Plugins

Rebuild bundles when any of these change:

- plugin project code
- manifest or schema generation behavior
- harness/runtime bootstrapping
- capability declarations or validators
- typed domain configuration contracts
- communication contracts such as `InvocationEnvelope`
- container names or hydration assumptions used by the harness

If a process is failing inside the first plugin step after a runtime contract change, stale bundles are one of the first things to verify.

## Recent Plugin Runtime Notes

- Invocation envelopes carry hydration identifiers and process-step telemetry context.
- Plugin logs, spans, and standard HTTP/runtime metrics flow to the same OTLP collector as the engine through inherited AppHost configuration.
- Capability-owned configuration is the supported plugin prerequisite pattern.
- Plugins that need software, network access, or config must declare capabilities; capabilities own any shared config requirements.
- Bundle version refreshes for the showcase/runtime plugin set should still be done through the canonical bundle script, not by editing project versions or ZIP contents by hand.
