# Connect two phases with a Data Bridge

A Phase Series gives each phase its own child Process. A Data Bridge carries selected output properties from a completed phase into the next phase. A correlation mapping lets a later trigger identify the waiting parent. This example uses the same one-node Blueprint twice so you can check the handoff by arithmetic. [Create the First score check Blueprint](../tutorials/first-process.md)

## Map an output and save the series

1. In **Phases**, choose **New phase series**. In **Phase setup**, set **Series name** to `Two score checks`. Use **Add phase** twice to select the same saved version of **First score check**. Each occurrence has its own phase ID.
2. Expand the **Success** output on the source node and the **PlayerInput** input on the destination node. Drag the source **Health** property to the **Data Bridge** divider. From the staged property on the bridge, drag to **Score** on the destination node. Both are integers.
3. Reopen **Phase setup**, choose **Add correlation**, select **Score** for **Result**, and enter `Speed` for **Trigger**. The handoff result takes the destination property name. The trigger will use a separate numeric field to identify the waiting parent, so you can see which value actually reaches **Score**.
4. Choose **Create**. On subsequent edits **Save Version** creates a new version of the series. The Phases UI authors two to four phases. The API also accepts a one-phase definition.

## Start the first child and trigger the second

Use the PowerShell connection setup in the API guide to set `$api` and `$auth`. You can also select **Two score checks** under **Start Process** and choose **Start Phase Series**. The UI opens the parent overview in Process Explorer. The API returns both the parent and child IDs. [Connect to the API](process-api.md#connect-as-a-local-developer)

```powershell
$definitions = Invoke-RestMethod "$api/api/phased-blueprints" @auth
$series = $definitions | Where-Object name -eq 'Two score checks' | Select-Object -First 1
if (!$series) { throw 'Create Two score checks in Phases first.' }
$seriesUrl = "$api/api/phased-blueprints/$($series.id)"
$startBody = @{ initialTaskInput = @{ Name = 'Ada'; Score = 7; Speed = 6 } } | ConvertTo-Json
$first = Invoke-RestMethod "$seriesUrl/processes" @auth -Method Post -ContentType 'application/json' -Body $startBody
$parentUrl = "$seriesUrl/processes/$($first.phasedProcessId)"
$parent = Invoke-RestMethod $parentUrl @auth
$parent | Select-Object status, phases | ConvertTo-Json -Depth 3
```

Wait until a fresh GET of `$parentUrl` shows `WaitingForTrigger`. The first child has finished with `Health = 14` and `Alive = true`. The second phase has index **1** in the API. Send its trigger with `Speed = 14` to match the waiting parent and `Score = 1` to make the handoff visible. Use the series version returned on start.

To find the same start in the UI, open **Processes → Phase Series**. Search by series name or full parent ID and choose **Waiting for trigger**. Open the parent and expand **Data Bridge** between the two phases. The recorded value for the mapping from **Health** to **Score** should already be `14`. The overview refreshes while you wait and its URL can be shared.

```powershell
$triggerBody = @{
    phasedBlueprintVersionId = $first.phasedBlueprintVersionId
    triggerInput = @{ Name = 'Ada'; Score = 1; Speed = 14 }
} | ConvertTo-Json
$second = Invoke-RestMethod "$seriesUrl/phases/1/triggers" @auth -Method Post -ContentType 'application/json' -Body $triggerBody
$second | Select-Object phasedProcessId, childProcessId, phaseIndex
Invoke-RestMethod $parentUrl @auth | Select-Object status, phases | ConvertTo-Json -Depth 3
```

The response should identify the original parent and a second child. Refresh the parent until it shows `Completed`. Open the second child in Process Explorer or read its Step using the API guide. **Inputs** should show `PlayerInput.Score = 14` and `PlayerInput.Speed = 14`. The bridge replaced the trigger's score of 1 with the first child's health. **Outputs** should show `Success.Health = 28` and `Success.Alive = true`.

If the trigger returns `404`, check that the previous phase completed, the result has been registered, and the trigger uses the original version and the exact correlation value and JSON type. A missing correlation property gives `400`. For another independent attempt on the same version, choose a different initial score and use its doubled value for correlation. A repeated correlation key can identify the earlier parent.

For API clients, `GET /api/phased-processes` lists starts newest first. Supply `q` for a name or full parent ID, `status` for a parent status, and the returned `continuationToken` for the next page. Use `GET $parentUrl/overview` for phase status and child IDs and `GET $parentUrl/handoffs/{sourcePhaseId}` for the recorded values of one Data Bridge.

Each handoff mapping reports whether a value was captured in `hasValue`. When true, `valueJson` contains the JSON literal as text, preserving the precision of recorded numbers. A recorded JSON null has `hasValue: true` and `valueJson: "null"`. Historical values whose destination is unknown appear in `unattributedValues`.

## Inspect the seeded CI/CD example

The **CI/CD Phase Series Demo** maps the build ID and artifact locations into the deployment phase and correlates on `BuildId`. Its build, publish and email Plugins require their respective tools, storage and email configuration. Inspect the saved definition to see the four handoffs without running the build. [Series definition](../../SeedData/Blueprints/cicd-phase-series-demo.phase-series.json) · [Plugin capabilities and configuration](../agent-context/plugin-development.md#capabilities-and-configuration)
