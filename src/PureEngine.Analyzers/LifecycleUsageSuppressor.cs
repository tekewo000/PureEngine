using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace PureEngine.Analyzers;

/// <summary>Lifecycle callbacks are invoked through reflection, not ordinary C# call sites.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LifecycleUsageSuppressor : DiagnosticSuppressor
{
    private static readonly SuppressionDescriptor LifecycleUsage = new(
        "PEUSP001", "IDE0051", "PureEngine invokes attributed lifecycle methods through reflection.");

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [LifecycleUsage];

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        var start = context.Compilation.GetTypeByMetadataName("PureEngine.Core.StartAttribute");
        var update = context.Compilation.GetTypeByMetadataName("PureEngine.Core.UpdateAttribute");
        var destroy = context.Compilation.GetTypeByMetadataName("PureEngine.Core.DestroyAttribute");
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            if (diagnostic.Id != "IDE0051" || diagnostic.Location.SourceTree is not { } tree) continue;
            var declaration = tree.GetRoot(context.CancellationToken)
                .FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (declaration is null) continue;
            var method = context.GetSemanticModel(tree).GetDeclaredSymbol(declaration, context.CancellationToken);
            if (method is null) continue;
            if (method.GetAttributes().Any(attribute => attribute.AttributeClass is { } type
                && (SymbolEqualityComparer.Default.Equals(type, start)
                    || SymbolEqualityComparer.Default.Equals(type, update)
                    || SymbolEqualityComparer.Default.Equals(type, destroy))))
                context.ReportSuppression(Suppression.Create(LifecycleUsage, diagnostic));
        }
    }
}
