﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using ParallelHelper.Extensions;
using ParallelHelper.Util;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ParallelHelper.Analyzer.BestPractices {
  /// <summary>
  /// Analyzer that analyzes sources for methods that hold a a <see cref="System.Threading.CancellationToken"/> in the
  /// enclosing scope (i.e. as a class member field) but do not pass ito to all method invocations.
  /// 
  /// <example>Illustrates a class that holds a cancellation token but does not pass it further.
  /// <code>
  /// using System.Threading;
  /// 
  /// class Sample {
  ///   private readonly CancellationTokenSource cancellationToken = new CancellationTokenSource();
  /// 
  ///   public async Task DoWorkAsync1() {
  ///     await DoWorkAsync2();
  ///   }
  ///   
  ///   public Task DoWorkAsync2(CancellationToken cancellationToken = default) {
  ///     return Task.CompletedTask;
  ///   }
  /// }
  /// </code>
  /// </example>
  /// </summary>
  [DiagnosticAnalyzer(LanguageNames.CSharp)]
  public class UnusedCancellationTokenFromEnclosingScopeAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "PH_P007";

    private const string Category = "Concurrency";

    private static readonly LocalizableString Title = "Unused Cancellation Token";
    private static readonly LocalizableString MessageFormat = "At least one enclosing scope holds a cancellation token that is not passed to this method invocation.";
    private static readonly LocalizableString Description = "";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
      DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning,
      isEnabledByDefault: true, description: Description, helpLinkUri: HelpLinkFactory.CreateUri(DiagnosticId)
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    private static readonly string[] CancellationTokenTypes = {
      "System.Threading.CancellationToken",
      "System.Threading.CancellationTokenSource"
    };

    public override void Initialize(AnalysisContext context) {
      context.EnableConcurrentExecution();
      context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
      context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.ClassDeclaration);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context) {
      var (staticTokens, instanceTokens) = GetDeclaredCancellationTokensCount(context);
      new Analyzer(context, staticTokens, instanceTokens).Analyze();
    }

    private static (int StaticTokens, int InstanceTokens) GetDeclaredCancellationTokensCount(SyntaxNodeAnalysisContext context) {
      var classSymbol = context.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)context.Node);
      IEnumerable<bool> GetAllCancellationTokenStaticInfo<TSymbol>(
        Func<TSymbol, ITypeSymbol?> getType,
        Func<TSymbol, bool> isStatic
      ) where TSymbol : ISymbol {
        return classSymbol.GetAllBaseTypesAndSelf()
          .SelectMany(type => type.GetMembers())
          .WithCancellation(context.CancellationToken)
          .OfType<TSymbol>()
          .Where(field => getType(field) != null)
          .Where(field => IsCancellationTokenType(context.SemanticModel, getType(field)!))
          .Select(isStatic);
      }
      var fieldCancellationTokens = GetAllCancellationTokenStaticInfo<IFieldSymbol>(f => f.Type, f => f.IsStatic);
      var propertyCancellationTokens = GetAllCancellationTokenStaticInfo<IPropertySymbol>(p => p.Type, p => p.IsStatic);
      var cancellationTokens = fieldCancellationTokens.Concat(propertyCancellationTokens).ToArray();
      return (
        cancellationTokens.Count(isStatic => isStatic),
        cancellationTokens.Count(isStatic => !isStatic)
      );
    }

    private static bool IsCancellationTokenType(SemanticModel semanticModel, ITypeSymbol type) {
      return CancellationTokenTypes.Any(cancellationTokenType => semanticModel.IsEqualType(type, cancellationTokenType));
    }

    private class Analyzer : SyntaxNodeAnalyzerWithSyntaxWalkerBase<ClassDeclarationSyntax> {
      // TODO Do not assume that nested classes share the same scope?
      // TODO Avoid that two analyses of nested classes report the same issue twice.
      private bool EnclosingScopeHasUseableToken {
        get {
          if (_localTokensInEnclosingScope > 0 || _staticTokensInEnclosingScope > 0) {
            return true;
          }
          return _enclosingScopeIsInstance && _instanceTokensInEnclosingScope > 0;
        }
      }

      private readonly int _instanceTokensInEnclosingScope;
      private readonly int _staticTokensInEnclosingScope;
      private int _localTokensInEnclosingScope;

      private bool _enclosingScopeIsInstance;

      public Analyzer(SyntaxNodeAnalysisContext context, int staticFieldTokens, int instanceFieldTokens)
          : base(context) {
        _staticTokensInEnclosingScope = staticFieldTokens;
        _instanceTokensInEnclosingScope = instanceFieldTokens;
      }

      public override void VisitInvocationExpression(InvocationExpressionSyntax node) {
        if(EnclosingScopeHasUseableToken && InvocationMissesCancellationToken(node)) {
          Context.ReportDiagnostic(Diagnostic.Create(Rule, node.GetLocation()));
        }
        base.VisitInvocationExpression(node);
      }

      public override void VisitMethodDeclaration(MethodDeclarationSyntax node) {
        _enclosingScopeIsInstance = !HasStaticModifier(node.Modifiers);
        IncrementCancellationTokenAndVisitLocalBase(
          ReceivesCancellationToken(node.ParameterList),
          () => base.VisitMethodDeclaration(node)
        );
        _enclosingScopeIsInstance = false;
      }

      public override void VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) {
        IncrementCancellationTokenAndVisitLocalBase(
          ReceivesCancellationToken(node.ParameterList),
          () => base.VisitParenthesizedLambdaExpression(node)
        );
      }

      public override void VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) {
        IncrementCancellationTokenAndVisitLocalBase(
          IsCancellationToken(node.Parameter),
          () => base.VisitSimpleLambdaExpression(node)
        );
      }

      public override void VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) {
        IncrementCancellationTokenAndVisitLocalBase(
          ReceivesCancellationToken(node.ParameterList),
          () => base.VisitAnonymousMethodExpression(node)
        );
      }

      private bool HasStaticModifier(SyntaxTokenList modifiers) {
        return modifiers.Any(SyntaxKind.StaticKeyword);
      }

      private void IncrementCancellationTokenAndVisitLocalBase(bool receivesLocalCancellationToken, Action baseVisit) {
        if (receivesLocalCancellationToken) {
          ++_localTokensInEnclosingScope;
          baseVisit();
          --_localTokensInEnclosingScope;
        } else {
          baseVisit();
        }
      }

      private bool ReceivesCancellationToken(ParameterListSyntax? parameterList) {
        if (parameterList == null) {
          return false;
        }
        var parameters = parameterList.Parameters;
        return parameters != null && parameters.WithCancellation(CancellationToken).Any(IsCancellationToken);
      }

      private bool IsCancellationToken(ParameterSyntax parameter) {
        var type = SemanticModel.GetDeclaredSymbol(parameter, CancellationToken).Type;
        return type != null && IsCancellationTokenType(type);
      }

      private bool IsCancellationTokenType(ITypeSymbol type) {
        return UnusedCancellationTokenFromEnclosingScopeAnalyzer.IsCancellationTokenType(SemanticModel, type);
      }

      private bool InvocationMissesCancellationToken(InvocationExpressionSyntax invocation) {
        return SemanticModel.GetSymbolInfo(invocation, CancellationToken).Symbol is IMethodSymbol method
          && !InvocationUsesCancellationToken(invocation)
          && (MethodAcceptsCancellationToken(method) || MethodHasOverloadThatAcceptsCancellationToken(method));
      }

      private bool InvocationUsesCancellationToken(InvocationExpressionSyntax invocation) {
        return invocation.ArgumentList.Arguments
          .WithCancellation(CancellationToken)
          .Select(argument => SemanticModel.GetTypeInfo(argument.Expression, CancellationToken).Type)
          .IsNotNull()
          .Where(IsCancellationTokenType)
          .Any();
      }

      private bool MethodHasOverloadThatAcceptsCancellationToken(IMethodSymbol method) {
        return method.GetAllOverloads(CancellationToken).Any(MethodAcceptsCancellationToken);
      }

      private bool MethodAcceptsCancellationToken(IMethodSymbol method) {
        return method.Parameters
          .WithCancellation(CancellationToken)
          .Where(parameter => IsCancellationTokenType(parameter.Type))
          .Any();
      }
    }
  }
}
