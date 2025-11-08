using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PluginManifestGenerator;

public static class PluginManifestBuilder
{
    public static PluginManifest BuildManifest(INamedTypeSymbol pluginSymbol, SemanticModel model)
    {
        var manifest = new PluginManifest
        {
            PluginClassName = pluginSymbol.Name
        };

        var inputProp = pluginSymbol.GetMembers().OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.Name == "InputChannels");
        var outputProp = pluginSymbol.GetMembers().OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.Name == "OutputChannels");

        if (inputProp != null)
        {
            manifest.InputChannels.AddRange(GetChannelElementTypes(inputProp, model));
        }

        if (outputProp != null)
        {
            manifest.OutputChannels.AddRange(GetChannelElementTypes(outputProp, model));
        }

        return manifest;
    }

    private static IEnumerable<ChannelManifest> GetChannelElementTypes(IPropertySymbol channelProp, SemanticModel model)
    {
        var channels = new List<ChannelManifest>();
        var syntaxRef = channelProp.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return channels;

        var propSyntax = syntaxRef.GetSyntax() as PropertyDeclarationSyntax;
        if (propSyntax == null) return channels;

        ExpressionSyntax expr = null;
        if (propSyntax.ExpressionBody != null)
            expr = propSyntax.ExpressionBody.Expression;
        else if (propSyntax.Initializer != null)
            expr = propSyntax.Initializer.Value;

        // ---- C# 12 collection expression support ([ ... ]) ----
        if (expr is CollectionExpressionSyntax colExpr)
        {
            foreach (var item in colExpr.Elements)
            {
                // Only interested in actual expressions
                if (item is ExpressionElementSyntax exprElem && exprElem.Expression is ObjectCreationExpressionSyntax objCreate)
                {
                    TryAddChannel(objCreate, model, channels);
                }
            }
        }
        // ---- Old-style: new Channel<T>[] { ... } ----
        else if (expr is ArrayCreationExpressionSyntax arrayCreate && arrayCreate.Initializer != null)
        {
            foreach (var itemExpr in arrayCreate.Initializer.Expressions)
            {
                if (itemExpr is ObjectCreationExpressionSyntax objCreate)
                    TryAddChannel(objCreate, model, channels);
            }
        }
        // ---- Implicit array creation: new[] { ... } ----
        else if (expr is ImplicitArrayCreationExpressionSyntax implicitArray && implicitArray.Initializer != null)
        {
            foreach (var itemExpr in implicitArray.Initializer.Expressions)
            {
                if (itemExpr is ObjectCreationExpressionSyntax objCreate)
                    TryAddChannel(objCreate, model, channels);
            }
        }

        return channels;
    }

    private static void TryAddChannel(
        ObjectCreationExpressionSyntax objCreate,
        SemanticModel model,
        List<ChannelManifest> channels)
    {
        var typeInfo = model.GetTypeInfo(objCreate);
        if (typeInfo.Type is INamedTypeSymbol channelSymbol && channelSymbol.IsGenericType)
        {
            var typeArg = channelSymbol.TypeArguments[0] as INamedTypeSymbol;
            if (typeArg != null)
            {
                var fields = typeArg.GetMembers().OfType<IPropertySymbol>()
                    .Where(p => p.DeclaredAccessibility == Accessibility.Public)
                    .Select(p => new FieldManifest
                    {
                        Name = p.Name,
                        Type = p.Type.Name
                    }).ToList();

                channels.Add(new ChannelManifest
                {
                    TypeName = typeArg.Name,
                    Description = "(description not resolvable statically)",
                    Fields = fields
                });
            }
        }
    }
}
