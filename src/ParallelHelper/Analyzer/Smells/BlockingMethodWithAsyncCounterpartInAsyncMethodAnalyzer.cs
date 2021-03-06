﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using ParallelHelper.Extensions;
using ParallelHelper.Util;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ParallelHelper.Analyzer.Smells {
  /// <summary>
  /// Analyzer that analyzes sources for the use of blocking methods that do have async counterparts inside async methods.
  /// 
  /// <example>Illustrates a class with an async method that uses a blocking method that does have an async counterpart.
  /// <code>
  /// class Sample {
  ///   public async Task DoWorkAsync() {
  ///     using(var client = new TcpClient())
  ///     using(var reader = new StreamReader(client.GetStream()) {
  ///       var buffer = new char[1024];
  ///       reader.Read(buffer, 0, buffer.Length);
  ///     }
  ///   }
  /// }
  /// </code>
  /// </example>
  /// </summary>
  [DiagnosticAnalyzer(LanguageNames.CSharp)]
  public class BlockingMethodWithAsyncCounterpartInAsyncMethodAnalyzer : DiagnosticAnalyzer {
    // TODO include methods that end with Async? Since methods could be implemented synchronously
    //      because the async counterpart is not known.
    public const string DiagnosticId = "PH_S019";

    private const string Category = "Concurrency";

    private const string AsyncSuffix = "Async";

    private static readonly LocalizableString Title = "Blocking Method in Async Method";
    private static readonly LocalizableString MessageFormat = "The blocking method '{0}' is used inside an async method, although it appears to have an async counterpart '{1}'.";
    private static readonly LocalizableString Description = "";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
      DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning,
      isEnabledByDefault: true, description: Description, helpLinkUri: HelpLinkFactory.CreateUri(DiagnosticId)
    );

    private static readonly string[] TaskTypes = {
      "System.Threading.Tasks.Task",
      "System.Threading.Tasks.Task`1"
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
      context.EnableConcurrentExecution();
      context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
      context.RegisterSyntaxNodeAction(AnalyzeAsyncCandidate, SyntaxKind.MethodDeclaration, SyntaxKind.AnonymousMethodExpression,
        SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression);
    }

    private static void AnalyzeAsyncCandidate(SyntaxNodeAnalysisContext context) {
      new Analyzer(context).Analyze();
    }

    private class Analyzer : SyntaxNodeAnalyzerBase<SyntaxNode> {
      private bool IsAsyncMethod => Node is MethodDeclarationSyntax method 
        && method.Modifiers.Any(SyntaxKind.AsyncKeyword);
      private bool IsAsyncAnonymousFunction => Node is AnonymousFunctionExpressionSyntax function 
        && function.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);

      public Analyzer(SyntaxNodeAnalysisContext context) : base(context) { }


      public override void Analyze() {
        if(!IsAsyncMethod && !IsAsyncAnonymousFunction) {
          return;
        }
        foreach(var invocation in GetAllInvocations()) {
          AnalyzeInvocation(invocation);
        }
      }

      private IEnumerable<InvocationExpressionSyntax> GetAllInvocations() {
        return Node.DescendantNodes()
          .WithCancellation(CancellationToken)
          .OfType<InvocationExpressionSyntax>();
      }

      private void AnalyzeInvocation(InvocationExpressionSyntax invocation) {
        if(SemanticModel.GetSymbolInfo(invocation, CancellationToken).Symbol is IMethodSymbol method 
            && !IsPotentiallyAsyncMethod(method) && TryGetAsyncCounterpart(method, out var asyncName)) {
          Context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), method.Name, asyncName));
        }
      }

      private bool IsPotentiallyAsyncMethod(IMethodSymbol method) {
        return method.IsAsync || method.Name.EndsWith(AsyncSuffix) || IsTaskType(method.ReturnType);
      }

      private bool IsTaskType(ITypeSymbol type) {
        return TaskTypes.Any(t => SemanticModel.IsEqualType(type, t));
      }

      private static bool TryGetAsyncCounterpart(IMethodSymbol method, out string asyncName) {
        var candidateName = method.Name + AsyncSuffix;
        if(method.ContainingType.MemberNames.Any(candidateName.Equals)) {
          asyncName = candidateName;
          return true;
        }
        asyncName = "";
        return false;
      }
    }
  }
}
