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
    public void Generator_reports_error_when_input_dto_is_not_marked_for_json_source_generation()
    {
        var diagnostics = RunGenerator("""
using System.Threading.Tasks;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<TestPlugin>("Test Plugin")
    .ConfigureInputChannel(p => p.Request)
    .Build();

public sealed class TestPlugin : IPlugin
{
    public RequestInput? Request { get; set; }
    public Task Execute() => Task.CompletedTask;
}

public sealed class RequestInput
{
    public string Message { get; set; } = "";
}
""");

        diagnostics.ShouldContain(diagnostic =>
            diagnostic.Id == "WBP005" &&
            diagnostic.GetMessage().Contains("Input channel 'Request'", StringComparison.Ordinal) &&
            diagnostic.GetMessage().Contains("RequestInput", StringComparison.Ordinal));
    }

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
    public void Generator_emits_manifest_with_only_input_and_output_channels()
    {
        var runResult = RunGeneratorResult("""
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

[PluginJsonSerializable]
public sealed class SuccessOutput
{
    public string Message { get; set; } = "";
}
""");

        var source = runResult.Results.Single().GeneratedSources.Single().SourceText.ToString();

        runResult.Diagnostics.ShouldBeEmpty();
        source.ShouldContain("outputChannels");
        source.ShouldContain("inputChannel");
    }

    [Test]
    public void Generator_emits_input_materializer_provider_metadata_for_marked_input_dto()
    {
        var runResult = RunGeneratorResult("""
using System.Threading.Tasks;
using Backend.Plugins.Harness;

var plugin = Plugin.CreateBuilder<TestPlugin>("Test Plugin")
    .ConfigureInputChannel(p => p.Request)
    .Build();

public sealed class TestPlugin : IPlugin
{
    public RequestInput? Request { get; set; }
    public Task Execute() => Task.CompletedTask;
}

[PluginJsonSerializable]
public sealed class RequestInput
{
    public string Message { get; set; } = "";
    public int Count { get; set; }
}
""");

        var source = runResult.Results.Single().GeneratedSources.Single().SourceText.ToString();

        runResult.Diagnostics.ShouldBeEmpty();
        source.ShouldContain("WorkflowBuilder.PluginInputMaterializerProvider");
        source.ShouldContain("WorkflowBuilderPluginInputMaterializerProvider");
        source.ShouldContain("IPluginInputMaterializer<global::RequestInput>");
        source.ShouldContain("PluginInputMaterializerUtilities.ReadString");
        source.ShouldContain("PluginInputMaterializerUtilities.ReadInt32");
    }

    private static IReadOnlyList<Diagnostic> RunGenerator(string source)
    {
        return RunGeneratorResult(source).Diagnostics;
    }

    private static GeneratorDriverRunResult RunGeneratorResult(string source)
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

        return driver.GetRunResult();
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
