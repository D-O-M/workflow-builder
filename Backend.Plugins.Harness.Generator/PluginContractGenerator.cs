using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Backend.Plugins.Harness.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class PluginContractGenerator : IIncrementalGenerator
{
    private const string ContractProviderMetadataKey = "WorkflowBuilder.PluginContractProvider";

    private static readonly SymbolDisplayFormat FullyQualifiedFormat =
        new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly DiagnosticDescriptor MultipleBuilderChainsRule = new(
        id: "WBP001",
        title: "Multiple plugin builder chains are not supported",
        messageFormat: "The plugin contract generator found more than one Plugin.CreateBuilder<TPlugin>(...) chain in this assembly.",
        category: "WorkflowBuilder.PluginContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedBuilderShapeRule = new(
        id: "WBP002",
        title: "Unsupported plugin builder shape",
        messageFormat: "{0}",
        category: "WorkflowBuilder.PluginContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ContractModelMustBeFlatRule = new(
        id: "WBP003",
        title: "Plugin contract models must be flat",
        messageFormat: "{0}",
        category: "WorkflowBuilder.PluginContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedPropertyTypeRule = new(
        id: "WBP004",
        title: "Unsupported contract property type",
        messageFormat: "{0}",
        category: "WorkflowBuilder.PluginContracts",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var analysis = context.CompilationProvider.Select(static (compilation, _) => AnalyzeCompilation(compilation));

        context.RegisterSourceOutput(analysis, static (productionContext, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                productionContext.ReportDiagnostic(diagnostic);
            }

            if (result.Contract is null)
            {
                return;
            }

            var providerTypeName = GetProviderTypeName(result.Contract);
            var source = RenderProviderSource(result.Contract, providerTypeName);
            productionContext.AddSource($"{SanitizeIdentifier(result.Contract.PluginTypeName)}.PluginContract.g.cs", source);
        });
    }

    private static AnalysisResult AnalyzeCompilation(Compilation compilation)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var pluginBuilders = ImmutableArray.CreateBuilder<PluginBuilderInvocation>();

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot();
            var buildInvocations = root
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                                            memberAccess.Name.Identifier.ValueText == "Build");

            foreach (var buildInvocation in buildInvocations)
            {
                var parsed = TryParseBuilderChain(buildInvocation, semanticModel);
                if (parsed.Diagnostic is not null)
                {
                    diagnostics.Add(parsed.Diagnostic);
                    continue;
                }

                if (parsed.Builder is not null)
                {
                    pluginBuilders.Add(parsed.Builder);
                }
            }
        }

        if (pluginBuilders.Count == 0)
        {
            return new AnalysisResult(null, diagnostics.ToImmutable());
        }

        if (pluginBuilders.Count > 1)
        {
            foreach (var builder in pluginBuilders)
            {
                diagnostics.Add(Diagnostic.Create(MultipleBuilderChainsRule, builder.Location));
            }

            return new AnalysisResult(null, diagnostics.ToImmutable());
        }

        var contract = BuildContract(pluginBuilders[0], diagnostics);
        return new AnalysisResult(contract, diagnostics.ToImmutable());
    }

    private static ParsedBuilderChain TryParseBuilderChain(
        InvocationExpressionSyntax buildInvocation,
        SemanticModel semanticModel)
    {
        var inputChannels = new List<PluginChannelContract>();
        var outputChannels = new List<PluginChannelContract>();
        PluginChannelContract? globalState = null;
        string? displayName = null;
        string? pluginTypeName = null;
        Location location = buildInvocation.GetLocation();

        var currentInvocation = buildInvocation;
        while (true)
        {
            if (currentInvocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                return ParsedBuilderChain.FromDiagnostic(
                    Diagnostic.Create(
                        UnsupportedBuilderShapeRule,
                        currentInvocation.GetLocation(),
                        "Plugin builder chains must be direct invocation chains ending in .Build()."));
            }

            var memberName = memberAccess.Name.Identifier.ValueText;

            if (memberName == "Build")
            {
                if (memberAccess.Expression is not InvocationExpressionSyntax nextInvocation)
                {
                    return ParsedBuilderChain.FromDiagnostic(
                        Diagnostic.Create(
                            UnsupportedBuilderShapeRule,
                            currentInvocation.GetLocation(),
                            "Build() must be called directly on the plugin builder chain."));
                }

                currentInvocation = nextInvocation;
                continue;
            }

            if (memberName is "ConfigureInputChannel" or "ConfigureOutputChannel" or "WithGlobalState")
            {
                if (currentInvocation.ArgumentList.Arguments.Count != 1)
                {
                    return ParsedBuilderChain.FromDiagnostic(
                        Diagnostic.Create(
                            UnsupportedBuilderShapeRule,
                            currentInvocation.GetLocation(),
                            $"'{memberName}' must be called with exactly one property-selector lambda."));
                }

                var lambdaExpression = currentInvocation.ArgumentList.Arguments[0].Expression;
                if (!TryGetSelectedProperty(lambdaExpression, semanticModel, out var propertySymbol))
                {
                    return ParsedBuilderChain.FromDiagnostic(
                        Diagnostic.Create(
                            UnsupportedBuilderShapeRule,
                            currentInvocation.GetLocation(),
                            $"'{memberName}' must use a direct property selector such as p => p.Input."));
                }

                var contract = new PluginChannelContract(
                    propertySymbol.Name,
                    ToDisplayName(propertySymbol.Type),
                    propertySymbol.Type,
                    location);

                switch (memberName)
                {
                    case "ConfigureInputChannel":
                        inputChannels.Add(contract);
                        break;
                    case "ConfigureOutputChannel":
                        outputChannels.Add(contract);
                        break;
                    default:
                        globalState = contract;
                        break;
                }

                if (memberAccess.Expression is not InvocationExpressionSyntax nextInvocation)
                {
                    return ParsedBuilderChain.FromDiagnostic(
                        Diagnostic.Create(
                            UnsupportedBuilderShapeRule,
                            currentInvocation.GetLocation(),
                            $"'{memberName}' must be chained directly from the plugin builder."));
                }

                currentInvocation = nextInvocation;
                continue;
            }

            if (memberName == "CreateBuilder")
            {
                if (currentInvocation.ArgumentList.Arguments.Count != 1)
                {
                    return ParsedBuilderChain.FromDiagnostic(
                        Diagnostic.Create(
                            UnsupportedBuilderShapeRule,
                            currentInvocation.GetLocation(),
                            "Plugin.CreateBuilder<TPlugin>(...) must be called with a single literal display-name argument."));
                }

                var displayNameOperation = semanticModel.GetOperation(currentInvocation.ArgumentList.Arguments[0].Expression);
                if (displayNameOperation?.ConstantValue is not { HasValue: true, Value: string display })
                {
                    return ParsedBuilderChain.FromDiagnostic(
                        Diagnostic.Create(
                            UnsupportedBuilderShapeRule,
                            currentInvocation.ArgumentList.Arguments[0].GetLocation(),
                            "Plugin.CreateBuilder<TPlugin>(...) must use a literal display-name argument."));
                }

                if (memberAccess.Name is not GenericNameSyntax genericName ||
                    genericName.TypeArgumentList.Arguments.Count != 1)
                {
                    return ParsedBuilderChain.FromDiagnostic(
                        Diagnostic.Create(
                            UnsupportedBuilderShapeRule,
                            currentInvocation.GetLocation(),
                            "Plugin.CreateBuilder<TPlugin>(...) must specify exactly one plugin type argument."));
                }

                if (semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type is not INamedTypeSymbol pluginTypeSymbol)
                {
                    return ParsedBuilderChain.FromDiagnostic(
                        Diagnostic.Create(
                            UnsupportedBuilderShapeRule,
                            currentInvocation.GetLocation(),
                            "Unable to resolve the plugin type supplied to Plugin.CreateBuilder<TPlugin>(...)."));
                }

                displayName = display;
                pluginTypeName = ToDisplayName(pluginTypeSymbol);
                break;
            }

            return ParsedBuilderChain.FromDiagnostic(
                Diagnostic.Create(
                    UnsupportedBuilderShapeRule,
                    currentInvocation.GetLocation(),
                    $"Unsupported call '{memberName}' in plugin builder chain. Only ConfigureInputChannel, ConfigureOutputChannel, WithGlobalState, and Build are supported."));
        }

        if (displayName is null || pluginTypeName is null)
        {
            return ParsedBuilderChain.FromDiagnostic(
                Diagnostic.Create(
                    UnsupportedBuilderShapeRule,
                    buildInvocation.GetLocation(),
                    "Could not resolve Plugin.CreateBuilder<TPlugin>(...) in the builder chain."));
        }

        if (inputChannels.Count > 1)
        {
            return ParsedBuilderChain.FromDiagnostic(
                Diagnostic.Create(
                    UnsupportedBuilderShapeRule,
                    buildInvocation.GetLocation(),
                    "Plugins may declare at most one input channel."));
        }

        return ParsedBuilderChain.FromBuilder(
            new PluginBuilderInvocation(
                displayName,
                pluginTypeName,
                inputChannels.SingleOrDefault(),
                outputChannels,
                globalState,
                buildInvocation.GetLocation()));
    }

    private static GeneratedPluginContract? BuildContract(
        PluginBuilderInvocation invocation,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var inputChannel = invocation.InputChannel is null
            ? null
            : BuildChannelContract(invocation.InputChannel, isGlobalState: false, diagnostics);

        var outputChannels = invocation.OutputChannels
            .Select(channel => BuildChannelContract(channel, isGlobalState: false, diagnostics))
            .Where(channel => channel is not null)
            .Cast<GeneratedChannelContract>()
            .ToList();

        var globalState = invocation.GlobalState is null
            ? BuildImplicitGlobalStateContract(invocation.Location, diagnostics)
            : BuildChannelContract(invocation.GlobalState, isGlobalState: true, diagnostics);

        if (inputChannel is null && invocation.InputChannel is not null)
        {
            return null;
        }

        if (globalState is null)
        {
            return null;
        }

        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return null;
        }

        var schemaDictionary = new Dictionary<string, GeneratedSchemaContract>(StringComparer.Ordinal);
        CollectSchema(inputChannel, schemaDictionary);
        CollectSchema(globalState, schemaDictionary);
        foreach (var channel in outputChannels)
        {
            CollectSchema(channel, schemaDictionary);
        }

        return new GeneratedPluginContract(
            invocation.DisplayName,
            invocation.PluginTypeName,
            inputChannel,
            outputChannels,
            globalState,
            schemaDictionary);
    }

    private static GeneratedChannelContract? BuildImplicitGlobalStateContract(
        Location location,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        return BuildChannelContract(
            new PluginChannelContract(
                "GlobalState",
                "Backend.Plugins.Harness.GlobalStateStub",
                null,
                location),
            isGlobalState: true,
            diagnostics);
    }

    private static GeneratedChannelContract? BuildChannelContract(
        PluginChannelContract channel,
        bool isGlobalState,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var channelType = channel.TypeSymbol;
        if (channelType is null)
        {
            channelType = null;
        }

        if (channelType is null &&
            !string.Equals(channel.TypeName, "Backend.Plugins.Harness.GlobalStateStub", StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic.Create(
                UnsupportedBuilderShapeRule,
                channel.Location,
                $"Unable to resolve the CLR type for channel '{channel.Name}'."));
            return null;
        }

        var resolvedChannelType = channelType?.TypeKind == TypeKind.Class || channelType?.TypeKind == TypeKind.Struct
            ? channelType
            : channelType;

        if (resolvedChannelType is not null &&
            !IsFlatObjectType(resolvedChannelType))
        {
            diagnostics.Add(Diagnostic.Create(
                ContractModelMustBeFlatRule,
                channel.Location,
                $"{(isGlobalState ? "Global state" : $"Channel '{channel.Name}'")} must be a flat DTO with only public scalar or enum properties."));
            return null;
        }

        var properties = new List<GeneratedPropertyContract>();
        if (resolvedChannelType is not null)
        {
            foreach (var property in resolvedChannelType
                         .GetMembers()
                         .OfType<IPropertySymbol>()
                         .Where(static property => !property.IsStatic && property.DeclaredAccessibility == Accessibility.Public))
            {
                if (!TryBuildPropertyContract(property, diagnostics, out var propertyContract))
                {
                    continue;
                }

                properties.Add(propertyContract);
            }
        }
        else if (string.Equals(channel.TypeName, "Backend.Plugins.Harness.GlobalStateStub", StringComparison.Ordinal))
        {
            properties.Add(new GeneratedPropertyContract("Empty", WorkflowScalarKind.Boolean, true, null));
        }

        return new GeneratedChannelContract(channel.Name, channel.TypeName, properties.OrderBy(property => property.Name, StringComparer.Ordinal).ToList());
    }

    private static bool TryBuildPropertyContract(
        IPropertySymbol property,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out GeneratedPropertyContract contract)
    {
        contract = default!;

        if (property.Parameters.Length > 0)
        {
            diagnostics.Add(Diagnostic.Create(
                UnsupportedPropertyTypeRule,
                property.Locations.FirstOrDefault(),
                $"Property '{property.Name}' cannot be indexed."));
            return false;
        }

        var propertyType = property.Type;
        if (propertyType.TypeKind == TypeKind.Array ||
            IsDictionaryType(propertyType) ||
            IsEnumerableType(propertyType))
        {
            diagnostics.Add(Diagnostic.Create(
                ContractModelMustBeFlatRule,
                property.Locations.FirstOrDefault(),
                $"Property '{property.Name}' uses an unsupported collection or dictionary type. Plugin contract models must be flat."));
            return false;
        }

        if (!TryMapWorkflowScalar(property, out var kind, out var isRequired, out var enumValues, out var reason))
        {
            diagnostics.Add(Diagnostic.Create(
                UnsupportedPropertyTypeRule,
                property.Locations.FirstOrDefault(),
                $"Property '{property.Name}' uses unsupported type '{ToDisplayName(property.Type)}'. {reason}"));
            return false;
        }

        contract = new GeneratedPropertyContract(property.Name, kind, isRequired, enumValues);
        return true;
    }

    private static bool TryMapWorkflowScalar(
        IPropertySymbol property,
        out WorkflowScalarKind kind,
        out bool isRequired,
        out IReadOnlyList<string>? enumValues,
        out string reason)
    {
        kind = default;
        enumValues = null;
        reason = string.Empty;

        var type = property.Type;
        var unwrappedType = UnwrapNullable(type, out var isNullableValueType);

        isRequired = !isNullableValueType && property.NullableAnnotation != NullableAnnotation.Annotated;

        if (unwrappedType.TypeKind == TypeKind.Enum)
        {
            kind = WorkflowScalarKind.Enum;
            enumValues = unwrappedType
                .GetMembers()
                .OfType<IFieldSymbol>()
                .Where(static field => field.HasConstantValue)
                .Select(field => field.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            return true;
        }

        switch (unwrappedType.SpecialType)
        {
            case SpecialType.System_String:
                kind = WorkflowScalarKind.String;
                return true;
            case SpecialType.System_Boolean:
                kind = WorkflowScalarKind.Boolean;
                return true;
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
                kind = WorkflowScalarKind.Integer;
                return true;
            case SpecialType.System_Decimal:
            case SpecialType.System_Double:
            case SpecialType.System_Single:
                kind = WorkflowScalarKind.Decimal;
                return true;
        }

        var displayName = ToDisplayName(unwrappedType);
        switch (displayName)
        {
            case "System.Guid":
                kind = WorkflowScalarKind.Guid;
                return true;
            case "System.DateOnly":
                kind = WorkflowScalarKind.Date;
                return true;
            case "System.DateTime":
            case "System.DateTimeOffset":
                kind = WorkflowScalarKind.DateTime;
                return true;
        }

        if (IsFlatObjectType(unwrappedType))
        {
            reason = "Nested object types are not supported.";
            return false;
        }

        reason = "Only String, Integer, Decimal, Boolean, Guid, Date, DateTime, and Enum properties are supported.";
        return false;
    }

    private static bool IsFlatObjectType(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None)
        {
            return false;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return false;
        }

        var displayName = ToDisplayName(type);
        return displayName is not "System.Guid" and not "System.DateOnly" and not "System.DateTime" and not "System.DateTimeOffset";
    }

    private static void CollectSchema(
        GeneratedChannelContract? channel,
        Dictionary<string, GeneratedSchemaContract> schemas)
    {
        if (channel is null || schemas.ContainsKey(channel.TypeName))
        {
            return;
        }

        schemas[channel.TypeName] = new GeneratedSchemaContract(channel.Properties);
    }

    private static string RenderProviderSource(GeneratedPluginContract contract, string providerTypeName)
    {
        var manifestJson = SerializeManifest(
            new PluginManifestFile
            {
                DisplayName = contract.DisplayName,
                InputChannel = contract.InputChannel is null ? null : MapChannel(contract.InputChannel),
                OutputChannels = contract.OutputChannels.Select(MapChannel).ToList(),
                GlobalState = MapChannel(contract.GlobalState)
            });

        var schemasJson = SerializeSchemas(
            new GeneratedSchemasFile
            {
                Components = new GeneratedSchemaComponents
                {
                    Schemas = contract.Schemas.ToDictionary(
                        entry => entry.Key,
                        entry => new GeneratedSchemaNode
                        {
                            Type = ["object"],
                            Required = entry.Value.Properties.Where(property => property.Required).Select(property => property.Name).ToArray(),
                            Properties = entry.Value.Properties.ToDictionary(
                                property => property.Name,
                                property => new GeneratedSchemaProperty
                                {
                                    Type = GetJsonSchemaTypes(property.Kind, property.Required),
                                    Format = GetJsonSchemaFormat(property.Kind),
                                    Enum = property.EnumValues?.ToArray()
                                },
                                StringComparer.Ordinal)
                        },
                        StringComparer.Ordinal)
                }
            });

        var escapedManifest = ToRawStringLiteral(manifestJson);
        var escapedSchemas = ToRawStringLiteral(schemasJson);

        return $$"""
using System;

[assembly: System.Reflection.AssemblyMetadata("{{ContractProviderMetadataKey}}", "{{providerTypeName}}")]

namespace {{GetProviderNamespace(providerTypeName)}};

internal static class {{GetProviderClassName(providerTypeName)}}
{
    public static string ManifestJson => {{escapedManifest}};
    public static string SchemasJson => {{escapedSchemas}};
}
""";
    }

    private static ManifestFileChannelInfo MapChannel(GeneratedChannelContract channel) =>
        new()
        {
            Name = channel.Name,
            Type = channel.TypeName,
            Properties = channel.Properties.Select(property => new ManifestFilePropertyInfo
            {
                Name = property.Name,
                Kind = property.Kind.ToString(),
                Required = property.Required,
                EnumValues = property.EnumValues?.ToArray() ?? []
            }).ToList()
        };

    private static string[] GetJsonSchemaTypes(WorkflowScalarKind kind, bool required)
    {
        var primary = kind switch
        {
            WorkflowScalarKind.Integer => "integer",
            WorkflowScalarKind.Decimal => "number",
            WorkflowScalarKind.Boolean => "boolean",
            _ => "string"
        };

        return required ? [primary] : [primary, "null"];
    }

    private static string? GetJsonSchemaFormat(WorkflowScalarKind kind) =>
        kind switch
        {
            WorkflowScalarKind.Guid => "uuid",
            WorkflowScalarKind.Date => "date",
            WorkflowScalarKind.DateTime => "date-time",
            _ => null
        };

    private static bool TryGetSelectedProperty(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        out IPropertySymbol propertySymbol)
    {
        propertySymbol = null!;

        var lambda = expression as LambdaExpressionSyntax;
        if (lambda?.Body is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        if (memberAccess.Expression is not IdentifierNameSyntax)
        {
            return false;
        }

        propertySymbol = semanticModel.GetSymbolInfo(memberAccess).Symbol as IPropertySymbol;
        return propertySymbol is not null;
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type, out bool isNullableValueType)
    {
        isNullableValueType = false;
        if (type is INamedTypeSymbol namedType &&
            namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            isNullableValueType = true;
            return namedType.TypeArguments[0];
        }

        return type;
    }

    private static bool IsDictionaryType(ITypeSymbol type) =>
        type.AllInterfaces.Any(@interface =>
            @interface.Name == "IDictionary" ||
            (@interface is INamedTypeSymbol namedType &&
             namedType.ConstructedFrom?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) == "System.Collections.Generic.IDictionary<TKey, TValue>"));

    private static bool IsEnumerableType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return false;
        }

        return type.AllInterfaces.Any(@interface =>
            @interface is INamedTypeSymbol namedType &&
            namedType.ConstructedFrom?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) == "System.Collections.Generic.IEnumerable<T>");
    }

    private static string ToDisplayName(ITypeSymbol symbol) =>
        symbol.ToDisplayString(FullyQualifiedFormat);

    private static string GetProviderTypeName(GeneratedPluginContract contract)
    {
        var normalizedPluginType = contract.PluginTypeName.Replace("global::", string.Empty);
        var lastDotIndex = normalizedPluginType.LastIndexOf('.');
        var pluginNamespace = lastDotIndex <= 0
            ? "WorkflowBuilder.Generated"
            : normalizedPluginType.Substring(0, lastDotIndex) + ".Generated";
        var pluginTypeName = lastDotIndex < 0
            ? normalizedPluginType
            : normalizedPluginType.Substring(lastDotIndex + 1);
        var providerTypeName = $"{SanitizeIdentifier(pluginTypeName)}PluginContractProvider";
        return $"{pluginNamespace}.{providerTypeName}";
    }

    private static string GetProviderNamespace(string providerTypeName)
    {
        var lastDotIndex = providerTypeName.LastIndexOf('.');
        return lastDotIndex <= 0 ? "WorkflowBuilder.Generated" : providerTypeName.Substring(0, lastDotIndex);
    }

    private static string GetProviderClassName(string providerTypeName)
    {
        var lastDotIndex = providerTypeName.LastIndexOf('.');
        return lastDotIndex < 0 || lastDotIndex == providerTypeName.Length - 1
            ? providerTypeName
            : providerTypeName.Substring(lastDotIndex + 1);
    }

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    private static string ToRawStringLiteral(string value)
    {
        var quoteCount = 3;
        var quoteSequence = new string('"', quoteCount);
        while (value.Contains(quoteSequence, StringComparison.Ordinal))
        {
            quoteCount++;
            quoteSequence = new string('"', quoteCount);
        }

        return $"{quoteSequence}{value}{quoteSequence}";
    }

    private static string SerializeManifest(PluginManifestFile manifest)
    {
        var builder = new JsonBuilder();
        builder.StartObject();
        builder.Property("displayName", manifest.DisplayName);
        builder.PropertyName("inputChannel");
        if (manifest.InputChannel is null)
        {
            builder.NullValue();
        }
        else
        {
            WriteChannel(builder, manifest.InputChannel);
        }

        builder.PropertyName("outputChannels");
        builder.StartArray();
        foreach (var channel in manifest.OutputChannels)
        {
            WriteChannel(builder, channel);
        }
        builder.EndArray();
        builder.PropertyName("globalState");
        WriteChannel(builder, manifest.GlobalState);
        builder.EndObject();
        return builder.ToString();
    }

    private static string SerializeSchemas(GeneratedSchemasFile schemasFile)
    {
        var builder = new JsonBuilder();
        builder.StartObject();
        builder.PropertyName("components");
        builder.StartObject();
        builder.PropertyName("schemas");
        builder.StartObject();

        foreach (var schemaEntry in schemasFile.Components.Schemas.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            builder.PropertyName(schemaEntry.Key);
            builder.StartObject();
            builder.Property("type", schemaEntry.Value.Type);
            builder.Property("required", schemaEntry.Value.Required);
            builder.PropertyName("properties");
            builder.StartObject();

            foreach (var propertyEntry in schemaEntry.Value.Properties.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                builder.PropertyName(propertyEntry.Key);
                builder.StartObject();
                builder.Property("type", propertyEntry.Value.Type);
                if (!string.IsNullOrWhiteSpace(propertyEntry.Value.Format))
                {
                    builder.Property("format", propertyEntry.Value.Format!);
                }

                if (propertyEntry.Value.Enum is not null && propertyEntry.Value.Enum.Length > 0)
                {
                    builder.Property("enum", propertyEntry.Value.Enum);
                }

                builder.EndObject();
            }

            builder.EndObject();
            builder.EndObject();
        }

        builder.EndObject();
        builder.EndObject();
        builder.EndObject();
        return builder.ToString();
    }

    private static void WriteChannel(JsonBuilder builder, ManifestFileChannelInfo channel)
    {
        builder.StartObject();
        builder.Property("name", channel.Name);
        builder.Property("type", channel.Type);
        builder.PropertyName("properties");
        builder.StartArray();
        foreach (var property in channel.Properties.OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            builder.StartObject();
            builder.Property("name", property.Name);
            builder.Property("kind", property.Kind);
            builder.Property("required", property.Required);
            if (property.EnumValues.Length > 0)
            {
                builder.Property("enumValues", property.EnumValues);
            }

            builder.EndObject();
        }
        builder.EndArray();
        builder.EndObject();
    }

    private sealed record AnalysisResult(
        GeneratedPluginContract? Contract,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ParsedBuilderChain(
        PluginBuilderInvocation? Builder,
        Diagnostic? Diagnostic)
    {
        public static ParsedBuilderChain FromBuilder(PluginBuilderInvocation builder) => new(builder, null);
        public static ParsedBuilderChain FromDiagnostic(Diagnostic diagnostic) => new(null, diagnostic);
    }

    private sealed record PluginBuilderInvocation(
        string DisplayName,
        string PluginTypeName,
        PluginChannelContract? InputChannel,
        IReadOnlyList<PluginChannelContract> OutputChannels,
        PluginChannelContract? GlobalState,
        Location Location);

    private sealed record PluginChannelContract(
        string Name,
        string TypeName,
        ITypeSymbol? TypeSymbol,
        Location Location);

    private sealed record GeneratedPluginContract(
        string DisplayName,
        string PluginTypeName,
        GeneratedChannelContract? InputChannel,
        IReadOnlyList<GeneratedChannelContract> OutputChannels,
        GeneratedChannelContract GlobalState,
        IReadOnlyDictionary<string, GeneratedSchemaContract> Schemas);

    private sealed record GeneratedChannelContract(
        string Name,
        string TypeName,
        IReadOnlyList<GeneratedPropertyContract> Properties);

    private sealed record GeneratedPropertyContract(
        string Name,
        WorkflowScalarKind Kind,
        bool Required,
        IReadOnlyList<string>? EnumValues);

    private sealed record GeneratedSchemaContract(
        IReadOnlyList<GeneratedPropertyContract> Properties);

    private enum WorkflowScalarKind
    {
        String,
        Integer,
        Decimal,
        Boolean,
        Guid,
        Date,
        DateTime,
        Enum
    }

    private sealed class PluginManifestFile
    {
        public string DisplayName { get; init; } = string.Empty;
        public ManifestFileChannelInfo? InputChannel { get; init; }
        public List<ManifestFileChannelInfo> OutputChannels { get; init; } = [];
        public ManifestFileChannelInfo GlobalState { get; init; } = new();
    }

    private sealed class ManifestFileChannelInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public List<ManifestFilePropertyInfo> Properties { get; init; } = [];
    }

    private sealed class ManifestFilePropertyInfo
    {
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public bool Required { get; init; }
        public string[] EnumValues { get; init; } = [];
    }

    private sealed class GeneratedSchemasFile
    {
        public GeneratedSchemaComponents Components { get; init; } = new();
    }

    private sealed class GeneratedSchemaComponents
    {
        public Dictionary<string, GeneratedSchemaNode> Schemas { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class GeneratedSchemaNode
    {
        public string[] Type { get; init; } = [];
        public string[] Required { get; init; } = [];
        public Dictionary<string, GeneratedSchemaProperty> Properties { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class GeneratedSchemaProperty
    {
        public string[] Type { get; init; } = [];
        public string? Format { get; init; }
        public string[]? Enum { get; init; }
    }

    private sealed class JsonBuilder
    {
        private readonly StringBuilder _builder = new();
        private readonly Stack<ContainerState> _stack = new();

        public void StartObject()
        {
            WriteValuePrefix();
            _builder.Append('{');
            _stack.Push(new ContainerState(ContainerKind.Object));
        }

        public void EndObject()
        {
            _builder.Append('}');
            _stack.Pop();
            MarkValueWritten();
        }

        public void StartArray()
        {
            WriteValuePrefix();
            _builder.Append('[');
            _stack.Push(new ContainerState(ContainerKind.Array));
        }

        public void EndArray()
        {
            _builder.Append(']');
            _stack.Pop();
            MarkValueWritten();
        }

        public void Property(string name, string value)
        {
            PropertyName(name);
            StringValue(value);
        }

        public void Property(string name, bool value)
        {
            PropertyName(name);
            BooleanValue(value);
        }

        public void Property(string name, IEnumerable<string> values)
        {
            PropertyName(name);
            StartArray();
            foreach (var value in values)
            {
                StringValue(value);
            }
            EndArray();
        }

        public void PropertyName(string name)
        {
            if (_stack.Count == 0 || _stack.Peek().Kind != ContainerKind.Object)
            {
                throw new InvalidOperationException("Property names can only be written inside JSON objects.");
            }

            var current = _stack.Peek();
            if (!current.IsFirst)
            {
                _builder.Append(',');
            }

            current.IsFirst = false;
            current.ExpectsValue = true;
            _builder.Append('"').Append(Escape(name)).Append("\":");
        }

        public void StringValue(string value)
        {
            WriteValuePrefix();
            _builder.Append('"').Append(Escape(value)).Append('"');
            MarkValueWritten();
        }

        public void BooleanValue(bool value)
        {
            WriteValuePrefix();
            _builder.Append(value ? "true" : "false");
            MarkValueWritten();
        }

        public void NullValue()
        {
            WriteValuePrefix();
            _builder.Append("null");
            MarkValueWritten();
        }

        public override string ToString() => _builder.ToString();

        private void WriteValuePrefix()
        {
            if (_stack.Count == 0)
            {
                return;
            }

            var current = _stack.Peek();
            if (current.Kind == ContainerKind.Array)
            {
                if (!current.IsFirst)
                {
                    _builder.Append(',');
                }

                current.IsFirst = false;
                return;
            }

            if (!current.ExpectsValue)
            {
                throw new InvalidOperationException("A JSON object property name must be written before its value.");
            }
        }

        private void MarkValueWritten()
        {
            if (_stack.Count == 0)
            {
                return;
            }

            var current = _stack.Peek();
            if (current.Kind == ContainerKind.Object)
            {
                current.ExpectsValue = false;
            }
        }

        private static string Escape(string value)
        {
            var escaped = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                escaped.Append(character switch
                {
                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\r' => "\\r",
                    '\n' => "\\n",
                    '\t' => "\\t",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    _ when char.IsControl(character) => $"\\u{(int)character:x4}",
                    _ => character.ToString()
                });
            }

            return escaped.ToString();
        }
    }

    private sealed class ContainerState
    {
        public ContainerState(ContainerKind kind)
        {
            Kind = kind;
        }

        public ContainerKind Kind { get; }
        public bool IsFirst { get; set; } = true;
        public bool ExpectsValue { get; set; }
    }

    private enum ContainerKind
    {
        Object,
        Array
    }
}
