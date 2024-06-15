// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Aspire.Hosting.Analyzers;

/// <summary>
/// 
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public partial class AppHostAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// 
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Diagnostics.SupportedDiagnostics;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="context"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public override void Initialize(AnalysisContext context)
    {
        // TODO: Don't register the analyzer if the project has disabled the analyzer in the project file.

        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(AnalyzeCompilationStart);
    }

    private void AnalyzeCompilationStart(CompilationStartAnalysisContext context)
    {
        var compilation = context.Compilation;
        var wellKnownTypes = WellKnownTypes.GetOrCreate(compilation);

        // We want ConcurrentHashSet here in case RegisterOperationAction runs in parallel.
        // Since ConcurrentHashSet doesn't exist, use ConcurrentDictionary and ignore the value.
        var concurrentQueue = new ConcurrentQueue<ConcurrentDictionary<ModelNameOperation, byte>>();
        context.RegisterOperationBlockStartAction(context =>
        {
            // Pool and reuse lists for each block.
            if (!concurrentQueue.TryDequeue(out var modelNameOperations))
            {
                modelNameOperations = new ConcurrentDictionary<ModelNameOperation, byte>();
            }

            context.RegisterOperationAction(c => DoOperationAnalysis(c, modelNameOperations), OperationKind.Invocation);

            context.RegisterOperationBlockEndAction(c =>
            {
                DetectInvalidModelNames(c, modelNameOperations);

                // Return to the pool.
                modelNameOperations.Clear();
                concurrentQueue.Enqueue(modelNameOperations);
            });
        });

        void DoOperationAnalysis(OperationAnalysisContext context, ConcurrentDictionary<ModelNameOperation, byte> modelNameOperations)
        {
            var invocation = (IInvocationOperation)context.Operation;
            var targetMethod = invocation.TargetMethod;

            if (!IsModelNameInvocation(wellKnownTypes, targetMethod, out var modelNameParameter))
            {
                return;
            }

            if (!TryGetStringToken(invocation, modelNameParameter!, out var token))
            {
                return;
            }

            // TODO: Extract the target from the attribute and use in place of "Resource".
            modelNameOperations.TryAdd(ModelNameOperation.Create(invocation, "Resource", token), value: default);
        }
    }

    private static bool TryGetStringToken(IInvocationOperation invocation, IParameterSymbol modelNameParameter, out SyntaxToken token)
    {
        IArgumentOperation? argumentOperation = null;
        foreach (var argument in invocation.Arguments)
        {
            if (SymbolEqualityComparer.Default.Equals(modelNameParameter, argument.Parameter))
            {
                argumentOperation = argument;
                break;
            }
        }

        if (argumentOperation?.Syntax is not ArgumentSyntax routePatternArgumentSyntax ||
            routePatternArgumentSyntax.Expression is not LiteralExpressionSyntax routePatternArgumentLiteralSyntax)
        {
            token = default;
            return false;
        }

        token = routePatternArgumentLiteralSyntax.Token;
        return true;
    }

    private static bool IsModelNameInvocation(WellKnownTypes wellKnownTypes, IMethodSymbol targetMethod, out IParameterSymbol? modelNameParameter)
    {
        var candidateParameter = targetMethod.Parameters.SingleOrDefault(ps =>
            SymbolEqualityComparer.Default.Equals(ps.Type, wellKnownTypes.Get(SpecialType.System_String))
            && HasModelNameAttribute(ps));

        if (candidateParameter is not null)
        {
            modelNameParameter = candidateParameter;
            return true;
        }

        modelNameParameter = null;
        return false;

        bool HasModelNameAttribute(IParameterSymbol parameter)
        {
            var modelNameAttribute = wellKnownTypes.Get(WellKnownTypeData.WellKnownType.Aspire_Hosting_ApplicationModel_ModelNameAttribute);
            var attrData = parameter.GetAttributes().SingleOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, modelNameAttribute) == true);

            return attrData is not null;
        }
    }

    private record struct ModelNameOperation(IInvocationOperation Operation, string Target, SyntaxToken ModelNameToken)
    {
        public static ModelNameOperation Create(IInvocationOperation operation, string target, SyntaxToken modelNameToken)
        {
            return new ModelNameOperation(operation, target, modelNameToken);
        }
    }
}