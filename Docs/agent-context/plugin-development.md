# Plugin Development

## What a Plugin Is

A plugin is the packaged executable implementation behind a `TaskDefinition` version.

At runtime:

- SQL stores the task-definition and task-version metadata
- blob storage stores the uploaded bundle ZIP
- the engine resolves the task version and launches the plugin through the harness
- the plugin hydrates runtime state via identifiers in the invocation envelope

## Where Plugin Code Lives

- production plugins: `Backend.Plugins/*`
- integration-test plugins: `IntegrationTests/Plugins/*`
- harness/runtime support:
  - `Backend.Plugins.Harness`
  - `Backend.Plugins.Communication`

## Harness and Communication Contracts

The engine and plugin executable communicate through `InvocationEnvelope` in `Backend.Plugins.Communication`.

Important implications:

- the plugin bundle must contain the current harness and communication assemblies
- when the envelope or hydration behavior changes, old bundles can become incompatible even if plugin source code is unchanged
- if the harness or communication contracts change, rebuild canonical bundles before relying on seeded or uploaded plugins

## Canonical Bundle Build Path

The canonical bundle script is:

- `BuildScripts/bundle-plugins.ps1`

It is responsible for:

- publishing plugin projects
- reading emitted manifest and schema artifacts
- applying the repo’s canonical versioning behavior
- writing canonical ZIPs to `Backend.Plugins/_bundles`

Do not hand-roll ZIPs or manually repackage plugins.

## Canonical Bundles

- canonical output path: `Backend.Plugins/_bundles`
- `_bundles` is ignored by git
- AppHost plugin seeding uses this output on a wiped startup when plugin seeding is enabled

The bundle script owns version behavior. Agents should not hand-edit ZIP layout or manually bump bundle versions as a substitute for using the script.

## Integration-Test Bundle Helper

The integration helper script is:

- `IntegrationTests/scripts/build-integration-plugin-bundles.ps1`

It delegates to the canonical bundle script but targets the integration-test plugin projects and a temp output directory.

## Seeding and Registration Flow

1. Canonical bundle ZIPs are uploaded or seeded into the plugins blob container.
2. `PluginDetector` extracts the bundle and parses:
   - `manifest.json`
   - schema files
3. `PluginBundleProcessingService` registers the task version metadata in SQL.
4. A plugin reload message is emitted so the engine refreshes its runtime catalog.

## When to Rebuild Plugins

Rebuild bundles when any of these change:

- plugin project code
- manifest or schema generation behavior
- harness/runtime bootstrapping
- communication contracts such as `InvocationEnvelope`
- container names or hydration assumptions used by the harness

If a process is failing inside the first plugin step after a runtime contract change, stale bundles are one of the first things to verify.
