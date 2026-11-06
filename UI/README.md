# WorkflowBuilder UI

The Angular 21 app provides Dashboard, Blueprint Builder, Phases, Start Process and Process Explorer. Use Node.js 24 and the repository's lockfile. [Frontend map](../Docs/agent-context/frontend-map.md)

## Run against the local AppHost

Start the backend from the repository root with `dotnet run --project Backend.AppHost --launch-profile http`. This profile leaves the frontend to a separate terminal. For a new catalog, prepare the sample Plugin before starting AppHost. [First Process tutorial](../Docs/tutorials/first-process.md)

In a second PowerShell terminal at the repository root, use the API's **http** endpoint from Aspire. The normal local profile uses port 5180. Read the SignalR URL from that API's client config because Aspire can assign the hub a different port each session.

```powershell
$env:WORKFLOW_BUILDER_API_PROXY_TARGET = 'http://localhost:5180'
$config = Invoke-RestMethod "$env:WORKFLOW_BUILDER_API_PROXY_TARGET/api/client-config"
$env:WORKFLOW_BUILDER_SIGNALR_PROXY_TARGET = $config.signalRBaseUrl
Set-Location UI
npm ci
npm start
```

Open [the local UI](http://localhost:4200). With the development realm enabled, sign in as `developer` with password `developer`. The browser obtains the API and SignalR addresses at startup. If sign-in works but the dashboard fails to load, first check the response from `/api/client-config` and the **backend-api** resource in Aspire. [Local authentication](../Docs/authentication-keycloak.md)

## Build and test

Run from `UI` after `npm ci`.

| Check | Command | Requirements |
| --- | --- | --- |
| Unit tests (Vitest) | `npm run test:unit` | Runs the repository unit suite |
| Application build | `npm run build` | Compiles the production bundle |
| Mocked Playwright suite | `npm run test:e2e:mocked` | Install browsers once with `npx playwright install chromium firefox webkit` |
| Runtime Playwright smoke | `npm run test:e2e:runtime` | Running AppHost, API access and the seeded **Showcase Airport Disruption Early Warning** Blueprint |

The runtime smoke assumes direct access to the application and has no interactive sign-in step. Use a dedicated local no-auth test stack and the proxy settings above for that lane. Keep the normal development stack authenticated when testing the login flow. [Playwright configuration](playwright.config.ts) · [Runtime smoke scenario](e2e/specs/runtime-smoke.spec.ts) · [Developer workflows](../Docs/agent-context/developer-workflows.md)
