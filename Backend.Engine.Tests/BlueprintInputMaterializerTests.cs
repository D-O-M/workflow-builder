using System.Text.Json;
using Backend.Models;
using Shouldly;

namespace Backend.Engine.Tests;

public class BlueprintInputMaterializerTests
{
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
