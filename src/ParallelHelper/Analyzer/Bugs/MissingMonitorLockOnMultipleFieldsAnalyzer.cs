﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using ParallelHelper.Extensions;
using ParallelHelper.Util;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ParallelHelper.Analyzer.Bugs {
  /// <summary>
  /// Analyzer that analyzes the source for classes that incorporate only a partial monitor synchornization, i.e. blocks
  /// that are missing the lock-statement when accessing a multiple fields.
  /// 
  /// <example>A class with a method that locks prior field access and another that doesn't.
  /// <code>
  /// class Sample {
  ///   private readonly object syncObject = new object();
  ///   private bool closed = false;
  ///   private int balance = 0;
  /// 
  ///   public void SetBalance(int balance) {
  ///     if(closed) {
  ///       throw new InvalidOperationException("account is closed");
  ///     }
  ///     this.balance = balance;
  ///   }
  /// 
  ///   public int GetBalance() {
  ///     lock(syncObject) {
  ///       if(closed) {
  ///         throw new InvalidOperationException("account is closed");
  ///       }
  ///       return this.balance;
  ///     }
  ///   }
  /// }
  /// </code>
  /// </example>
  /// </summary>
  [DiagnosticAnalyzer(LanguageNames.CSharp)]
  public class MissingMonitorLockOnMultipleFieldAnalyzer : DiagnosticAnalyzer {
    public const string DiagnosticId = "PH_B009";

    private const string Category = "Concurrency";

    private static readonly LocalizableString Title = "Missing Monitor Synchronization";
    private static readonly LocalizableString MessageFormat = "The access to the field '{0}' is probably missing an enclosing lock-statement for synchronization.";
    private static readonly LocalizableString Description = "";

    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
      DiagnosticId, Title, MessageFormat, Category, DiagnosticSeverity.Warning,
      isEnabledByDefault: true, description: Description, helpLinkUri: HelpLinkFactory.CreateUri(DiagnosticId)
    );

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context) {
      context.EnableConcurrentExecution();
      context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
      context.RegisterSemanticModelAction(AnalyzeSemanticModel);
    }

    private static void AnalyzeSemanticModel(SemanticModelAnalysisContext context) {
      new Analyzer(context, GetAllNonConstFields(context)).Analyze();
    }

    private static ISet<IFieldSymbol> GetAllNonConstFields(SemanticModelAnalysisContext context) {
      // TODO also exclude readonly fields?
      var semanticModel = context.SemanticModel;
      var cancellationToken = context.CancellationToken;
      return semanticModel.SyntaxTree.GetRoot(cancellationToken)
        .DescendantNodesAndSelf()
        .WithCancellation(cancellationToken)
        .OfType<FieldDeclarationSyntax>()
        .Where(declaration => !declaration.Modifiers.Any(SyntaxKind.ConstKeyword))
        .SelectMany(declaration => declaration.Declaration.Variables)
        .Select(variable => (IFieldSymbol)semanticModel.GetDeclaredSymbol(variable, cancellationToken))
        .IsNotNull()
        .ToImmutableHashSet();
    }

    private class Analyzer : FieldAccessAwareSemanticModelAnalyzerWithSyntaxWalkerBase {
      public Analyzer(SemanticModelAnalysisContext context, ISet<IFieldSymbol> declaredFields) : base(context, declaredFields) { }

      public override void Analyze() {
        base.Analyze();
        foreach(var (field, location) in GetAllFieldAccessesToReport()) {
          Context.ReportDiagnostic(Diagnostic.Create(Rule, location, field.Name));
        }
      }

      private IEnumerable<(IFieldSymbol Field, Location Location)> GetAllFieldAccessesToReport() {
        // The output is ordered to have reproducability for the unit tests.
        return GetAllFieldAccessesInsideSameScopeAccessingVariablesAccessedInsideSameLockWithAtLeastOneWriting()
          .Concat(GetAllFieldAccessesInsideSameScopeWithAtLeastOneWritingAccessingVariablesAccessedInsideSameLock())
          .Select(access => (access.Field, Location : access.Access.GetLocation()))
          .Distinct();
      }

      private ISet<FieldAccess> GetAllFieldAccessesInsideSameScopeAccessingVariablesAccessedInsideSameLockWithAtLeastOneWriting() {
        var accessesByScope = GetAllAccessesOutsideLockByScope();
        var fieldsByLockWithOneWritten = GetAllFieldsAccessesByLock()
          .Where(accesses => accesses.Any(access => access.IsWriting))
          .SelectMany(accesses => accesses.Select(access => access.Field))
          .ToImmutableHashSet();
        return accessesByScope
          .Where(accesses => accesses.Select(access => access.Field).Distinct().Count(fieldsByLockWithOneWritten.Contains) >= 2)
          .SelectMany(accesses => accesses)
          .ToImmutableHashSet();
      }

      private ISet<FieldAccess> GetAllFieldAccessesInsideSameScopeWithAtLeastOneWritingAccessingVariablesAccessedInsideSameLock() {
        var accessesByScopeWithOneWriting = GetAllAccessesOutsideLockByScope()
          .Where(accesses => accesses.Any(access => access.IsWriting))
          .ToImmutableHashSet();
        var fieldsByLock = GetAllFieldsAccessesByLock()
          .SelectMany(accesses => accesses.Select(access => access.Field))
          .ToImmutableHashSet();
        return accessesByScopeWithOneWriting
          .Where(accesses => accesses.Select(access => access.Field).Distinct().Count(fieldsByLock.Contains) >= 2)
          .SelectMany(accesses => accesses)
          .ToImmutableHashSet();
      }

      private ISet<ISet<FieldAccess>> GetAllAccessesOutsideLockByScope() {
        return FieldAccesses
          .WithCancellation(CancellationToken)
          .Where(access => !access.IsInsideLock)
          .GroupBy(access => access.EnclosingScope)
          .Where(accesses => accesses.Count() >= 2)
          .Select(accesses => (ISet<FieldAccess>)accesses.ToImmutableHashSet())
          .ToImmutableHashSet();
      }

      private ISet<ISet<FieldAccess>> GetAllFieldsAccessesByLock() {
        return FieldAccesses
          .WithCancellation(CancellationToken)
          .Where(access => access.IsInsideLock)
          .GroupBy(access => access.EnclosingLock)
          .Where(accesses => accesses.Count() >= 2)
          .Select(accesses => (ISet<FieldAccess>)accesses.ToImmutableHashSet())
          .ToImmutableHashSet();
      }
    }
  }
}
