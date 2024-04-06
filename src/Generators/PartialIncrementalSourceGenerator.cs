using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using PartialSourceGen.Builders;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace PartialSourceGen.Generators;

/// <summary>
/// An incremental generator for constructing partial entities
/// </summary>
[Generator]
public class PartialIncrementalSourceGenerator : IIncrementalGenerator
{
    private const string Disclaimer = """
    //------------------------------------------------------------------------------
    // <auto-generated>
    //     This code was generated from a template.
    //
    //     Manual changes to this file may cause unexpected behavior in your application.
    //     Manual changes to this file will be overwritten if the code is regenerated.
    // </auto-generated>
    //------------------------------------------------------------------------------

    """;

    private const string SourceAttribute = """

    using System;

    #if !PARTIALSOURCEGEN_EXCLUDE_ATTRIBUTES
    namespace PartialSourceGen
    {
        #nullable enable
        /// <summary>
        /// Generate partial optional properties of this class/struct.
        /// </summary>
        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
        internal sealed class PartialAttribute : Attribute
        {
            /// <summary>
            /// The optional summary for the partial entity. If not given
            /// the original summary will be used.
            /// </summary>
            public string? Summary { get; set; }

            /// <summary>
            /// If true, required properties will maintain their required modifier.
            /// If false, the partial entity will remove the required modifier.
            /// </summary>
            public bool IncludeRequiredProperties { get; set; }

            /// <summary>
            /// The optional class name for the partial entity.
            /// If not specified, the naming convection will be Partial[ClassName]
            /// </summary>
            public string? PartialClassName { get; set; }
        }

        /// <summary>
        /// Include the initializer for this property in the partial entity.
        /// </summary>
        [AttributeUsage(AttributeTargets.Property)]
        internal sealed class IncludeInitializerAttribute : Attribute
        {
        }

        #if NET7_0_OR_GREATER
        /// <summary>
        /// Replace a type with a partial reference
        /// </summary>
        /// <typeparam name="TOriginal">The original type</typeparam>
        /// <typeparam name="TPartial">The partial type</typeparam>
        [AttributeUsage(AttributeTargets.Property)]
        internal sealed class PartialReferenceAttribute<TOriginal, TPartial> : Attribute
        {
            /// <summary>
            /// Instantiate a partial reference attribute
            /// </summary>
            /// <param name="name">The partial property name to generate</param>
            public PartialReferenceAttribute(string? name = null)
            {
            }
        }
        #endif
        /// <summary>
        /// Replace a type with a partial reference
        /// </summary>
        [AttributeUsage(AttributeTargets.Property)]
        internal sealed class PartialReferenceAttribute : Attribute
        {
            /// <summary>
            /// Instantiate a partial reference attribute
            /// </summary>
            /// <param name="original">The original type</param>
            /// <param name="partial">The partial type</param>
            /// <param name="name">The partial property name to generate</param>
            public PartialReferenceAttribute(Type original, Type partial, string? name = null)
            {
            }
        }
        #nullable disable
    }
    #endif
    """;

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx => ctx.AddSource("PartialSourceGenAttributes.g.cs", SourceText.From(Disclaimer + SourceAttribute, Encoding.UTF8)));

        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            fullyQualifiedMetadataName: "PartialSourceGen.PartialAttribute",
            predicate: static (n, _) => n.IsKind(SyntaxKind.ClassDeclaration)
                                 || n.IsKind(SyntaxKind.StructDeclaration)
                                 || n.IsKind(SyntaxKind.RecordDeclaration)
                                 || n.IsKind(SyntaxKind.RecordStructDeclaration),
            transform: SemanticTransform)
            .Where(static n => n is not null);

        context.RegisterSourceOutput(candidates, static (spc, source) => Execute(in source, spc));
    }

    private PartialInfo? SemanticTransform(GeneratorAttributeSyntaxContext context, CancellationToken token)
    {
        context.Attributes.SingleOrDefault(a => a.NamedArguments.Any(n => n.Key == ""));
        if (context.TargetSymbol is not INamedTypeSymbol nameSymbol)
        {
            return null;
        }

        var root = context.SemanticModel.SyntaxTree.GetRoot(token);
        var props = context.TargetNode.DescendantNodes().OfType<PropertyDeclarationSyntax>();
        if (!props.Any())
        {
            return null;
        }

        var name = nameSymbol.Name;
        var givenName = context.GetPartialClassName();
        var node = context.TargetNode;
        var summary = context.GetSummaryText();
        var includeRequired = context.GetIncludeRequiredProperties();
        return new(
            givenName ?? ("Partial" + name),
            summary,
            includeRequired,
            root,
            node,
            props.ToArray()
        );
    }

    private static void Execute(in PartialInfo? source, SourceProductionContext spc)
    {
        if (!source.HasValue)
        {
            return;
        }

        var (name, summaryTxt, includeRequired, root, node, originalProps) = source.GetValueOrDefault();
        List<PropertyDeclarationSyntax> optionalProps = [];
        Dictionary<string, MemberDeclarationSyntax> propMembers = [];
        var hasPropertyInitializer = false;

        foreach (var prop in originalProps)
        {
            var includeInitializerAttribute = prop.AttributeLists
                                                  .SelectMany(ats => ats.DescendantNodes().OfType<IdentifierNameSyntax>())
                                                  .FirstOrDefault(n => n.Identifier.ValueText.StartsWith("IncludeInitializer"));
            var includeInitializer = includeInitializerAttribute is not null && prop.Initializer is not null;
            var isExpression = prop.ExpressionBody is not null;
            TypeSyntax propertyType;
            if (prop.Type is NullableTypeSyntax nts)
            {
                propertyType = nts;
            }
            else
            {
                var hasRequiredAttribute = prop.AttributeLists.SelectMany(attrs => attrs.Attributes.Select(a => a.Name.GetText().ToString()))
                                                   .Any(s => s.StartsWith("Required", System.StringComparison.OrdinalIgnoreCase));
                var hasRequired = prop.Modifiers.Any(m => m.IsKind(SyntaxKind.RequiredKeyword));
                var keepType = hasRequired || hasRequiredAttribute || includeInitializer;

                if (keepType)
                {
                    // Retain original type when
                    // 1. has Required attribute
                    // 2. has required keyword
                    // 3. has IncludeInitializer with initializer
                    propertyType = prop.Type;
                }
                else
                {
                    propertyType = SyntaxFactory.NullableType(prop.Type);
                }
            }

            var propName = prop.Identifier.ValueText.Trim();
            IEnumerable<SyntaxToken> modifiers = prop.Modifiers;

            if (!includeRequired)
            {
                // Remove the required keyword
                modifiers = prop.Modifiers.Where(m => !m.IsKind(SyntaxKind.RequiredKeyword));
            }

            // A candidate for the optional property
            PropertyDeclarationSyntax candidateProp;

            if (!isExpression)
            {
                candidateProp = SyntaxFactory
                        .PropertyDeclaration(propertyType, propName)
                        .WithModifiers(SyntaxFactory.TokenList(modifiers))
                        .WithAccessorList(prop.AccessorList)
                        .WithLeadingTrivia(prop.GetLeadingTrivia());
            }
            else
            {
                candidateProp = SyntaxFactory
                        .PropertyDeclaration(propertyType, propName)
                        .WithModifiers(SyntaxFactory.TokenList(modifiers))
                        .WithAccessorList(prop.AccessorList)
                        .WithExpressionBody(prop.ExpressionBody)
                        .WithSemicolonToken(prop.SemicolonToken)
                        .WithLeadingTrivia(prop.GetLeadingTrivia());
            }

            // Get partial reference types
            var hasPartialReference = prop.GetPartialReferenceInfo(out var originalSource, out var partialSource, out var partialRefName);
            if (hasPartialReference)
            {
                var partialRefProp = SyntaxFactory.ParseTypeName(partialSource!);
                candidateProp = candidateProp
                    .ReplaceNodes(candidateProp.DescendantNodes().OfType<IdentifierNameSyntax>(), (n, _) =>
                        n.IsEquivalentTo(originalSource!, topLevel: true)
                            ? partialRefProp
                            : n);

                if (!string.IsNullOrWhiteSpace(partialRefName))
                {
                    candidateProp = candidateProp.WithIdentifier(SyntaxFactory.Identifier(partialRefName!));
                }
            }

            if (includeInitializer)
            {
                candidateProp = candidateProp
                    .WithInitializer(prop.Initializer)
                    .WithSemicolonToken(prop.SemicolonToken);
            }

            // Get all field and method references
            var hasPropertyMembers = prop.PropertyMemberReferences(node, out var constructPropMembers);
            if (hasPropertyMembers)
            {
                foreach (var propertyMember in constructPropMembers!)
                {
                    propMembers.TryAdd(propertyMember.Key, propertyMember.Value);
                }
            }

            hasPropertyInitializer = hasPropertyInitializer || (prop.Initializer is not null && includeInitializer);
            optionalProps.Add(candidateProp);
        }

        List<MemberDeclarationSyntax> members = [.. optionalProps];

        if (propMembers.Any())
        {
            members.AddRange(propMembers.Values);
        }

        // Sort members
        members = [.. members.OrderBy(declaration =>
        {
            if (declaration is FieldDeclarationSyntax)
                return 0; // Field comes first
            else if (declaration is PropertyDeclarationSyntax)
                return 1; // Property comes second
            else if (declaration is MethodDeclarationSyntax)
                return 2; // Method comes third
            else
                return 3; // Other member types can be handled accordingly
        })];

        var excludeNotNullConstraint = node.DescendantNodes().OfType<TypeParameterConstraintClauseSyntax>().Where(cs => cs.Constraints.Any(c => c.DescendantNodes().OfType<IdentifierNameSyntax>().Any(n => !n.Identifier.ValueText.Equals("notnull"))));

        SyntaxNode? partialType = node switch
        {
            RecordDeclarationSyntax record => SyntaxFactory
                .RecordDeclaration(record.Kind(), record.Keyword, name)
                .WithClassOrStructKeyword(record.ClassOrStructKeyword)
                .WithModifiers(record.AddPartialKeyword())
                .WithConstraintClauses(SyntaxFactory.List(excludeNotNullConstraint))
                .WithTypeParameterList(record.TypeParameterList)
                .WithOpenBraceToken(record.OpenBraceToken)
                .IncludeConstructorIfStruct(record, hasPropertyInitializer, propMembers)
                .AddMembers([.. members])
                .WithCloseBraceToken(record.CloseBraceToken)
                .WithSummary(record, summaryTxt),
            StructDeclarationSyntax val => SyntaxFactory
                .StructDeclaration(name)
                .WithModifiers(val.AddPartialKeyword())
                .WithTypeParameterList(val.TypeParameterList)
                .WithConstraintClauses(SyntaxFactory.List(excludeNotNullConstraint))
                .WithOpenBraceToken(val.OpenBraceToken)
                .IncludeConstructorOnInitializer(val, hasPropertyInitializer, propMembers)
                .AddMembers([.. members])
                .WithCloseBraceToken(val.CloseBraceToken)
                .WithSummary(val, summaryTxt),
            ClassDeclarationSyntax val => SyntaxFactory
                .ClassDeclaration(name)
                .WithModifiers(val.AddPartialKeyword())
                .WithTypeParameterList(val.TypeParameterList)
                .WithConstraintClauses(SyntaxFactory.List(excludeNotNullConstraint))
                .WithOpenBraceToken(val.OpenBraceToken)
                .AddMembers([.. members])
                .WithCloseBraceToken(val.CloseBraceToken)
                .WithSummary(val, summaryTxt),
            _ => null
        };

        if (partialType is null)
        {
            return;
        }

        var newRoot = root
            .ReplaceNode(node, partialType)
            .WithNullableEnableDirective()
            .FilterOutEntitiesExcept(partialType)
            .NormalizeWhitespace();

        var newTree = SyntaxFactory.SyntaxTree(newRoot, root.SyntaxTree.Options);
        var sourceText = newTree.GetText().ToString();

        spc.AddSource(name + ".g.cs", Disclaimer + sourceText);
    }
}

internal readonly record struct PartialInfo
{
    public PartialInfo(
        string name,
        string? summary,
        bool includeRequired,
        SyntaxNode root,
        SyntaxNode node,
        PropertyDeclarationSyntax[] properties)
    {
        Name = name;
        Summary = summary;
        IncludeRequired = includeRequired;
        Root = root;
        Node = node;
        Properties = properties;
    }

    public string Name { get; }
    public string? Summary { get; }
    public bool IncludeRequired { get; }
    public SyntaxNode Root { get; }
    public SyntaxNode Node { get; }
    public PropertyDeclarationSyntax[] Properties { get; }

    public void Deconstruct(out string name, out string? summary, out bool includeRequired, out SyntaxNode root, out SyntaxNode node, out PropertyDeclarationSyntax[] properties)
    {
        name = Name;
        summary = Summary;
        includeRequired = IncludeRequired;
        root = Root;
        node = Node;
        properties = Properties;
    }
}