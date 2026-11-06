# Build a Plugin and make it available to Blueprint authors

Use the existing `TestPlugin` project for a first pass through the toolchain. Its **A Test Plugin** task accepts `PlayerInput` and emits a `Success` channel. The project references the harness. Build files under `Backend.Plugins` supply the contract generator and capability references. [Project](../../Backend.Plugins/TestPlugin/TestPlugin.csproj) · [Plugin registration](../../Backend.Plugins/TestPlugin/Program.cs)

## Define and check the contract

1. For your own task, create an executable .NET 10 project under `Backend.Plugins/<ProjectName>` following the sample project. Give it an explicit three-part project version. Use a distinct project name for the catalog identity and a display name that a Blueprint author can recognize.
2. Define the input/output DTOs with `[PluginJsonSerializable]`. Implement `IPlugin.Execute()`, read the input property and populate the output property. Register them in `Program.cs` with `ConfigureInputChannel` and `ConfigureOutputChannel`. For the sample, `Score = 7` and `Speed = 6` should produce `Health = 14` and `Alive = true`. These values can also be asserted in a plain unit test of the Plugin class. [Input and output types](../../Backend.Plugins/TestPlugin/DTOs.cs) · [Implementation](../../Backend.Plugins/TestPlugin/MyPlugin.cs)
3. If the task requires external software, network access or settings, declare its prerequisite with `.RequireCapability<TCapability>()`. For example, the Git checkout Plugin declares a Git CLI capability. The Engine checks installed capabilities before starting a Plugin agent. [Git checkout registration](../../Backend.Plugins/CiCd.GitCheckout/Program.cs) · [Configuration and ABI reference](../agent-context/plugin-development.md)
4. From the repository root, build and inspect the generated channel names, property kinds, required fields, capabilities and ABI. Replace `TestPlugin` with your project name for a new task.

```powershell
dotnet build Backend.Plugins/TestPlugin/TestPlugin.csproj -c Release
Get-Content Backend.Plugins/TestPlugin/manifest.json -Raw | ConvertFrom-Json
```

## Package for the Engine

```powershell
pwsh -File BuildScripts/bundle-plugins.ps1 -RootDir Backend.Plugins -ProjectFilter TestPlugin.csproj -OutputDir Backend.Plugins/_bundles -Configuration Release -RuntimeIdentifier win-x64
Get-ChildItem Backend.Plugins/_bundles -Filter 'TestPlugin_*.zip'
```

Choose the runtime identifier for the Engine host. The script publishes a framework-dependent executable, stages the generated contract and writes `<ProjectName>_<version>.zip`. With no earlier bundle in that output directory it uses the declared version. Within the same major version it compares the generated contract fingerprint with the latest canonical ZIP. A changed fingerprint advances the minor version and resets the patch to zero. An unchanged fingerprint advances the patch. The script can update the project version and republish. Review these tracked changes as part of the Plugin release. Describe behavioral changes in the release review, especially those that preserve the schema but alter the meaning of an output. [Canonical bundler](../../BuildScripts/bundle-plugins.ps1)

## Register and exercise the task

Start the normal development AppHost with Plugin seeding enabled and the ZIP in `Backend.Plugins/_bundles`. The one-shot seeder uploads missing ZIPs and passes bundles for registration. A direct upload to the configured Plugins blob container is also ingested by the Plugin Detector. Registration stores the task metadata in SQL and signals the Engine to reload. The Engine selects the newest runnable patch within the task's permitted major/minor lane.

In **Blueprints**, search the Plugin library by display name and add the task to a Blueprint. Start a Process and inspect the Step's **Inputs** and **Outputs**. [Worked example](../tutorials/first-process.md)

If the task is missing or unrunnable, query `GET /api/admin/plugin-ingestions?pluginName=TestPlugin` using your local API credentials. Inspect the blob name, status history, quarantine reason, and the Engine capability or ABI failure in the logs. For a bundle in ingestion quarantine, fix the reported cause before using `POST /api/admin/plugin-ingestions/{id}/unquarantine` and reprocessing it. Ingestion quarantine and per-Engine runtime health are tracked separately. [Ingestion endpoints](../../Backend.API/Controllers/PluginIngestionAdminController.cs) · [Connect to the API](process-api.md#connect-as-a-local-developer)
