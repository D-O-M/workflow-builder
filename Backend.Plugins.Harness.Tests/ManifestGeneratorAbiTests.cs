using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Backend.PluginAbi;
using Backend.Plugins.Harness;
using Shouldly;

namespace Backend.Plugins.Harness.Tests;

public sealed class ManifestGeneratorAbiTests
{
    [Test]
    public void EmitAll_writes_current_plugin_abi_metadata()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "workflow-builder-manifest-abi-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            var manifest = ManifestGenerator.CreateManifest(
                "ABI manifest",
                new KeyValuePair<string, Type>("Input", typeof(AbiInput)),
                [new KeyValuePair<string, Type>("Success", typeof(AbiOutput))],
                globalStateType: null);

            var result = ManifestGenerator.EmitAll(
                outputDirectory,
                manifest,
                [typeof(AbiInput), typeof(AbiOutput)]);

            var manifestJson = File.ReadAllText(result.manifestPath);
            var root = JsonNode.Parse(manifestJson)!.AsObject();
            var abi = root["abi"]!.AsObject();

            abi["version"]!["major"]!.GetValue<int>().ShouldBe(PluginAbiDefaults.CurrentMajor);
            abi["version"]!["minor"]!.GetValue<int>().ShouldBe(PluginAbiDefaults.CurrentMinor);
            abi["invocationEnvelopeSchemaVersion"]!.GetValue<int>().ShouldBe(PluginAbiDefaults.CurrentInvocationEnvelopeSchemaVersion);
            abi["resultSchemaVersion"]!.GetValue<int>().ShouldBe(PluginAbiDefaults.CurrentResultSchemaVersion);
            abi["hydrationProtocolVersion"]!.GetValue<int>().ShouldBe(PluginAbiDefaults.CurrentHydrationProtocolVersion);
            abi["processProtocolVersion"]!.GetValue<int>().ShouldBe(PluginAbiDefaults.CurrentProcessProtocolVersion);
            abi["harnessVersion"]!.GetValue<string>().ShouldBe(PluginAbiDefaults.CurrentHarnessVersion);
            abi["supportedStateStoreModes"]!.AsArray().Select(value => value!.GetValue<string>()).ShouldBe(PluginAbiDefaults.CurrentSupportedStateStoreModes);
            abi["supportedPropertyKinds"]!.AsArray().Select(value => value!.GetValue<string>()).ShouldBe(PluginAbiDefaults.CurrentSupportedPropertyKinds);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private sealed class AbiInput
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class AbiOutput
    {
        public int Score { get; set; }
    }
}
