# Create a Blueprint and inspect its first Process

Build a one-Step Blueprint that doubles a player's score and checks their speed. With a score of 7 and a speed of 6, you can predict the result before launching it. The `TestPlugin` project supplies this task and uses only its input. [Sample Plugin implementation](../../Backend.Plugins/TestPlugin/MyPlugin.cs)

## Prepare the local catalog

If the **Blueprints** Plugin library already lists **A Test Plugin**, go straight to the next section. For a fresh Windows checkout, start Docker with Linux containers and install the .NET 10 SDK, Azure Functions Core Tools v4, PowerShell 7 and Node.js 24. Run the commands below from the repository root.

On first setup, give the AppHost its required SQL Server password. This PowerShell command generates one and saves it in the AppHost's local .NET user secrets. If you already use a SQL data volume or have configured this parameter, keep its existing password.

```powershell
@{ Parameters = @{ SqlServerPassword = 'Wb7!' + [guid]::NewGuid().ToString('N') } } |
    ConvertTo-Json | dotnet user-secrets set --project Backend.AppHost
```

On a new Windows development machine, run `dotnet dev-certs https --trust` and accept the local trust prompt so the browser can sign into the HTTPS Keycloak realm. Build just the sample Plugin for the local Engine. On another platform, use the Engine's runtime identifier in place of `win-x64`.

```powershell
pwsh -File BuildScripts/bundle-plugins.ps1 -RootDir Backend.Plugins -ProjectFilter TestPlugin.csproj -OutputDir Backend.Plugins/_bundles -RuntimeIdentifier win-x64
dotnet run --project Backend.AppHost --launch-profile http -- --BlueprintSeeding:Enabled=false
```

This launch leaves Plugin seeding enabled and skips the optional showcase Blueprint imports, which require additional Plugins. In the Aspire dashboard, look for a completed **backend-plugin-seeder** and a running **backend-engine**. In a second terminal, start the UI and sign in with the local development account. [UI startup](../../UI/README.md) · [Local sign-in details](../authentication-keycloak.md)

## Save a one-Step Blueprint

1. Open **Blueprints**. Choose **Create new blueprint** (the plus button).
2. Use **Edit draft blueprint name**, enter `First score check`, and press Enter.
3. Open **Toggle plugin library** and search for `A Test Plugin`. Click the task card once to add a node. The single node is the starting node.
4. Choose **Save blueprint**. Open **Start Process** and search for **First score check**. Finding it there confirms the Blueprint was saved.

## Start and inspect the Process

1. Select **First score check** and fill in **Name** with `Ada`, **Score** with `7`, and **Speed** with `6`.
2. Choose **Continue to Runtime Overrides**, leave the overrides empty, and choose **Start Process**.
3. Process Explorer opens the new Process. Keep the URL if you want to return to this instance. When the node is complete, click its body and open **Outputs**. The `Success` channel should contain `Health = 14` and `Alive = true`.
4. Open **Inputs** to check the values that this Step received. If the Step failed, inspect **Logs** and use its Process ID to correlate the failure with the Engine logs in Aspire.

You can now start the same Blueprint through the API or use its output as the input to another phase. [Start a Process through the API](../how-to/process-api.md) · [Connect two phases with a Data Bridge](../how-to/phase-series.md)
