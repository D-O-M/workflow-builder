using Backend.Plugins.Configuration.CiCd;
using Backend.Plugins.Configuration.Dcc;

namespace Backend.Engine.Capabilities;

[EngineCapability(
    "software:command:git",
    CapabilityKindNames.Software,
    "Git CLI",
    Description = "Requires the git command-line client.",
    Remediation = "Install Git and ensure it is available on PATH.")]
[CommandCapability("git", Arguments = "--version")]
public sealed class GitCliCapability;

[EngineCapability(
    "software:sdk:dotnet",
    CapabilityKindNames.Software,
    ".NET SDK",
    Description = "Requires a .NET SDK capable of building and testing projects.",
    Remediation = "Install the .NET SDK and ensure dotnet is available on PATH.")]
[CommandCapability("dotnet", Arguments = "--list-sdks")]
public sealed class DotNetSdkCapability;

[EngineCapability(
    "software:runtime:node",
    CapabilityKindNames.Software,
    "Node.js",
    Description = "Requires the Node.js runtime.",
    Remediation = "Install Node.js and ensure node is available on PATH.")]
[CommandCapability("node", Arguments = "--version")]
public sealed class NodeJsRuntimeCapability;

[EngineCapability(
    "software:command:npm",
    CapabilityKindNames.Software,
    "npm CLI",
    Description = "Requires the npm command-line client.",
    Remediation = "Install npm and ensure it is available on PATH.")]
[CommandCapability("npm", Arguments = "--version")]
public sealed class NpmCliCapability;

[EngineCapability(
    "software:command:powershell",
    CapabilityKindNames.Software,
    "PowerShell",
    Description = "Requires PowerShell for plugin bundle scripts.",
    Remediation = "Install PowerShell and ensure pwsh is available on PATH.")]
[CommandCapability("pwsh", Arguments = "-Version")]
public sealed class PowerShellCapability;

[EngineCapability(
    "configuration:cicd:phase-trigger-email",
    CapabilityKindNames.Configuration,
    "CI/CD phase trigger email configuration",
    Description = "Requires the CI/CD approval email configuration used by the phase trigger email plugin.")]
[RequiresConfiguration(typeof(PhaseTriggerEmailOptions))]
public sealed class CiCdPhaseTriggerEmailCapability;

[EngineCapability(
    "network:dcc:gateway",
    CapabilityKindNames.Network,
    "DCC gateway access",
    Description = "Requires DCC gateway configuration and validates the configured endpoint when one is supplied.")]
[RequiresConfiguration(typeof(GatewayOptions))]
[HttpEndpointCapability("Dcc:Gateway:EndpointUrl")]
public sealed class DccGatewayCapability;

[EngineCapability(
    "configuration:cicd:artifact-storage",
    CapabilityKindNames.Configuration,
    "CI/CD artifact storage",
    Description = "Requires engine-managed artifact blob storage.")]
public sealed class CiCdArtifactStorageCapability;

[EngineCapability(
    "configuration:dcc:xml-blob-storage",
    CapabilityKindNames.Configuration,
    "DCC XML blob storage",
    Description = "Requires engine-managed XML blob storage for DCC payload handoff.")]
public sealed class DccXmlBlobStorageCapability;
