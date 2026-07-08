# Keycloak Authentication

WorkflowBuilder uses Keycloak for local-development user authentication and JWT bearer validation. Product authorization seams stay in WorkflowBuilder code under `Backend.Security` so blueprint, process, plugin, schedule, M2M, and external-trigger permissions can grow without coupling product rules to the identity provider.

## Local AppHost

Normal AppHost development starts a Keycloak resource on `https://localhost:8080` unless authentication is explicitly disabled or the AppHost is running in integration-test/NFT mode.

The dev realm import lives at:

```text
Infra/Keycloak/Realms/workflow-builder-realm.json
```

It creates:

- realm: `workflow-builder`
- admin console login: `admin` / `admin`
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

The frontend bootstraps by anonymously fetching `/api/client-config`. The response includes API, SignalR, Grafana, and browser auth settings:

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

`Backend.SignalR` protects `/hubs/processes` with bearer auth. The hub accepts `access_token` from the query string only on the hub route, which is the SignalR browser-client pattern. Internal broadcaster endpoints require either an internal-publisher bearer token or a configured internal shared-secret header.

The local AppHost blueprint seeder uses the `workflow-builder-blueprint-seeder` service-account client when authentication is enabled. If seeded imports fail, the seeder exits nonzero so Aspire marks the one-shot resource as failed instead of quietly completing.

## Frontend Flow

Angular uses `oidc-client-ts` with Authorization Code + PKCE:

1. The app loads `/api/client-config`.
2. Product routes are guarded.
3. Unauthenticated users redirect to Keycloak.
4. `/auth/callback` completes sign-in and returns to the original route.
5. API requests receive `Authorization: Bearer <access_token>`.
6. SignalR reconnects use a fresh token through `accessTokenFactory`.

## Known Limits

This pass intentionally does not implement fine-grained blueprint/process/plugin authorization, plugin entitlements, M2M/client-credentials flows, schedule or external-trigger authorization, admin permission UI, ISO controls, compliance evidence, access-review workflows, or audit-retention policy.
