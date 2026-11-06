# Start and inspect a Process through the HTTP API

This example starts the latest saved version of **First score check**. Create that Blueprint once using the tutorial. The API uses a numeric Blueprint definition ID, a GUID for each Process, and the node's GUID for the Step detail route below. [First Process tutorial](../tutorials/first-process.md)

## Connect as a local developer

Use PowerShell 7. The normal local AppHost exposes **backend-api** on `http://localhost:5180`. For another environment, use its API URL. In Development the API serves the generated schema at `/openapi/v1.json` and the interactive Scalar reference at `/scalar/v1`.

```powershell
$api = 'http://localhost:5180'
$config = Invoke-RestMethod "$api/api/client-config"
$auth = @{}
if ($config.authentication.enabled) {
    $token = Read-Host 'Local API access token' -AsSecureString
    $auth = @{ Authentication = 'Bearer'; Token = $token }
    if ($api -eq 'http://localhost:5180') {
        $auth.AllowUnencryptedAuthentication = $true
    }
}
```

For the local sample, sign into the UI first. In the browser's Network tools, select an authenticated request to `/api/blueprints` and copy the access token from its `Authorization` header into the prompt, omitting the `Bearer ` prefix. This is the access token for the API audience. When local authentication is disabled, the script skips the prompt. The SPA uses authorization code with PKCE. Provisioned seeder and broadcaster identities serve their respective internal jobs. [Keycloak configuration and authentication scope](../authentication-keycloak.md)

## Start the Blueprint

```powershell
$blueprints = Invoke-RestMethod "$api/api/blueprints" @auth
$blueprint = $blueprints | Where-Object name -eq 'First score check' | Select-Object -First 1
if (!$blueprint) { throw 'Create First score check using the tutorial first.' }
$blueprintId = $blueprint.id
$body = @{ initialTaskInput = @{ Name = 'Ada'; Score = 7; Speed = 6 } } | ConvertTo-Json -Depth 3
$accepted = Invoke-RestMethod "$api/api/blueprints/$blueprintId/processes" @auth -Method Post -ContentType 'application/json' -Body $body
$accepted
```

`202 Accepted` returns `processId`, `blueprintId` and `acceptedAt`. Save `processId` to track that instance. Each new start request creates a new Process. Optional `nodeOverrides` are keyed by Blueprint node GUID, with a property/value object for each node.

## Read the result

```powershell
$process = Invoke-RestMethod "$api/api/processes/$($accepted.processId)" @auth
$process | Select-Object blueprintName, blueprintVersionNumber, status, processId
$stepUrl = "$api/api/blueprints/$blueprintId/processes/$($accepted.processId)/steps/$($process.startingBlueprintNodeId)"
$step = Invoke-RestMethod $stepUrl @auth
$step | Select-Object status, inputs, outputs, failureMessage | ConvertTo-Json -Depth 8
```

Repeat the read while the Process is in progress. A completed Step returns `outputs.Success.Health` of 14 and `outputs.Success.Alive` of `true`. For `400` on start, compare the input property names and JSON types with the starting task. For `401`, obtain a fresh access token. For `404`, check that the definition, Process and node IDs came from this environment.

Visual Studio users can send the same requests from the sample HTTP file. Its bearer headers read `WORKFLOW_BUILDER_ACCESS_TOKEN` from the editor process environment. On an explicitly no-auth local stack, omit those headers. [HTTP requests](../../Backend.API/Backend.API.http) · [Visual Studio environment-variable syntax](https://learn.microsoft.com/en-us/aspnet/core/test/http-files#environment-variables)
