using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Backend.Topology;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Net;

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);
        var repositoryRoot = GetRepositoryRootPath();
        var observabilityRoot = GetObservabilityRootPath(repositoryRoot);
        var integrationTestsEnabled = builder.Configuration.GetValue("IntegrationTests:Enabled", false);
        var nftEnabled = builder.Configuration.GetValue("Nft:Enabled", false);
        var nftMinimalServices = builder.Configuration.GetValue("Nft:MinimalServices", nftEnabled);
        var authEnabled = builder.Configuration.GetValue(
            "Authentication:Enabled",
            builder.Environment.IsDevelopment() && !integrationTestsEnabled && !nftEnabled);
        var keycloakEnabled = builder.Configuration.GetValue(
            "Authentication:Keycloak:Enabled",
            authEnabled && builder.Environment.IsDevelopment() && !nftEnabled);
        var keycloakRealm = GetConfigurationValueOrDefault(
            builder.Configuration,
            "Authentication:Keycloak:Realm",
            "workflow-builder");
        var keycloakAudience = GetConfigurationValueOrDefault(
            builder.Configuration,
            "Authentication:Keycloak:Audience",
            "workflow-builder-api");
        var keycloakSpaClientId = GetConfigurationValueOrDefault(
            builder.Configuration,
            "Authentication:Keycloak:SpaClientId",
            "workflow-builder-spa");
        var keycloakScope = GetConfigurationValueOrDefault(
            builder.Configuration,
            "Authentication:Keycloak:Scope",
            "openid profile email");
        var blueprintSeederClientId = GetConfigurationValueOrDefault(
            builder.Configuration,
            "BlueprintSeeding:Authentication:ClientId",
            "workflow-builder-blueprint-seeder");
        var blueprintSeederScope = GetConfigurationValueOrDefault(
            builder.Configuration,
            "BlueprintSeeding:Authentication:Scope",
            "profile email");
        var keycloakAuthority = GetConfigurationValueOrDefault(
            builder.Configuration,
            "Authentication:Keycloak:Authority",
            $"https://localhost:8080/realms/{keycloakRealm}");
        var keycloakRequireHttpsMetadata = builder.Configuration.GetValue(
            "Authentication:Keycloak:RequireHttpsMetadata",
            !builder.Environment.IsDevelopment());
        var synchronousEngineProjectionUpdatesEnabled = builder.Configuration.GetValue(
            "Projections:SynchronousEngineUpdatesEnabled",
            !nftEnabled);
        var frontendEnabled = builder.Configuration.GetValue("Frontend:Enabled", true);
        var signalREnabled = builder.Configuration.GetValue("SignalR:Enabled", !nftMinimalServices || frontendEnabled);
        var dispatcherEnabled = builder.Configuration.GetValue("Dispatcher:Enabled", true);
        var projectionWorkerEnabled = builder.Configuration.GetValue("ProjectionWorker:Enabled", !nftMinimalServices || nftEnabled);
        var maintenanceEnabled = builder.Configuration.GetValue("Maintenance:Enabled", !nftMinimalServices);
        var pluginDetectorEnabled = builder.Configuration.GetValue("PluginDetector:Enabled", !nftMinimalServices);
        var progressBroadcasterEnabled = builder.Configuration.GetValue(
            "ProgressBroadcaster:Enabled",
            signalREnabled && !nftMinimalServices);
        if (progressBroadcasterEnabled && !signalREnabled)
        {
            throw new InvalidOperationException("ProgressBroadcaster requires SignalR. Enable SignalR or disable ProgressBroadcaster.");
        }

        if (frontendEnabled && !signalREnabled)
        {
            throw new InvalidOperationException("Frontend requires SignalR. Enable SignalR or disable Frontend.");
        }

        var pluginRuntimeMaxConcurrentResultFinalizations = builder.Configuration.GetValue(
            "Nft:PluginRuntime:MaxConcurrentResultFinalizations",
            nftEnabled ? Environment.ProcessorCount * 4 : Environment.ProcessorCount * 2);
        var pluginRuntimeResultFinalizationQueueCapacity = builder.Configuration.GetValue(
            "Nft:PluginRuntime:ResultFinalizationQueueCapacity",
            1024);
        var pluginRuntimeResultFinalizationQueueFullMode = builder.Configuration.GetValue(
            "Nft:PluginRuntime:ResultFinalizationQueueFullMode",
            "Wait");
        var processStepQueueMaxConcurrentMessages = builder.Configuration.GetValue(
            "Nft:ProcessStepQueue:MaxConcurrentMessages",
            nftEnabled ? 32 : 8);
        var processStepQueuePrefetchCount = builder.Configuration.GetValue(
            "Nft:ProcessStepQueue:PrefetchCount",
            processStepQueueMaxConcurrentMessages * 4);
        var processStepQueueMaxAutoLockRenewalMinutes = builder.Configuration.GetValue(
            "Nft:ProcessStepQueue:MaxAutoLockRenewalMinutes",
            5);
        var pluginSeedingBundlesRoot = GetPluginSeedingBundlesRoot(builder.Configuration, repositoryRoot);
        var blueprintSeedingRoot = GetBlueprintSeedingRoot(builder.Configuration, repositoryRoot);

        var cosmosDatabaseName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.Cosmos.DatabaseNameKey,
            WorkflowBuilderTopology.Cosmos.DefaultDatabaseName);
        var (cosmos, cosmosDb) = AddCosmos(
            builder,
            cosmosDatabaseName,
            integrationTestsEnabled);

        var password = builder.AddParameter("SqlServerPassword", secret: true);

        var sql = AddSqlServer(builder, password);
        var sqlDb = sql.AddDatabase(WorkflowBuilderTopology.Sql.TasksDbConnectionName);

        var pluginsBlobResourceName = builder.Configuration.GetValue(
            WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ResourceNameKey,
            WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ResourceName);

        var pluginsBlobContainerName = builder.Configuration.GetValue(
            WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameKey,
            WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerName);

        var artifactsBlobResourceName = builder.Configuration.GetValue(
            WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ResourceNameKey,
            WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ResourceName);

        var artifactsBlobContainerName = builder.Configuration.GetValue(
            WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameKey,
            WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerName);

        var (storage, blobs, pluginsBlobContainer, artifactsBlobContainer) = AddStorage(builder,
            pluginsBlobResourceName,
            pluginsBlobContainerName,
            artifactsBlobResourceName,
            artifactsBlobContainerName);

        var (serviceBus, processStepQueue, pluginControlQueue) = AddServiceBus(builder);
        var keycloakAdminUsername = builder.AddParameter("KeycloakAdminUsername", "admin");
        var keycloakAdminPassword = builder.AddParameter("KeycloakAdminPassword", "admin", secret: true);
        var blueprintSeederClientSecret = builder.AddParameter("BlueprintSeederClientSecret", "developer", secret: true);
        var keycloak = keycloakEnabled
            ? builder.AddKeycloak("keycloak", 8080, keycloakAdminUsername, keycloakAdminPassword)
                .WithRealmImport(Path.Combine(repositoryRoot, "Infra", "Keycloak", "Realms"))
            : null;
        var observability = builder.Configuration.GetValue("Observability:Enabled", true)
            ? AddObservability(
                builder,
                observabilityRoot)
            : ObservabilityResources.Disabled();
        var cosmosResourceName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.Cosmos.ResourceNameKey,
            WorkflowBuilderTopology.Cosmos.DefaultResourceName);
        var cosmosConnectionName =
            TryGetConfigurationValue(builder.Configuration, WorkflowBuilderTopology.Cosmos.ConnectionNameKey)
            ?? WorkflowBuilderTopology.Cosmos.DefaultDatabaseName;
        var processStepQueueConnectionName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameKey,
            WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionName);
        var processStepQueueName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey,
            WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueName);
        var pluginControlQueueConnectionName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameKey,
            WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionName);
        var pluginControlQueueName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameKey,
            WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueName);
        var migrations = builder.AddProject<Projects.Backend_Database_MigrationService>("backend-database-migrations")
            .WithReference(cosmos, connectionName: cosmosConnectionName)
            .WaitFor(cosmosDb)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);
        WaitForObservability(migrations, observability);
        migrations.WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
            .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
            .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName);
        ConfigureTelemetryAndObservability(
            migrations,
            observability);

        var api = builder.AddProject<Projects.Backend_API>(
                "backend-api",
                launchProfileName: "aspire")
            .WithReference(cosmos, connectionName: cosmosConnectionName)
            .WaitFor(cosmosDb)
            .WithReference(blobs)
            .WaitFor(pluginsBlobContainer)
            .WaitFor(artifactsBlobContainer)
            .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
            .WaitFor(serviceBus)
            .WithReference(sqlDb)
            .WaitFor(sqlDb)
            .WaitForCompletion(migrations);
        WaitForObservability(api, observability, includeLogAndMetricBackends: true);
        if (integrationTestsEnabled)
        {
            api.WithHttpEndpoint(
                name: "http",
                env: "ASPNETCORE_HTTP_PORTS");
        }
        else
        {
            api.WithHttpEndpoint(
                    port: 5180,
                    name: "http",
                    env: "ASPNETCORE_HTTP_PORTS")
                .WithExternalHttpEndpoints();
        }

        if (keycloak is not null)
        {
            api.WithReference(keycloak)
                .WaitFor(keycloak);
        }

        api.WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
            .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
            .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
            .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameEnvironmentVariable, processStepQueueConnectionName)
            .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameEnvironmentVariable, processStepQueueName);
        ConfigureAuthentication(
            api,
            authEnabled,
            keycloakEnabled,
            keycloakAuthority,
            keycloakRealm,
            keycloakAudience,
            keycloakSpaClientId,
            keycloakScope,
            keycloakRequireHttpsMetadata);
        ConfigureClientAuthentication(
            api,
            authEnabled,
            keycloakAuthority,
            keycloakAudience,
            keycloakSpaClientId,
            keycloakScope);

        IResourceBuilder<ProjectResource>? signalR = null;
        EndpointReference? signalRHttpEndpoint = null;
        if (signalREnabled)
        {
            signalR = builder.AddProject<Projects.Backend_SignalR>(
                    "backend-signalr",
                    launchProfileName: "aspire")
                .WithHttpEndpoint(
                    name: "http",
                    env: "ASPNETCORE_HTTP_PORTS")
                .WithExternalHttpEndpoints();
            WaitForObservability(signalR, observability);
            ConfigureAuthentication(
                signalR,
                authEnabled,
                keycloakEnabled,
                keycloakAuthority,
                keycloakRealm,
                keycloakAudience,
                keycloakSpaClientId,
                keycloakScope,
                keycloakRequireHttpsMetadata);
            if (keycloak is not null)
            {
                signalR.WithReference(keycloak)
                    .WaitFor(keycloak);
            }

            signalRHttpEndpoint = signalR.GetEndpoint("http");
        }

        api.WithEnvironment(WorkflowBuilderTopology.ClientConfig.ApiBaseUrlEnvironmentVariable, api.GetEndpoint("http"));
        if (signalRHttpEndpoint is not null)
        {
            api.WithEnvironment(WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlEnvironmentVariable, signalRHttpEndpoint);
        }
        else
        {
            api.WithEnvironment(WorkflowBuilderTopology.ClientConfig.SignalRBaseUrlEnvironmentVariable, "http://127.0.0.1:9");
        }

        if (observability.Enabled)
        {
            api.WithEnvironment(WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlEnvironmentVariable, observability.GrafanaHttpEndpoint!);
        }
        else
        {
            api.WithEnvironment(WorkflowBuilderTopology.ClientConfig.GrafanaBaseUrlEnvironmentVariable, "http://127.0.0.1:9");
        }

        ConfigureTelemetryAndObservability(
            api,
            observability);
        if (signalR is not null)
        {
            ConfigureTelemetryAndObservability(
                signalR,
                observability);
        }

        var engine = builder.AddProject<Projects.Backend_Engine>(
                "backend-engine",
                launchProfileName: "aspire")
            .WithHttpEndpoint(
                name: "http",
                env: "ASPNETCORE_HTTP_PORTS")
            .WithReference(cosmos, connectionName: cosmosConnectionName)
            .WaitFor(cosmosDb)
            .WithReference(blobs)
            .WaitFor(pluginsBlobContainer)
            .WaitFor(artifactsBlobContainer)
            .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
            .WithReference(pluginControlQueue, connectionName: pluginControlQueueConnectionName)
            .WaitFor(serviceBus)
            .WithReference(sqlDb)
            .WaitFor(sqlDb)
            .WaitForCompletion(migrations);
        WaitForObservability(engine, observability);
        engine.WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
            .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
            .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
            .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameEnvironmentVariable, pluginsBlobContainerName)
            .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Artifacts.ContainerNameEnvironmentVariable, artifactsBlobContainerName)
            .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameEnvironmentVariable, processStepQueueConnectionName)
            .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameEnvironmentVariable, processStepQueueName)
            .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameEnvironmentVariable, pluginControlQueueConnectionName)
            .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameEnvironmentVariable, pluginControlQueueName)
            .WithEnvironment(
                "PluginRuntime__MaxConcurrentResultFinalizations",
                pluginRuntimeMaxConcurrentResultFinalizations.ToString())
            .WithEnvironment(
                "PluginRuntime__ResultFinalizationQueueCapacity",
                pluginRuntimeResultFinalizationQueueCapacity.ToString())
            .WithEnvironment(
                "PluginRuntime__ResultFinalizationQueueFullMode",
                pluginRuntimeResultFinalizationQueueFullMode)
            .WithEnvironment(
                "ProcessStepQueue__MaxConcurrentMessages",
                processStepQueueMaxConcurrentMessages.ToString())
            .WithEnvironment(
                "ProcessStepQueue__PrefetchCount",
                processStepQueuePrefetchCount.ToString())
            .WithEnvironment(
                "ProcessStepQueue__MaxAutoLockRenewalMinutes",
                processStepQueueMaxAutoLockRenewalMinutes.ToString())
            .WithEnvironment(
                "Projections__SynchronousEngineUpdatesEnabled",
                synchronousEngineProjectionUpdatesEnabled.ToString())
            .WithEnvironment("WorkflowBuilder__RepositoryRoot", repositoryRoot)
            .WithEnvironment("WORKFLOW_BUILDER_DEMO_REPOSITORY", repositoryRoot);
        if (integrationTestsEnabled)
        {
            engine.WithEnvironment("PluginRuntime__Diagnostics", "true")
                .WithEnvironment("PluginRuntime__HydrationTimeoutSeconds", "15");
        }
        ConfigureTelemetryAndObservability(
            engine,
            observability);

        if (dispatcherEnabled)
        {
            var dispatcher = builder.AddProject<Projects.Backend_Dispatcher>(
                    "backend-dispatcher")
                .WithReference(cosmos, connectionName: cosmosConnectionName)
                .WaitFor(cosmosDb)
                .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
                .WaitFor(serviceBus)
                .WaitForCompletion(migrations)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
                .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameEnvironmentVariable, processStepQueueConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameEnvironmentVariable, processStepQueueName);
            WaitForObservability(dispatcher, observability);
            ConfigureTelemetryAndObservability(
                dispatcher,
                observability);
        }

        if (projectionWorkerEnabled)
        {
            var projectionWorker = builder.AddProject<Projects.Backend_ProjectionWorker>(
                    "backend-projection-worker")
                .WithReference(cosmos, connectionName: cosmosConnectionName)
                .WaitFor(cosmosDb)
                .WithReference(sqlDb)
                .WaitFor(sqlDb)
                .WaitForCompletion(migrations)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName);
            WaitForObservability(projectionWorker, observability);
            ConfigureTelemetryAndObservability(
                projectionWorker,
                observability);
        }

        if (maintenanceEnabled)
        {
            var maintenance = builder.AddProject<Projects.Backend_Maintenance>(
                    "backend-maintenance")
                .WithReference(cosmos, connectionName: cosmosConnectionName)
                .WaitFor(cosmosDb)
                .WithReference(processStepQueue, connectionName: processStepQueueConnectionName)
                .WaitFor(serviceBus)
                .WithReference(sqlDb)
                .WaitFor(sqlDb)
                .WaitForCompletion(migrations)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
                .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ConnectionNameEnvironmentVariable, processStepQueueConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameEnvironmentVariable, processStepQueueName);
            WaitForObservability(maintenance, observability);
            ConfigureTelemetryAndObservability(
                maintenance,
                observability);
        }

        /*builder.AddProject<Projects.Backend_PluginLoader>("plugin-loader")
            .WithReference(blobs)
            .WaitFor(blobContainer)
            .WithReference(processStepQueue)
            .WaitFor(serviceBus)
            .WithReference(sqlDb)
            .WaitFor(sqlDb);*/

        if (pluginDetectorEnabled)
        {
            var pluginDetector = builder.AddAzureFunctionsProject<Projects.Backend_Functions_PluginDetector>("plugin-detector")
                .WithHostStorage(storage)
                .WaitFor(pluginsBlobContainer)
                .WithReference(cosmos, connectionName: cosmosConnectionName)
                .WaitFor(cosmosDb)
                .WithReference(serviceBus, connectionName: WorkflowBuilderTopology.ServiceBus.SharedConnectionName)
                .WithReference(pluginControlQueue, connectionName: pluginControlQueueConnectionName)
                .WaitFor(serviceBus)
                .WithReference(sqlDb)
                .WaitFor(sqlDb)
                .WaitForCompletion(migrations)
                ;
            WaitForObservability(pluginDetector, observability);
            pluginDetector.WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameEnvironmentVariable, pluginsBlobContainerName)
                .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.LegacyAliasKey!, pluginsBlobContainerName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
                .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameEnvironmentVariable, pluginControlQueueConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameEnvironmentVariable, pluginControlQueueName);
            ConfigureTelemetryAndObservability(
                pluginDetector,
                observability);
        }

        if (progressBroadcasterEnabled && signalR is not null && signalRHttpEndpoint is not null)
        {
            var progressBroadcaster = builder.AddAzureFunctionsProject<Projects.Backend_Functions_ProgressBroadcaster>("progress-broadcaster")
                .WithHostStorage(storage)
                .WithReference(cosmos, connectionName: cosmosConnectionName)
                .WithReference(cosmos, connectionName: WorkflowBuilderTopology.Realtime.RealtimeCosmosConnectionName)
                .WaitFor(cosmosDb)
                .WithReference(sqlDb)
                .WaitFor(sqlDb)
                .WaitForCompletion(migrations)
                .WaitFor(signalR)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
                .WithEnvironment(WorkflowBuilderTopology.Realtime.SignalRBaseUrlEnvironmentVariable, signalRHttpEndpoint)
                .WithEnvironment(WorkflowBuilderTopology.Realtime.CosmosDatabaseNameEnvironmentVariable, cosmosDatabaseName)
                .WithEnvironment(WorkflowBuilderTopology.Realtime.ProcessesContainerNameEnvironmentVariable, WorkflowBuilderTopology.Cosmos.Containers.Processes.Name)
                .WithEnvironment(WorkflowBuilderTopology.Realtime.ProcessStepsContainerNameEnvironmentVariable, WorkflowBuilderTopology.Cosmos.Containers.ProcessSteps.Name)
                .WithEnvironment(WorkflowBuilderTopology.Realtime.ProcessRealtimeSummariesContainerNameEnvironmentVariable, WorkflowBuilderTopology.Cosmos.Containers.ProcessRealtimeSummaries.Name)
                .WithEnvironment(WorkflowBuilderTopology.Realtime.ProcessNodeRealtimeContainerNameEnvironmentVariable, WorkflowBuilderTopology.Cosmos.Containers.ProcessNodeRealtime.Name)
                .WithEnvironment(WorkflowBuilderTopology.Realtime.LeaseContainerNameEnvironmentVariable, WorkflowBuilderTopology.Cosmos.Containers.ProcessStepProgressLeases.Name);
            WaitForObservability(progressBroadcaster, observability);
            if (integrationTestsEnabled)
            {
                progressBroadcaster.WithEnvironment(WorkflowBuilderTopology.Realtime.UseHostedChangeFeedEnvironmentVariable, "true");
            }

            ConfigureTelemetryAndObservability(
                progressBroadcaster,
                observability);
        }

        IResourceBuilder<ProjectResource>? pluginSeeder = null;
        if (GetPluginSeedingEnabled(builder))
        {
            pluginSeeder = builder.AddProject<Projects.Backend_PluginSeeder>("backend-plugin-seeder")
                .WithReference(blobs)
                .WaitFor(pluginsBlobContainer)
                .WithReference(cosmos, connectionName: cosmosConnectionName)
                .WaitFor(cosmosDb)
                .WithReference(pluginControlQueue, connectionName: pluginControlQueueConnectionName)
                .WaitFor(serviceBus)
                .WithReference(sqlDb)
                .WaitFor(sqlDb)
                .WaitFor(api)
                .WaitFor(engine)
                .WaitForCompletion(migrations)
                .WithEnvironment("PluginSeeding__Enabled", "true")
                .WithEnvironment("PluginSeeding__BundlesRoot", pluginSeedingBundlesRoot)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ConnectionNameEnvironmentVariable, cosmosConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.ResourceNameEnvironmentVariable, cosmosResourceName)
                .WithEnvironment(WorkflowBuilderTopology.Cosmos.DatabaseNameEnvironmentVariable, cosmosDatabaseName)
                .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ConnectionNameEnvironmentVariable, pluginControlQueueConnectionName)
                .WithEnvironment(WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameEnvironmentVariable, pluginControlQueueName)
                .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.LegacyAliasKey!, pluginsBlobContainerName)
                .WithEnvironment(WorkflowBuilderTopology.Storage.BlobContainers.Plugins.ContainerNameEnvironmentVariable, pluginsBlobContainerName);
            if (signalR is not null)
            {
                pluginSeeder.WaitFor(signalR);
            }

            WaitForObservability(pluginSeeder, observability);
        }

        if (GetBlueprintSeedingEnabled(builder))
        {
            var blueprintSeeder = builder.AddProject<Projects.Backend_BlueprintSeeder>("backend-blueprint-seeder")
                .WaitFor(api)
                .WaitFor(engine)
                .WaitForCompletion(migrations)
                .WithEnvironment("BlueprintSeeding__Enabled", "true")
                .WithEnvironment("BlueprintSeeding__BlueprintsRoot", blueprintSeedingRoot)
                .WithEnvironment("BlueprintSeeding__ApiBaseUrl", api.GetEndpoint("http"))
                .WithEnvironment("BlueprintSeeding__Authentication__Enabled", authEnabled.ToString())
                .WithEnvironment("BlueprintSeeding__Authentication__Authority", authEnabled ? keycloakAuthority : string.Empty)
                .WithEnvironment("BlueprintSeeding__Authentication__ClientId", blueprintSeederClientId)
                .WithEnvironment("BlueprintSeeding__Authentication__ClientSecret", blueprintSeederClientSecret)
                .WithEnvironment("BlueprintSeeding__Authentication__Scope", blueprintSeederScope);
            if (signalR is not null)
            {
                blueprintSeeder.WaitFor(signalR);
            }

            if (authEnabled && keycloak is not null)
            {
                blueprintSeeder.WaitFor(keycloak);
            }

            WaitForObservability(blueprintSeeder, observability);

            if (pluginSeeder is not null)
            {
                blueprintSeeder.WaitForCompletion(pluginSeeder);
            }
        }

        if (frontendEnabled)
        {
            AddFrontend(builder, api, signalR!, repositoryRoot);
        }

        builder.Build().Run();
    }

    static ObservabilityResources
        AddObservability(
            IDistributedApplicationBuilder builder,
            string observabilityRoot)
    {
        var alloyConfigPath = Path.Combine(observabilityRoot, "alloy", "config.alloy");
        var lokiConfigPath = Path.Combine(observabilityRoot, "loki", "config.yml");
        var tempoConfigPath = Path.Combine(observabilityRoot, "tempo", "config.yml");
        var prometheusConfigPath = Path.Combine(observabilityRoot, "prometheus", "prometheus.yml");
        var grafanaProvisioningPath = Path.Combine(observabilityRoot, "grafana", "provisioning");

        var aspireOtlpEndpoint = TryGetConfigurationValue(
            builder.Configuration,
            "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL") is { } dashboardOtlpEndpoint
                ? TranslateAspireOtlpEndpointForDocker(dashboardOtlpEndpoint)
                : "host.docker.internal:18889";

        var loki = builder.AddContainer("loki", "grafana/loki:latest")
            .WithArgs("-config.file=/etc/loki/config.yml")
            .WithBindMount(lokiConfigPath, "/etc/loki/config.yml", isReadOnly: true)
            .WithHttpEndpoint(
                targetPort: 3100,
                port: null,
                name: "http",
                isProxied: false);

        var tempo = builder.AddContainer("tempo", "grafana/tempo:latest")
            .WithArgs("-config.file=/etc/tempo/config.yml")
            .WithBindMount(tempoConfigPath, "/etc/tempo/config.yml", isReadOnly: true)
            .WithHttpEndpoint(
                targetPort: 3200,
                port: null,
                name: "http",
                isProxied: false);

        var prometheus = builder.AddContainer("prometheus", "prom/prometheus:latest")
            .WithArgs(
                "--config.file=/etc/prometheus/prometheus.yml",
                "--storage.tsdb.path=/prometheus",
                "--web.enable-otlp-receiver")
            .WithBindMount(prometheusConfigPath, "/etc/prometheus/prometheus.yml", isReadOnly: true)
            .WithHttpEndpoint(
                targetPort: 9090,
                port: null,
                name: "http",
                isProxied: false);

        var alloy = builder.AddContainer("grafana-alloy", "grafana/alloy:latest")
            .WithArgs(
                "run",
                "/etc/alloy/config.alloy",
                "--storage.path=/var/lib/alloy/data",
                "--server.http.listen-addr=0.0.0.0:12345")
            .WithBindMount(alloyConfigPath, "/etc/alloy/config.alloy", isReadOnly: true)
            .WithEnvironment("OBSERVABILITY__ASPIRE_OTLP_ENDPOINT", aspireOtlpEndpoint)
            .WaitFor(loki)
            .WaitFor(tempo)
            .WaitFor(prometheus)
            .WithEndpoint(
                targetPort: 4317,
                port: null,
                scheme: "http",
                name: "otlp-grpc",
                isProxied: false)
            .WithEndpoint(
                targetPort: 4318,
                port: null,
                scheme: "http",
                name: "otlp-http",
                isProxied: false);

        var grafana = builder.AddContainer("grafana", "grafana/grafana-oss:latest")
            .WithEnvironment("GF_AUTH_ANONYMOUS_ENABLED", "true")
            .WithEnvironment("GF_AUTH_ANONYMOUS_ORG_ROLE", "Admin")
            .WithEnvironment("GF_AUTH_DISABLE_LOGIN_FORM", "true")
            .WithEnvironment("GF_SECURITY_ALLOW_EMBEDDING", "true")
            .WithBindMount(grafanaProvisioningPath, "/etc/grafana/provisioning", isReadOnly: true)
            .WaitFor(loki)
            .WaitFor(tempo)
            .WaitFor(prometheus)
            .WithHttpEndpoint(
                targetPort: 3000,
                port: null,
                name: "http",
                isProxied: false);

        return new ObservabilityResources(
            Enabled: true,
            Alloy: alloy,
            Grafana: grafana,
            Loki: loki,
            Tempo: tempo,
            Prometheus: prometheus);
    }

    static (
        IResourceBuilder<AzureCosmosDBResource> account,
        IResourceBuilder<AzureCosmosDBDatabaseResource> database)
        AddCosmos(
        IDistributedApplicationBuilder builder,
        string cosmosDatabaseName,
        bool integrationTestsEnabled)
    {
        var cosmosResourceName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.Cosmos.ResourceNameKey,
            WorkflowBuilderTopology.Cosmos.DefaultResourceName);
        var persistCosmosData = builder.Configuration.GetValue("AzureCosmos:PersistData", false);

