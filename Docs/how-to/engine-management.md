# Manage the Engine fleet

Open `/management/` on the WorkflowBuilder frontend and sign in with the runtime engineer or administrator role. A save applies to all Engines in this deployment. Expand **Engine status** to see each reporting replica’s revision. **Awaiting an Engine** means the desired revision is saved and there is currently no fresh replica report.

## Settings and credentials

Under **Capability settings**, find the declared key, choose **Edit** or **Set secret**, and save. Ordinary managed values appear in this view. A secret can be replaced, but its value is never returned. **Remove override** restores the deployment-supplied value when one exists. The Engine rechecks the capability and passes the same effective settings to the Plugin. A newer save by another operator causes a conflict. Review the refreshed values before submitting again.

For example, `Dcc:Gateway:EndpointUrl` is a URI and `Dcc:Gateway:ApiKey` is an optional credential. Changes apply to Plugins that declare the corresponding capability. Allow admitted Steps to finish before retiring an old credential in its issuing system.

## Plugin availability

Use **Plugins** to inspect the versions serving each major/minor lane. Disabling an exact patch allows another runnable patch in the same lane to take over. Disabling the whole Plugin covers existing and newly registered versions. Re-enabling the Plugin restores its individual version rules.

Each Engine stops admitting affected Steps as it applies the revision. Admitted Steps finish. When a disabled lane cannot serve a Step, Process Explorer shows the wait. Once eligible, the same Step resumes under its original Process and Step IDs. The Azure recovery job checks every minute, including when all Engine replicas have scaled down.

## API

All routes are under `/api/engine-management` and require a signed-in runtime engineer or admin. Read the current revision and use it for the next mutation.

| Request | Purpose |
| --- | --- |
| `GET /settings`, `GET /plugins`, `GET /fleet` | Inspect declared settings, Plugin policy, and observed application status. |
| `PUT /settings/{key}` | Set an override with a JSON body containing `value` and `expectedRevision`. |
| `DELETE /settings/{key}?expectedRevision=N` | Remove an override. |
| `PUT /plugins/{id}`, `PUT /plugins/{id}/versions/{versionId}` | Set `enabled` with `expectedRevision`. |

Successful writes return the saved revision. A stale revision returns HTTP 409. Secret bodies belong only in the request over a protected connection.

## Deployment

Local AppHost enables management by default with sign-in enabled. Run `npm start` in `UI` and use the usual frontend address with `/management/`. The dev server proxies the second Angular app on an internal port, 4201 by default. Set `WORKFLOW_BUILDER_MANAGEMENT_PORT` to choose another port. API and Engine processes share the same OS user's encrypted development store.

To allow management sign-in on an otherwise anonymous product deployment, set `Authentication:ManagementEnabled=true` on the API and configure the existing `Authentication:Keycloak` authority, audience, and SPA client. Local AppHost sets the management flag when both `EngineManagement:Enabled` and `Authentication:Keycloak:Enabled` are enabled. Only a bearer token for the configured API audience carrying the runtime engineer or administrator role grants management access.

For Azure, apply the additive SQL migration and deploy the management-aware Engine, Dispatcher, and API images before enabling `enable_engine_management` in Terraform. Enable Keycloak and Azure App Configuration. Configure the OIDC client's redirect and post-logout redirect URIs for `/management/auth/callback` and `/management/auth/logout-callback` on the frontend origin. Supply the released Dispatcher image in `dispatcher_image` when provisioning the recovery job. Subsequent releases update the job with the Dispatcher image.

Terraform provisions a dedicated Plugin vault, a write-only secret role for the API identity, read access for the Engine identity, and a scheduled Dispatcher job. Management opens read-only. Check that every running Engine reports its revision and the Dispatcher job completes, then set `enable_engine_management_writes = true`. Verify by changing a non-secret capability setting, comparing desired and reported revisions, and checking a test Plugin through a disable/re-enable cycle. Restore overrides and enable parked work before rolling back to older Engine binaries.

The Azure wake schedule defaults to once per minute in UTC. Set `engine_management_recovery_cron` to change it. To exercise parking and recovery while both Engines and the regular Dispatcher are stopped, run the [two-replica integration pilot](../../IntegrationTests/README.md).
