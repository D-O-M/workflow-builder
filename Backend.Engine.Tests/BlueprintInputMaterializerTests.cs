using System;
using System.Collections.Generic;
using System.Text.Json;
using Backend.Models;
using Backend.Plugins.Communication;
using Shouldly;

namespace Backend.Engine.Tests;

public class BlueprintInputMaterializerTests
{
    [Test]
    public void PluginExecutionResult_RoundTrips_null_global_state_for_no_state_plugins()
    {
        var result = new PluginExecutionResult(
            Guid.NewGuid(),
            Succeeded: true,
            OutputData: new Dictionary<string, JsonElement>(StringComparer.Ordinal),
            GlobalState: null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            ErrorMessage: null);

        var payload = PluginExecutionResultSerializer.Serialize(result);
        var roundTripped = PluginExecutionResultSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(payload));

        roundTripped.GlobalState.ShouldBeNull();
    }

    [Test]
    public void MaterializePartialInput_UsesExactContractPropertyNames()
    {
        using var document = JsonDocument.Parse("""
            {
              "AirportCode": "EGLL",
              "WindowLabel": "2026-04-25 to 2026-04-26"
            }
            """);

        var materialized = BlueprintInputMaterializer.MaterializePartialInput(
            document.RootElement,
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "AirportCode",
                    TargetProperty = "AirportCode"
                }
            ]);

        materialized.Keys.ShouldBe(["AirportCode"]);
        materialized["AirportCode"].GetString().ShouldBe("EGLL");
    }

    [Test]
    public void MaterializePartialInput_ToleratesCamelCaseOutputFromOlderPluginBundles()
    {
        using var document = JsonDocument.Parse("""
            {
              "airportCode": "EGLL"
            }
            """);

        var materialized = BlueprintInputMaterializer.MaterializePartialInput(
            document.RootElement,
            [
                new ExecutableBlueprintPropertyMapping
                {
                    SourceProperty = "AirportCode",
                    TargetProperty = "AirportCode"
                }
            ]);

        materialized.Keys.ShouldBe(["AirportCode"]);
        materialized["AirportCode"].GetString().ShouldBe("EGLL");
    }
}
