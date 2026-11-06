# Keycloak Authentication

WorkflowBuilder uses Keycloak to sign in local development users. Backend.API and Backend.SignalR validate JWT bearer tokens. WorkflowBuilder defines its own identity and authorization policies in [Backend.Security](../Backend.Security/Authentication/WorkflowSecurityServiceCollectionExtensions.cs).

## Local AppHost

Normal AppHost development starts a Keycloak resource on `https://localhost:8080` unless authentication is explicitly disabled or the AppHost is running in integration-test/NFT mode.

The dev realm import lives at:

```text
Infra/Keycloak/Realms/workflow-builder-realm.json
```

The AppHost sets the development admin console credentials to `admin` / `admin`. The imported realm provides the following development identities.

- realm: `workflow-builder`
- SPA client: `workflow-builder-spa`
- API audience/client: `workflow-builder-api`
- blueprint seeder service-account client: `workflow-builder-blueprint-seeder` / `developer`
- progress broadcaster service-account client: `workflow-builder-progress-broadcaster` / `developer`
- dev user: `developer` / `developer`
- dev roles:
  - `workflowbuilder.admin`
  - `workflowbuilder.blueprint_author`
  - `workflowbuilder.process_operator`
  - `workflowbuilder.runtime_engineer`
  - `workflowbuilder.auditor`
  - `workflowbuilder.internal_publisher`

## Configuration

Server-side auth settings:

```json
{
  "Authentication": {
    "Enabled": true,
    "Keycloak": {
      "Enabled": true,
      "Authority": "https://localhost:8080/realms/workflow-builder",
      "Realm": "workflow-builder",
      "Audience": "workflow-builder-api",
      "SpaClientId": "workflow-builder-spa",
      "Scope": "openid profile email",
      "RequireHttpsMetadata": false
    }
  }
}
```

Set `Authentication:Enabled=false` to run the backend and frontend in local no-auth mode. In that mode, API and SignalR use a stable local development actor and the Angular app does not redirect to Keycloak.

## Runtime Client Config

Angular first fetches the same-origin `/runtime-bootstrap.json`, then requests `/api/client-config` from its `apiBaseUrl` (same-origin when empty). Both requests omit credentials. The second response supplies API, SignalR, Grafana and browser authentication settings. The following response illustrates the shape. Use the returned addresses because AppHost can assign different hub and Grafana ports on each launch. [Client bootstrap](../UI/src/app/domain/runtime-client-config.service.ts)

```json
{
  "apiBaseUrl": "http://localhost:5180",
  "signalRBaseUrl": "http://localhost:5000",
  "grafanaBaseUrl": "http://localhost:3000",
  "authentication": {
    "enabled": true,
    "authority": "https://localhost:8080/realms/workflow-builder",
    "clientId": "workflow-builder-spa",
    "scope": "openid profile email",
    "audience": "workflow-builder-api"
  }
}
```

When auth is disabled, `authentication.enabled` is `false` and the remaining auth strings are empty.

## API And SignalR

`Backend.API` validates JWT bearer tokens for normal controllers. `/api/client-config`, Aspire health/default endpoints, and local development OpenAPI/Scalar endpoints remain anonymous. APIs return normal `401`/`403` responses; they do not redirect browser users to Keycloak.

`Backend.SignalR` protects `/hubs/processes` with the authenticated-user policy. The hub accepts a token in the `access_token` query parameter on the hub route. Its Blueprint and Process group methods accept the requested identifier from an authenticated caller. The declared roles describe policies available to code. The product controller filter currently requires an authenticated actor, including on the Plugin ingestion admin routes. [API filter](../Backend.API/Program.cs) · [Subscription checks](../Backend.SignalR/Hubs/ProcessProgressHub.cs)

The same SignalR host serves the broadcaster's subscription snapshot and event-publish endpoints. Access follows this order for both endpoints.

| Condition | Result |
| --- | --- |
| Authentication enabled and bearer token has `workflowbuilder.internal_publisher` or `workflowbuilder.admin` | Accept. |
| Otherwise, a publisher secret is configured | Accept only a matching `X-WorkflowBuilder-Internal-Publisher` header, including when user authentication is enabled. |
| Otherwise, host environment is Development | Accept without a publisher credential. |
| Otherwise | Return HTTP 401. |

The broadcaster HTTP client can send a configured bearer token, shared secret, or both. Terraform supplies a generated shared secret for the demo. [Publisher checks](../Backend.SignalR/Program.cs) · [Broadcaster HTTP client](../Backend.Functions.ProgressBroadcaster/Program.cs) · [Azure demo settings](../Infra/Terraform/locals.tf)

The local AppHost blueprint seeder uses the `workflow-builder-blueprint-seeder` service-account client when authentication is enabled. If seeded imports fail, the seeder exits nonzero so Aspire marks the one-shot resource as failed instead of quietly completing.

## Frontend Flow

Angular uses `oidc-client-ts` with Authorization Code + PKCE:

1. The app loads the bootstrap file and public client configuration.
2. Product routes are guarded.
3. Unauthenticated users redirect to Keycloak.
4. `/auth/callback` completes sign-in and returns to the original route.
5. API requests receive `Authorization: Bearer <access_token>`.
6. SignalR reconnects use a fresh token through `accessTokenFactory`.

## Deployment and access boundaries

The checked-in Azure demo disables user authentication. Its optional Keycloak Container App starts a development server with an admin account. To enable sign-in, provision the realm, API audience, SPA client, roles and users, and configure the SPA redirect URIs and web origins for the deployed frontend. Verify the authority's discovery document, a successful browser callback and an authenticated request to the deployed API. [Azure Keycloak module](../Infra/Terraform/modules/keycloak/main.tf) · [Local realm definition](../Infra/Keycloak/Realms/workflow-builder-realm.json)

The Blueprint seeder implements OAuth client credentials. General external-caller provisioning, resource-specific access controls and Plugin entitlements remain product work. [Seeder token acquisition](../Backend.BlueprintSeeder/BlueprintSeederAccessTokenProvider.cs) · [Stakeholder needs](stakeholder-roles/README.md)
