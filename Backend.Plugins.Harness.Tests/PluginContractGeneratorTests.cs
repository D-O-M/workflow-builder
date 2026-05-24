extern alias Generator;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using PluginContractGenerator = Generator::Backend.Plugins.Harness.Generator.PluginContractGenerator;

namespace Backend.Plugins.Harness.Tests;

public sealed class PluginContractGeneratorTests
{
    [Test]
    public void Generator_reports_error_when_output_dto_is_not_marked_for_json_source_generation()
    {
        var diagnostics = RunGenerator("""
using System.Threading.Tasks;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<TestPlugin>("Test Plugin")
    .ConfigureOutputChannel(p => p.Success)
    .Build();

public sealed class TestPlugin : IPlugin
{
    public SuccessOutput? Success { get; set; }
    public Task Execute() => Task.CompletedTask;
}

public sealed class SuccessOutput
{
    public string Message { get; set; } = "";
}
""");

        diagnostics.ShouldContain(diagnostic =>
            diagnostic.Id == "WBP005" &&
            diagnostic.GetMessage().Contains("Output channel 'Success'", StringComparison.Ordinal) &&
            diagnostic.GetMessage().Contains("SuccessOutput", StringComparison.Ordinal));
    }

    [Test]
    public void Generator_reports_error_when_global_state_dto_is_not_marked_for_json_source_generation()
    {
        var diagnostics = RunGenerator("""
using System.Threading.Tasks;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<TestPlugin>("Test Plugin")
    .WithGlobalState(p => p.State)
    .Build();

public sealed class TestPlugin : IPlugin
{
    public required TestState State { get; set; }
    public Task Execute() => Task.CompletedTask;
}

public sealed class TestState : IGlobalState
{
    public string RunLabel { get; set; } = "";
}
""");

        diagnostics.ShouldContain(diagnostic =>
            diagnostic.Id == "WBP005" &&
            diagnostic.GetMessage().Contains("Global state 'State'", StringComparison.Ordinal) &&
            diagnostic.GetMessage().Contains("TestState", StringComparison.Ordinal));
    }

    [Test]
    public void Generator_accepts_marked_output_and_global_state_dtos()
    {
        var diagnostics = RunGenerator("""
using System.Threading.Tasks;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<TestPlugin>("Test Plugin")
    .WithGlobalState(p => p.State)
    .ConfigureOutputChannel(p => p.Success)
    .Build();

public sealed class TestPlugin : IPlugin
{
    public required TestState State { get; set; }
    public SuccessOutput? Success { get; set; }
    public Task Execute() => Task.CompletedTask;
}

[PluginJsonSerializable]
public sealed class TestState : IGlobalState
{
    public string RunLabel { get; set; } = "";
}

[PluginJsonSerializable]
public sealed class SuccessOutput
{
    public string Message { get; set; } = "";
}
""");

        diagnostics.ShouldNotContain(diagnostic => diagnostic.Id == "WBP005");
    }

    private static IReadOnlyList<Diagnostic> RunGenerator(string source)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var references = GetMetadataReferences();
        var compilation = CSharpCompilation.Create(
            "PluginContractGeneratorTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new PluginContractGenerator());
        driver = driver.RunGenerators(compilation);

        return driver.GetRunResult().Diagnostics;
    }

    private static IReadOnlyList<MetadataReference> GetMetadataReferences()
    {
        var trustedPlatformAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
            ?.Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .ToList()
            ?? [];

        trustedPlatformAssemblies.Add(MetadataReference.CreateFromFile(typeof(Plugin).Assembly.Location));
        return trustedPlatformAssemblies;
    }
}
