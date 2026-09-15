using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Finare.Unreal.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class UnrealNativePropertyGenerator : IIncrementalGenerator
{
    private const string ExposeAttributeName = "Finare.ExposeAttribute";
    private const string ScriptAssetAttributeName = "Finare.ScriptAssetAttribute";
    private const string ScriptBaseName = "Finare.Unreal.UnrealObjectScript";
    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly DiagnosticDescriptor PropertyMustBePartial = new(
        "FINAREGEN001",
        "Reflected property must be partial",
        "Exposed reflected property '{0}' must be declared partial so Finare can generate live Unreal accessors",
        "Finare.Unreal.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor PropertyShapeUnsupported = new(
        "FINAREGEN002",
        "Reflected property shape is unsupported",
        "Exposed reflected property '{0}' must be a non-static, non-indexed partial property with get and set accessors",
        "Finare.Unreal.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TypeMustBePartial = new(
        "FINAREGEN003",
        "Reflected property owner must be partial",
        "Type '{0}' must be declared partial when it contains an exposed reflected property",
        "Finare.Unreal.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NestedTypeUnsupported = new(
        "FINAREGEN005",
        "Nested reflected property owner is unsupported",
        "Reflected property '{0}' must be declared on a top-level class",
        "Finare.Unreal.Generators",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<PropertyCandidate?> candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is PropertyDeclarationSyntax,
                static (syntaxContext, cancellationToken) =>
                    CreateCandidate(syntaxContext, cancellationToken))
            .Where(static candidate => candidate is not null);

        context.RegisterSourceOutput(
            candidates.Collect(),
            static (productionContext, collected) => Emit(productionContext, collected!));
    }

    private static PropertyCandidate? CreateCandidate(
        GeneratorSyntaxContext context,
        System.Threading.CancellationToken cancellationToken)
    {
        PropertyDeclarationSyntax propertySyntax = (PropertyDeclarationSyntax)context.Node;
        IPropertySymbol? property = context.SemanticModel.GetDeclaredSymbol(
            propertySyntax,
            cancellationToken) as IPropertySymbol;
        if (property is null)
        {
            return null;
        }

        INamedTypeSymbol owner = property.ContainingType;
        if (!IsScriptOwner(owner))
        {
            return null;
        }
        bool explicitlyExposed = HasExposeAttribute(property);
        bool implicitlyExposed = !property.IsStatic && HasScriptAssetAttribute(owner);
        if (!explicitlyExposed && !implicitlyExposed)
        {
            return null;
        }
        if (!HasModifier(propertySyntax.Modifiers, SyntaxKind.PartialKeyword))
        {
            return new PropertyCandidate(
                owner,
                property,
                propertySyntax,
                null,
                replicated: false,
                ImmutableArray.Create(Diagnostic.Create(
                    PropertyMustBePartial,
                    propertySyntax.GetLocation(),
                    property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat))));
        }
        ClassDeclarationSyntax? ownerSyntax = owner.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(declaration => declaration.Span.Contains(propertySyntax.Span));
        ownerSyntax ??= owner.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(cancellationToken))
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault();

        Location location = propertySyntax.GetLocation();
        string displayName = property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (property.IsStatic || property.IsIndexer || propertySyntax.AccessorList is null ||
            property.GetMethod is null || property.SetMethod is null ||
            property.ExplicitInterfaceImplementations.Length != 0 ||
            property.RefKind != RefKind.None ||
            !propertySyntax.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ||
            !propertySyntax.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)))
        {
            diagnostics.Add(Diagnostic.Create(PropertyShapeUnsupported, location, displayName));
        }
        for (INamedTypeSymbol? containing = owner;
             containing is not null;
             containing = containing.ContainingType)
        {
            bool hasPartialDeclaration = containing.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax(cancellationToken))
                .OfType<ClassDeclarationSyntax>()
                .Any(declaration => HasModifier(declaration.Modifiers, SyntaxKind.PartialKeyword));
            if (!hasPartialDeclaration)
            {
                diagnostics.Add(Diagnostic.Create(TypeMustBePartial, location, containing.Name));
            }
        }

        bool replicated = property.GetAttributes()
            .Where(attribute => attribute.AttributeClass?.ToDisplayString() == ExposeAttributeName)
            .SelectMany(attribute => attribute.NamedArguments)
            .Any(argument => argument.Key == "Replicated" && argument.Value.Value is true);

        return new PropertyCandidate(
            owner,
            property,
            propertySyntax,
            ownerSyntax,
            replicated,
            diagnostics.ToImmutable());
    }

    private static bool HasExposeAttribute(IPropertySymbol property) => property
        .GetAttributes()
        .Any(attribute => attribute.AttributeClass?.ToDisplayString() == ExposeAttributeName);

    private static bool HasScriptAssetAttribute(INamedTypeSymbol type) => type
        .GetAttributes()
        .Any(attribute => attribute.AttributeClass?.ToDisplayString() == ScriptAssetAttributeName);

    private static bool IsScriptOwner(INamedTypeSymbol owner)
    {
        for (INamedTypeSymbol? current = owner.BaseType;
             current is not null;
             current = current.BaseType)
        {
            if (current.ToDisplayString() == ScriptBaseName ||
                (current.Name == "UnrealObjectScript" &&
                 current.ContainingNamespace.ToDisplayString() == "Finare.Unreal"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind) =>
        modifiers.Any(modifier => modifier.IsKind(kind));

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<PropertyCandidate?> candidates)
    {
        foreach (PropertyCandidate candidate in candidates
                     .Where(static candidate => candidate is not null)
                     .Cast<PropertyCandidate>()
                     .OrderBy(static candidate => candidate.Property.ToDisplayString(), StringComparer.Ordinal))
        {
            foreach (Diagnostic diagnostic in candidate.Diagnostics)
            {
                context.ReportDiagnostic(diagnostic);
            }
        }

        foreach (var group in candidates
                     .Where(static candidate => candidate is not null && candidate.Diagnostics.IsEmpty)
                     .Cast<PropertyCandidate>()
                     .GroupBy(static candidate => candidate.Owner, SymbolEqualityComparer.Default)
                     .OrderBy(static group => group.Key!.ToDisplayString(), StringComparer.Ordinal))
        {
            INamedTypeSymbol owner = (INamedTypeSymbol)group.Key!;
            string hintName = "UnrealNativeProperties_" + StableName(owner);
            context.AddSource(hintName + ".g.cs", Render(owner, group.OrderBy(
                static candidate => candidate.Property.Name,
                StringComparer.Ordinal)));
        }
    }

    private static string Render(
        INamedTypeSymbol owner,
        IEnumerable<PropertyCandidate> properties)
    {
        var source = new StringBuilder();
        source.AppendLine("// <auto-generated />");
        source.AppendLine("#nullable enable");
        source.AppendLine();
        string namespaceName = owner.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : owner.ContainingNamespace.ToDisplayString();
        if (namespaceName.Length != 0)
        {
            source.Append("namespace ").Append(namespaceName).AppendLine(";");
            source.AppendLine();
        }

        var typeChain = new Stack<INamedTypeSymbol>();
        for (INamedTypeSymbol? current = owner; current is not null; current = current.ContainingType)
        {
            typeChain.Push(current);
        }

        int nesting = 0;
        foreach (INamedTypeSymbol type in typeChain)
        {
            ClassDeclarationSyntax declaration = GetClassDeclaration(type);
            AppendIndent(source, nesting);
            AppendTypeDeclaration(source, type, declaration);
            source.AppendLine();
            AppendIndent(source, nesting);
            source.AppendLine("{");
            nesting++;
        }
        foreach (PropertyCandidate candidate in properties)
        {
            string indent = new(' ', nesting * 4);
            string propertyAccessibility = AccessibilityText(candidate.Property.DeclaredAccessibility);
            string propertyModifiers = string.Join(
                " ",
                candidate.PropertySyntax.Modifiers
                    .Where(modifier => !modifier.IsKind(SyntaxKind.PartialKeyword) &&
                        !modifier.IsKind(SyntaxKind.PublicKeyword) &&
                        !modifier.IsKind(SyntaxKind.PrivateKeyword) &&
                        !modifier.IsKind(SyntaxKind.ProtectedKeyword) &&
                        !modifier.IsKind(SyntaxKind.InternalKeyword))
                    .Select(modifier => modifier.ValueText));
            string propertyType = candidate.Property.Type.ToDisplayString(TypeFormat);
            string getterModifiers = AccessorModifiers(candidate.PropertySyntax, SyntaxKind.GetAccessorDeclaration);
            string setterModifiers = AccessorModifiers(candidate.PropertySyntax, SyntaxKind.SetAccessorDeclaration);
            string propertyName = candidate.PropertySyntax.Identifier.Text;
            source.Append(indent).Append("[global::Finare.Unreal.UnrealNativePropertyAttribute(Replicated = ")
                .Append(candidate.Replicated ? "true" : "false")
                .AppendLine(")] ");
            source.Append(indent)
                .Append(propertyAccessibility.Length == 0 ? string.Empty : propertyAccessibility + " ")
                .Append(propertyModifiers.Length == 0 ? string.Empty : propertyModifiers + " ")
                .Append("partial ")
                .Append(propertyType)
                .Append(' ')
                .Append(propertyName)
                .AppendLine();
            source.Append(indent).AppendLine("{");
            string memberIndent = new(' ', (nesting + 1) * 4);
            source.Append(memberIndent).Append(getterModifiers).Append("get => IsReflectedPropertyAccessActive ? ReadReflectedProperty<")
                .Append(propertyType)
                .Append(">(")
                .Append("nameof(")
                .Append(propertyName)
                .AppendLine(")) : field;");
            source.Append(memberIndent).Append(setterModifiers).AppendLine("set");
            source.Append(memberIndent).AppendLine("{");
            source.Append(new string(' ', (nesting + 2) * 4)).Append("if (IsReflectedPropertyAccessActive) WriteReflectedProperty(")
                .Append("nameof(")
                .Append(propertyName)
                .Append("), value, replicated: ")
                .Append(candidate.Replicated ? "true" : "false")
                .AppendLine(");");
            source.Append(new string(' ', (nesting + 2) * 4)).AppendLine("else field = value;");
            source.Append(memberIndent).AppendLine("}");
            source.Append(indent).AppendLine("}");
        }
        for (int index = nesting - 1; index >= 0; index--)
        {
            AppendIndent(source, index);
            source.AppendLine("}");
        }
        return source.ToString();
    }

    private static ClassDeclarationSyntax GetClassDeclaration(INamedTypeSymbol type) => type
        .DeclaringSyntaxReferences
        .Select(reference => reference.GetSyntax())
        .OfType<ClassDeclarationSyntax>()
        .First();

    private static void AppendTypeDeclaration(
        StringBuilder source,
        INamedTypeSymbol type,
        ClassDeclarationSyntax declaration)
    {
        string accessibility = AccessibilityText(type.DeclaredAccessibility);
        string classModifiers = string.Join(
            " ",
            declaration.Modifiers
                .Where(modifier => !modifier.IsKind(SyntaxKind.PartialKeyword) &&
                    !modifier.IsKind(SyntaxKind.PublicKeyword) &&
                    !modifier.IsKind(SyntaxKind.PrivateKeyword) &&
                    !modifier.IsKind(SyntaxKind.ProtectedKeyword) &&
                    !modifier.IsKind(SyntaxKind.InternalKeyword))
                .Select(modifier => modifier.ValueText));
        string typeParameters = type.TypeParameters.Length == 0
            ? string.Empty
            : "<" + string.Join(", ", type.TypeParameters.Select(parameter => parameter.Name)) + ">";
        source.Append(accessibility.Length == 0 ? string.Empty : accessibility + " ")
            .Append(classModifiers.Length == 0 ? string.Empty : classModifiers + " ")
            .Append("partial class ")
            .Append(declaration.Identifier.Text)
            .Append(typeParameters);
        foreach (TypeParameterConstraintClauseSyntax constraint in declaration.ConstraintClauses)
        {
            source.Append(' ').Append(constraint.ToFullString().Trim());
        }
    }

    private static void AppendIndent(StringBuilder source, int nesting) =>
        source.Append(new string(' ', nesting * 4));

    private static string AccessorModifiers(
        PropertyDeclarationSyntax property,
        SyntaxKind accessorKind)
    {
        AccessorDeclarationSyntax? accessor = property.AccessorList?.Accessors
            .FirstOrDefault(candidate => candidate.IsKind(accessorKind));
        return accessor is null || accessor.Modifiers.Count == 0
            ? string.Empty
            : string.Join(" ", accessor.Modifiers.Select(modifier => modifier.ValueText)) + " ";
    }

    private static string AccessibilityText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        Accessibility.Private => "private",
        _ => string.Empty,
    };

    private static string StableName(INamedTypeSymbol symbol)
    {
        string value = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        uint hash = 2166136261;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class PropertyCandidate
    {
        public PropertyCandidate(
            INamedTypeSymbol owner,
            IPropertySymbol property,
            PropertyDeclarationSyntax propertySyntax,
            ClassDeclarationSyntax? ownerSyntax,
            bool replicated,
            ImmutableArray<Diagnostic> diagnostics)
        {
            Owner = owner;
            Property = property;
            PropertySyntax = propertySyntax;
            OwnerSyntax = ownerSyntax;
            Replicated = replicated;
            Diagnostics = diagnostics;
        }

        public INamedTypeSymbol Owner { get; }
        public IPropertySymbol Property { get; }
        public PropertyDeclarationSyntax PropertySyntax { get; }
        public ClassDeclarationSyntax? OwnerSyntax { get; }
        public bool Replicated { get; }
        public ImmutableArray<Diagnostic> Diagnostics { get; }
    }
}