#pragma warning disable ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var cosmos = builder.AddAzureCosmosDB(cosmosResourceName)
            .RunAsPreviewEmulator(emulator =>
            {
                if (integrationTestsEnabled)
                {
                    emulator.WithGatewayPort(null);
                }
                else
                {
                    emulator.WithDataExplorer(1234);
                    emulator.WithGatewayPort(8081);
                }

                emulator.WithLifetime(
                    persistCosmosData
                        ? ContainerLifetime.Persistent
                        : ContainerLifetime.Session);

                if (persistCosmosData)
                {
                    emulator.WithDataVolume();
                }
            });
#pragma warning restore ASPIRECOSMOSDB001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        var database = cosmos.AddCosmosDatabase(cosmosDatabaseName);
        foreach (var container in WorkflowBuilderTopology.CreateCatalog().Cosmos.Containers)
        {
            database.AddContainer(container.Name, container.PartitionKey);
        }

        return (cosmos, database);
    }

    static IResourceBuilder<SqlServerServerResource> AddSqlServer(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ParameterResource> password)
    {
        var persistSqlData = builder.Configuration.GetValue("SqlServer:PersistData", false);

        var sqlServer = builder.AddSqlServer("sqlserver", password)
            .WithLifetime(
                persistSqlData
                    ? ContainerLifetime.Persistent
                    : ContainerLifetime.Session)
            .WithEnvironment("ACCEPT_EULA", "Y")
            .WithEnvironment("MSSQL_SA_PASSWORD", password);

        if (persistSqlData)
        {
            sqlServer.WithDataVolume();
        }

        return sqlServer;
    }
    //.WithEndpoint(port: 1433, name: "sqlserver-tcp", targetPort: 1433);

    static (
        IResourceBuilder<AzureStorageResource> storage,
        IResourceBuilder<AzureBlobStorageResource> blobStorage,
        IResourceBuilder<AzureBlobStorageContainerResource> pluginsBlobContainer,
        IResourceBuilder<AzureBlobStorageContainerResource> artifactsBlobContainer)
        AddStorage(IDistributedApplicationBuilder builder,
            string pluginsBlobResourceName,
            string pluginsBlobContainerName,
            string artifactsBlobResourceName,
            string artifactsBlobContainerName)
    {
        var azureStorage = builder.AddAzureStorage(
            builder.Configuration.GetValue(
                WorkflowBuilderTopology.Storage.ResourceNameKey,
                WorkflowBuilderTopology.Storage.DefaultResourceName));
        var persistStorageData = builder.Configuration.GetValue("AzureStorage:PersistData", false);

        if (builder.Environment.IsDevelopment())
        {
            azureStorage.RunAsEmulator(config =>
                config.WithLifetime(
                    persistStorageData
                        ? ContainerLifetime.Persistent
                        : ContainerLifetime.Session));
        }

        var blobs = azureStorage.AddBlobs(WorkflowBuilderTopology.Storage.BlobConnectionName);

        var pluginsBlobContainer = azureStorage.AddBlobContainer(
            pluginsBlobResourceName,
            pluginsBlobContainerName);

        var artifactsBlobContainer = azureStorage.AddBlobContainer(
            artifactsBlobResourceName,
            artifactsBlobContainerName);

        return (azureStorage, blobs, pluginsBlobContainer, artifactsBlobContainer);
    }

    static (IResourceBuilder<AzureServiceBusResource> serviceBus,
        IResourceBuilder<AzureServiceBusQueueResource> processStepQueue,
        IResourceBuilder<AzureServiceBusQueueResource> pluginControlQueue)
        AddServiceBus(IDistributedApplicationBuilder builder)
    {
        var serviceBusResourceName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.ResourceNameKey,
            WorkflowBuilderTopology.ServiceBus.DefaultResourceName);
        var processStepQueueResourceName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ResourceNameKey,
            WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.ResourceName);
        var processStepQueueName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueNameKey,
            WorkflowBuilderTopology.ServiceBus.Queues.ProcessStep.QueueName);
        var pluginControlQueueResourceName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ResourceNameKey,
            WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.ResourceName);
        var pluginControlQueueName = GetConfigurationValueOrDefault(
            builder.Configuration,
            WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueNameKey,
            WorkflowBuilderTopology.ServiceBus.Queues.PluginControl.QueueName);

        var serviceBus = builder.AddAzureServiceBus(serviceBusResourceName);

        if (builder.Environment.IsDevelopment())
        {
            serviceBus.RunAsEmulator();
        }

        var processStepQueue = serviceBus.AddServiceBusQueue(
            processStepQueueResourceName,
            processStepQueueName);
        var pluginControlQueue = serviceBus.AddServiceBusQueue(
            pluginControlQueueResourceName,
            pluginControlQueueName);

        return (serviceBus, processStepQueue, pluginControlQueue);
    }

    static string GetRequiredConfigurationValue(
        IConfiguration configuration,
        string key) =>
        configuration.GetValue<string>(key)
        ?? throw new InvalidOperationException($"Configuration value '{key}' is not configured.");

    static string GetConfigurationValueOrDefault(
        IConfiguration configuration,
        string key,
        string defaultValue) =>
        configuration.GetValue<string>(key) ?? defaultValue;

    static string? TryGetConfigurationValue(IConfiguration configuration, string key) =>
        configuration.GetValue<string>(key);

    static IResourceBuilder<T> ConfigureAuthentication<T>(
        IResourceBuilder<T> resourceBuilder,
        bool authEnabled,
        bool keycloakEnabled,
        string keycloakAuthority,
        string keycloakRealm,
        string keycloakAudience,
        string keycloakSpaClientId,
        string keycloakScope,
        bool requireHttpsMetadata)
        where T : IResourceWithEnvironment
    {
        return resourceBuilder
            .WithEnvironment("Authentication__Enabled", authEnabled.ToString())
            .WithEnvironment("Authentication__Keycloak__Enabled", keycloakEnabled.ToString())
            .WithEnvironment("Authentication__Keycloak__Authority", keycloakAuthority)
            .WithEnvironment("Authentication__Keycloak__Realm", keycloakRealm)
            .WithEnvironment("Authentication__Keycloak__Audience", keycloakAudience)
            .WithEnvironment("Authentication__Keycloak__SpaClientId", keycloakSpaClientId)
            .WithEnvironment("Authentication__Keycloak__Scope", keycloakScope)
            .WithEnvironment("Authentication__Keycloak__RequireHttpsMetadata", requireHttpsMetadata.ToString());
    }

    static IResourceBuilder<T> ConfigureClientAuthentication<T>(
        IResourceBuilder<T> resourceBuilder,
        bool authEnabled,
        string keycloakAuthority,
        string keycloakAudience,
        string keycloakSpaClientId,
        string keycloakScope)
        where T : IResourceWithEnvironment
    {
        return resourceBuilder
            .WithEnvironment("ClientConfig__Authentication__Enabled", authEnabled.ToString())
            .WithEnvironment("ClientConfig__Authentication__Authority", authEnabled ? keycloakAuthority : string.Empty)
            .WithEnvironment("ClientConfig__Authentication__ClientId", authEnabled ? keycloakSpaClientId : string.Empty)
            .WithEnvironment("ClientConfig__Authentication__Scope", authEnabled ? keycloakScope : string.Empty)
            .WithEnvironment("ClientConfig__Authentication__Audience", authEnabled ? keycloakAudience : string.Empty);
    }

    static void WaitForObservability<T>(
        IResourceBuilder<T> resourceBuilder,
        ObservabilityResources observability,
        bool includeLogAndMetricBackends = false)
        where T : IResourceWithWaitSupport
    {
        if (!observability.Enabled)
        {
            return;
        }

        resourceBuilder.WaitFor(observability.Alloy!);

        if (includeLogAndMetricBackends)
        {
            resourceBuilder
                .WaitFor(observability.Loki!)
                .WaitFor(observability.Prometheus!);
        }
    }

    static void ConfigureTelemetryAndObservability<T>(
        IResourceBuilder<T> resourceBuilder,
        ObservabilityResources observability)
        where T : IResourceWithEnvironment
    {
        if (!observability.Enabled)
        {
            resourceBuilder
                .WithEnvironment("OTEL_SDK_DISABLED", "true")
                .WithEnvironment("Observability__GrafanaBaseUrl", string.Empty)
                .WithEnvironment("Observability__LokiBaseUrl", "http://127.0.0.1:9")
                .WithEnvironment("Observability__TempoBaseUrl", "http://127.0.0.1:9")
                .WithEnvironment("Observability__PrometheusBaseUrl", "http://127.0.0.1:9");
            return;
        }

        resourceBuilder
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", observability.AlloyOtlpGrpcEndpoint!)
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc")
            .WithEnvironment("Observability__GrafanaBaseUrl", observability.GrafanaHttpEndpoint!)
            .WithEnvironment("Observability__LokiBaseUrl", observability.LokiHttpEndpoint!)
            .WithEnvironment("Observability__TempoBaseUrl", observability.TempoHttpEndpoint!)
            .WithEnvironment("Observability__PrometheusBaseUrl", observability.PrometheusHttpEndpoint!);
    }

    sealed record ObservabilityResources(
        bool Enabled,
        IResourceBuilder<ContainerResource>? Alloy,
        IResourceBuilder<ContainerResource>? Grafana,
        IResourceBuilder<ContainerResource>? Loki,
        IResourceBuilder<ContainerResource>? Tempo,
        IResourceBuilder<ContainerResource>? Prometheus)
    {
        public EndpointReference? AlloyOtlpGrpcEndpoint => Alloy?.GetEndpoint("otlp-grpc");
        public EndpointReference? GrafanaHttpEndpoint => Grafana?.GetEndpoint("http");
        public EndpointReference? LokiHttpEndpoint => Loki?.GetEndpoint("http");
        public EndpointReference? TempoHttpEndpoint => Tempo?.GetEndpoint("http");
        public EndpointReference? PrometheusHttpEndpoint => Prometheus?.GetEndpoint("http");

        public static ObservabilityResources Disabled() =>
            new(
                Enabled: false,
                Alloy: null,
                Grafana: null,
                Loki: null,
                Tempo: null,
                Prometheus: null);
    }

    static string GetObservabilityRootPath(string repositoryRoot) =>
        Path.Combine(repositoryRoot, "Infra", "Observability");

    static bool GetPluginSeedingEnabled(IDistributedApplicationBuilder builder) =>
        builder.Environment.IsDevelopment()
            ? builder.Configuration.GetValue("PluginSeeding:Enabled", true)
            : builder.Configuration.GetValue("PluginSeeding:Enabled", false);

    static bool GetBlueprintSeedingEnabled(IDistributedApplicationBuilder builder) =>
        builder.Environment.IsDevelopment()
            ? builder.Configuration.GetValue("BlueprintSeeding:Enabled", true)
            : builder.Configuration.GetValue("BlueprintSeeding:Enabled", false);

    static string GetPluginSeedingBundlesRoot(IConfiguration configuration, string repositoryRoot)
    {
        var configuredRoot = configuration.GetValue<string>("PluginSeeding:BundlesRoot");
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(repositoryRoot, "Backend.Plugins", "_bundles");
        }

        return Path.GetFullPath(
            Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(repositoryRoot, configuredRoot));
    }

    static string GetBlueprintSeedingRoot(IConfiguration configuration, string repositoryRoot)
    {
        var configuredRoot = configuration.GetValue<string>("BlueprintSeeding:BlueprintsRoot");
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(repositoryRoot, "SeedData", "Blueprints");
        }

        return Path.GetFullPath(
            Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(repositoryRoot, configuredRoot));
    }

    static string GetRepositoryRootPath()
    {
        foreach (var candidateRoot in GetCandidateRoots())
        {
            if (Directory.Exists(Path.Combine(candidateRoot, "Backend.AppHost")) &&
                Directory.Exists(Path.Combine(candidateRoot, "Infra")))
            {
                return candidateRoot;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from '{Environment.CurrentDirectory}' or '{AppContext.BaseDirectory}'.");
    }

    static IEnumerable<string> GetCandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(startPath) || !Directory.Exists(startPath))
            {
                continue;
            }

            for (var current = new DirectoryInfo(startPath); current is not null; current = current.Parent)
            {
                if (seen.Add(current.FullName))
                {
                    yield return current.FullName;
                }
            }
        }
    }

    static string TranslateAspireOtlpEndpointForDocker(string endpointUrl)
    {
        var uri = new Uri(endpointUrl, UriKind.Absolute);
        var host = uri.Host switch
        {
            "localhost" => "host.docker.internal",
            "127.0.0.1" => "host.docker.internal",
            "::1" => "host.docker.internal",
            _ => uri.Host
        };

        if (!string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = Dns.GetHostAddresses(host);
            }
            catch
            {
                // Docker Desktop exposes host.docker.internal; if unavailable the exporter
                // will fail fast at runtime rather than breaking AppHost startup.
            }
        }

        return $"{host}:{uri.Port}";
    }

    static void AddFrontend(
        IDistributedApplicationBuilder builder,
        IResourceBuilder<ProjectResource> api,
        IResourceBuilder<ProjectResource> signalR,
        string repositoryRoot)
    {
        var uiDirectoryPath = Path.Combine(repositoryRoot, "UI");

        var frontend = builder.AddDockerfile("frontend", uiDirectoryPath)
            .WithHttpEndpoint(
                targetPort: 4200,
                port: 4200,
                name: "frontend-http",
                isProxied: false);

        MountUiRootFilesAndTopDirs(frontend, uiDirectoryPath, "/app");

        frontend.WithReference(api)
            .WithEnvironment("WORKFLOW_BUILDER_API_PROXY_TARGET", api.GetEndpoint("http"))
            .WithEnvironment("WORKFLOW_BUILDER_SIGNALR_PROXY_TARGET", signalR.GetEndpoint("http"))
            .WaitFor(api);
    }

    static void MountUiRootFilesAndTopDirs(
            IResourceBuilder<ContainerResource> container,
            string sourceRoot,
            string targetRoot = "/app")
    {
        // Combine top-level files and directories into a single list
        var entries = Directory.GetFileSystemEntries(sourceRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(static entry =>
            {
                var name = Path.GetFileName(entry).ToLowerInvariant();
                var exclusions = new string[] { "package-lock.json", "node_modules", "dockerfile" };

                // Exclude node_modules and hidden entries
                if (name.StartsWith("."))
                    return false;

                if (exclusions.Any(e => e == name))
                    return false;

                // Exclude by extension or exact name
                var extension = Path.GetExtension(entry);
                return !extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                       && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
            });

        // Mount each entry (file or directory)
        foreach (var entry in entries)
        {
            var relativeName = Path.GetFileName(entry);
            var targetPath = Path.Combine(targetRoot, relativeName).Replace("\\", "/");

            container.WithBindMount(entry, targetPath, isReadOnly: false);
        }
    }
}
