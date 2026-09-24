#!/usr/bin/dotnet run
#:package Microsoft.CodeAnalysis.CSharp@5.0.0

// Guard-modernization rewriter: hand-written `if (...) throw new SomeException(...);`
// guards -> the .NET 6+ static ThrowIf* helpers (ArgumentNullException.ThrowIfNull,
// ArgumentException.ThrowIfNullOrEmpty, ArgumentOutOfRangeException.ThrowIfZero/
// ThrowIfNegative/ThrowIfNegativeOrZero/ThrowIfGreaterThan/ThrowIfGreaterThanOrEqual/
// ThrowIfLessThan/ThrowIfLessThanOrEqual/ThrowIfEqual/ThrowIfNotEqual,
// ObjectDisposedException.ThrowIf).
//
// This is a STANDALONE rewriter. It does not require CA1510-CA1513 to be enabled,
// does not run `dotnet format analyzers`, and does not need a code-fix host. It
// parses the tree itself (via a throwaway CSharpCompilation, same as every other
// `roslyn-rewriters`-style tool here), matches the same shapes the analyzer's
// operation-based matchers match (re-implemented against IOperation, since that
// API is public and works fine on a plain CSharpCompilation), and edits the syntax
// tree itself with a CSharpSyntaxRewriter, exactly like the real fixer's
// `ApplyFix` does with a `SyntaxEditor` — same output shape, different host.
//
// Ported from (read, not guessed — see the citation above each matcher below):
//   ~/source/dotnet/sdk/src/Microsoft.CodeAnalysis.NetAnalyzers/src/Microsoft.CodeAnalysis.NetAnalyzers/Microsoft.NetCore.Analyzers/Runtime/UseExceptionThrowHelpers.cs        (analyzer)
//   ~/source/dotnet/sdk/src/Microsoft.CodeAnalysis.NetAnalyzers/src/Microsoft.CodeAnalysis.NetAnalyzers/Microsoft.NetCore.Analyzers/Runtime/UseExceptionThrowHelpersFixer.cs  (fixer)
//
// See SKILL.md for the full per-rule table (matches -> produces), the deliberate
// skips (and why), and the EXTENSIONS beyond the stock fixer (marked "EXTENSION"
// in the code below) plus the one deliberate DIVERGENCE (paramName-mismatch
// reporting — the stock fixer silently discards a mismatched literal paramName;
// this tool reports it instead of rewriting, see "DIVERGENCE" below).
//
// Run (dry run, default):   dotnet run modernize-guards.cs -- /path/to/src
// Apply:                    dotnet run modernize-guards.cs -- /path/to/src --write
// Polyfill/alias mode (netstandard2.0/net4x, see SKILL.md "Polyfill mode"):
//                            dotnet run modernize-guards.cs -- /path/to/src --helper-type ArgumentExceptionHelper --write

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: dotnet run modernize-guards.cs -- <src-folder> [--write] [--helper-type <Name>]");
    return 1;
}

var srcFolder = args[0];
var write = args.Contains("--write");
string? helperTypeName = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--helper-type")
    {
        helperTypeName = args[i + 1];
    }
}

var paths = EnumerateSource(srcFolder).ToList();
var trees = new List<(string Path, SyntaxTree Tree)>();
foreach (var path in paths)
{
    var text = await File.ReadAllTextAsync(path);
    trees.Add((path, CSharpSyntaxTree.ParseText(text, path: path)));
}

var compilation = CSharpCompilation.Create(
    "GuardModernizeScan",
    trees.Select(t => t.Tree),
    GetReferences(),
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

var types = GuardTypes.Resolve(compilation, helperTypeName);
if (types is null)
{
    Console.WriteLine("Target compilation is missing one or more of ArgumentNullException/ArgumentException/ArgumentOutOfRangeException/ObjectDisposedException (or, in default mode, none of their ThrowIf* helpers) — nothing to rewrite. Pass --helper-type <Name> to target a polyfill/alias type instead (see SKILL.md, 'Polyfill mode').");
    return 0;
}

var changedFiles = 0;
var totalRewrites = 0;
var totalReports = 0;

foreach (var (path, tree) in trees)
{
    var model = compilation.GetSemanticModel(tree);
    var root = await tree.GetRootAsync();

    var rewriter = new GuardRewriter(model, types, path);
    var newRoot = rewriter.Visit(root);

    foreach (var finding in rewriter.Findings)
    {
        Console.WriteLine(finding);
    }

    totalRewrites += rewriter.Rewrites;
    totalReports += rewriter.Reports;

    if (newRoot == root)
    {
        continue;
    }

    changedFiles++;
    if (write)
    {
        await File.WriteAllTextAsync(path, newRoot!.ToFullString());
        Console.WriteLine($"rewrote {path}");
    }
    else
    {
        Console.WriteLine($"would rewrite {path}");
    }
}

Console.WriteLine();
Console.WriteLine($"{changedFiles} file(s) {(write ? "rewritten" : "would change")}.");
Console.WriteLine($"{totalRewrites} guard(s) modernized, {totalReports} finding(s) reported but not rewritten (see reasons above).");
if (!write && totalRewrites > 0)
{
    Console.WriteLine("re-run with --write to apply, then build, run tests, and re-run this tool to confirm idempotency (see SKILL.md).");
}

return 0;

static IEnumerable<string> EnumerateSource(string root) =>
    Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                    !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

static IEnumerable<MetadataReference> GetReferences()
{
    var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
        ?? throw new InvalidOperationException("no trusted platform assemblies — run on the .NET SDK, not AOT.");
    return tpa.Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
}

// ===========================================================================
// Resolved well-known types/members for the target compilation. Ported from
// UseExceptionThrowHelpers.Initialize's compilation-start setup, lines 92-145:
// it resolves ArgumentNullException/ArgumentOutOfRangeException/ArgumentException/
// ObjectDisposedException, bails if any is missing (they've always existed, but
// a from-scratch test compilation might omit one), resolves string.IsNullOrEmpty/
// Length/Empty and object.GetType (needed for the CA1511/CA1513 shapes), then
// resolves each ThrowIf* helper individually and bails only if NONE of them
// exist on the target (an old TFM) — "no helpers, nothing to suggest".
//
// EXTENSION (--helper-type): when helperTypeName is set, the "does the BCL
// helper exist on this compilation" gate is bypassed entirely — the whole point
// is to emit calls to a polyfill/alias type on a TFM where the analyzer would
// never fire because the BCL helpers genuinely don't exist there (see SKILL.md,
// "Polyfill mode"). All nine ArgumentOutOfRangeException members and both of
// ArgumentException/ArgumentNullException/ObjectDisposedException's members are
// then treated as "available" under that one alias name; the convention (method
// names/parameter order identical to the BCL) matches `csharp-guard-clauses`'
// `ArgumentExceptionHelper`/`ArgumentValidation` polyfill.
sealed class GuardTypes
{
    public required Compilation Compilation { get; init; }
    public required INamedTypeSymbol ArgumentNullExceptionType { get; init; }
    public required INamedTypeSymbol ArgumentExceptionType { get; init; }
    public required INamedTypeSymbol ArgumentOutOfRangeExceptionType { get; init; }
    public required INamedTypeSymbol ObjectDisposedExceptionType { get; init; }
    public required ISymbol StringIsNullOrEmpty { get; init; }
    public required ISymbol StringLength { get; init; }
    public required ISymbol StringEmpty { get; init; }
    public required ISymbol ObjectGetType { get; init; }

    // Null when helperTypeName is set: emitted invocations then target the alias
    // type by name (unqualified `HelperTypeName.Method(...)`, relying on a
    // `<Using Alias>` the target project supplies) instead of a fully-qualified
    // BCL exception type name.
    public string? HelperTypeName { get; init; }

    public bool HasThrowIfNull { get; init; }
    public bool HasThrowIfNullOrEmpty { get; init; }
    public bool HasThrowIf { get; init; } // ObjectDisposedException.ThrowIf
    public bool HasThrowIfZero { get; init; }
    public bool HasThrowIfNegative { get; init; }
    public bool HasThrowIfNegativeOrZero { get; init; }
    public bool HasThrowIfGreaterThan { get; init; }
    public bool HasThrowIfGreaterThanOrEqual { get; init; }
    public bool HasThrowIfLessThan { get; init; }
    public bool HasThrowIfLessThanOrEqual { get; init; }
    public bool HasThrowIfEqual { get; init; }
    public bool HasThrowIfNotEqual { get; init; }

    public static GuardTypes? Resolve(Compilation compilation, string? helperTypeName)
    {
        var ane = compilation.GetTypeByMetadataName("System.ArgumentNullException");
        var aoore = compilation.GetTypeByMetadataName("System.ArgumentOutOfRangeException");
        var ae = compilation.GetTypeByMetadataName("System.ArgumentException");
        var ode = compilation.GetTypeByMetadataName("System.ObjectDisposedException");
        if (ane is null || aoore is null || ae is null || ode is null)
        {
            return null;
        }

        var stringType = compilation.GetSpecialType(SpecialType.System_String);
        var objectType = compilation.GetSpecialType(SpecialType.System_Object);
        var stringIsNullOrEmpty = stringType.GetMembers("IsNullOrEmpty").FirstOrDefault();
        var stringLength = stringType.GetMembers("Length").FirstOrDefault();
        var stringEmpty = stringType.GetMembers("Empty").FirstOrDefault();
        var getType = objectType.GetMembers("GetType").FirstOrDefault();
        if (stringIsNullOrEmpty is null || stringLength is null || stringEmpty is null || getType is null)
        {
            return null;
        }

        bool polyfill = helperTypeName is not null;

        bool hasThrowIfNull = polyfill || ane.GetMembers("ThrowIfNull").Any();
        bool hasThrowIfNullOrEmpty = polyfill || ae.GetMembers("ThrowIfNullOrEmpty").Any();
        bool hasThrowIf = polyfill || ode.GetMembers("ThrowIf").Any();
        bool hasThrowIfZero = polyfill || aoore.GetMembers("ThrowIfZero").Any();
        bool hasThrowIfNegative = polyfill || aoore.GetMembers("ThrowIfNegative").Any();
        bool hasThrowIfNegativeOrZero = polyfill || aoore.GetMembers("ThrowIfNegativeOrZero").Any();
        bool hasThrowIfGreaterThan = polyfill || aoore.GetMembers("ThrowIfGreaterThan").Any();
        bool hasThrowIfGreaterThanOrEqual = polyfill || aoore.GetMembers("ThrowIfGreaterThanOrEqual").Any();
        bool hasThrowIfLessThan = polyfill || aoore.GetMembers("ThrowIfLessThan").Any();
        bool hasThrowIfLessThanOrEqual = polyfill || aoore.GetMembers("ThrowIfLessThanOrEqual").Any();
        bool hasThrowIfEqual = polyfill || aoore.GetMembers("ThrowIfEqual").Any();
        bool hasThrowIfNotEqual = polyfill || aoore.GetMembers("ThrowIfNotEqual").Any();

        if (!polyfill && !(hasThrowIfNull || hasThrowIfNullOrEmpty || hasThrowIf ||
            hasThrowIfZero || hasThrowIfNegative || hasThrowIfNegativeOrZero ||
            hasThrowIfGreaterThan || hasThrowIfGreaterThanOrEqual ||
            hasThrowIfLessThan || hasThrowIfLessThanOrEqual || hasThrowIfEqual || hasThrowIfNotEqual))
        {
            return null;
        }

        return new GuardTypes
        {
            Compilation = compilation,
            ArgumentNullExceptionType = ane,
            ArgumentExceptionType = ae,
            ArgumentOutOfRangeExceptionType = aoore,
            ObjectDisposedExceptionType = ode,
            StringIsNullOrEmpty = stringIsNullOrEmpty,
            StringLength = stringLength,
            StringEmpty = stringEmpty,
            ObjectGetType = getType,
            HelperTypeName = helperTypeName,
            HasThrowIfNull = hasThrowIfNull,
            HasThrowIfNullOrEmpty = hasThrowIfNullOrEmpty,
            HasThrowIf = hasThrowIf,
            HasThrowIfZero = hasThrowIfZero,
            HasThrowIfNegative = hasThrowIfNegative,
            HasThrowIfNegativeOrZero = hasThrowIfNegativeOrZero,
            HasThrowIfGreaterThan = hasThrowIfGreaterThan,
            HasThrowIfGreaterThanOrEqual = hasThrowIfGreaterThanOrEqual,
            HasThrowIfLessThan = hasThrowIfLessThan,
            HasThrowIfLessThanOrEqual = hasThrowIfLessThanOrEqual,
            HasThrowIfEqual = hasThrowIfEqual,
            HasThrowIfNotEqual = hasThrowIfNotEqual,
        };
    }

}

// ===========================================================================
// Top-level rewriter. Walks every IfStatementSyntax. On a match it replaces the
// WHOLE if-statement with an ExpressionStatement invoking the ThrowIf* helper —
// same as the fixer's ApplyFix (UseExceptionThrowHelpersFixer.cs, lines 108-123)
// which does `editor.ReplaceNode(node, ExpressionStatement(Invocation(...)))`
// where `node` is the if-statement found from the diagnostic's primary location
// (which the analyzer set to the whole conditional — `condition.CreateDiagnostic`,
// UseExceptionThrowHelpers.cs lines 194, 211, 259, 304).
//
// On a non-match it recurses into children via base.VisitIfStatement so nested
// if-statements (an `else if`, or an if inside another if's block) still get a
// chance to match independently — the analyzer's own OperationAction fires once
// per IThrowOperation wherever it is, so nesting is not a barrier there either.
sealed class GuardRewriter(SemanticModel model, GuardTypes types, string path) : CSharpSyntaxRewriter
{
    public List<string> Findings { get; } = [];
    public int Rewrites { get; private set; }
    public int Reports { get; private set; }

    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var (replacement, finding) = TryMatch(node);
        if (finding is not null)
        {
            Findings.Add(finding);
        }

        if (replacement is not null)
        {
            Rewrites++;
            return replacement.WithLeadingTrivia(node.GetLeadingTrivia()).WithTrailingTrivia(node.GetTrailingTrivia());
        }

        if (finding is not null)
        {
            Reports++;
        }

        return base.VisitIfStatement(node);
    }

    private (StatementSyntax? Replacement, string? Finding) TryMatch(IfStatementSyntax node)
    {
        // "If the condition has an else block, give up. These are rare."
        // (UseExceptionThrowHelpers.cs, line 178-182)
        if (node.Else is not null)
        {
            return (null, null);
        }

        if (model.GetOperation(node) is not IConditionalOperation condition)
        {
            return (null, null);
        }

        if (!TryGetSingleThrow(condition, out var throwOperation))
        {
            return (null, null);
        }

        // "Try to get the exception object creation operation. As a heuristic,
        // avoid recommending replacing any exceptions where a meaningful message
        // may have been provided." (UseExceptionThrowHelpers.cs, lines 152-160)
        if (Matchers.GetThrownException(throwOperation) is not IObjectCreationOperation creation ||
            Matchers.HasPossiblyMeaningfulAdditionalArguments(creation, types.ArgumentExceptionType))
        {
            return (null, null);
        }

        var exceptionType = creation.Type as INamedTypeSymbol;

        if (SymbolEqualityComparer.Default.Equals(exceptionType, types.ArgumentNullExceptionType))
        {
            return MatchArgumentNull(node, condition, creation);
        }

        if (SymbolEqualityComparer.Default.Equals(exceptionType, types.ArgumentExceptionType))
        {
            return MatchArgumentException(node, condition, creation);
        }

        if (SymbolEqualityComparer.Default.Equals(exceptionType, types.ArgumentOutOfRangeExceptionType))
        {
            return MatchArgumentOutOfRange(node, condition, creation);
        }

        if (SymbolEqualityComparer.Default.Equals(exceptionType, types.ObjectDisposedExceptionType))
        {
            return MatchObjectDisposed(node, condition, creation);
        }

        return (null, null);
    }

    // Ported from UseExceptionThrowHelpers.cs lines 162-176: the throw's parent
    // must be an IConditionalOperation, either directly (`if (x) throw ...;`) or
    // via a single-statement block (`if (x) { throw ...; }`). Re-expressed here
    // starting from the conditional and looking at WhenTrue, which is equivalent.
    private static bool TryGetSingleThrow(IConditionalOperation condition, out IThrowOperation throwOperation)
    {
        switch (condition.WhenTrue)
        {
            case IThrowOperation t:
                throwOperation = t;
                return true;
            case IBlockOperation { Operations: [IThrowOperation t2] }:
                throwOperation = t2;
                return true;
            default:
                throwOperation = null!;
                return false;
        }
    }

    // CA1510: ArgumentNullException.ThrowIfNull
    // Matcher ported from UseExceptionThrowHelpers.cs lines 186-202 (dispatch) and
    // IsParameterNullCheck, lines 365-391.
    private (StatementSyntax?, string?) MatchArgumentNull(IfStatementSyntax node, IConditionalOperation condition, IObjectCreationOperation creation)
    {
        if (!types.HasThrowIfNull)
        {
            return (null, null);
        }

        if (!Matchers.IsParameterNullCheck(condition.Condition, out var parameter) ||
            !parameter.Type!.IsReferenceType)
        {
            return (null, null);
        }

        return BuildOrReport(node, types.ArgumentNullExceptionType, "ThrowIfNull", parameter.Parameter!, creation, paramNameArgIndex: 0, otherSyntax: null);
    }

    // CA1511: ArgumentException.ThrowIfNullOrEmpty
    // Matcher ported from UseExceptionThrowHelpers.cs lines 205-219 (dispatch) and
    // IsNullOrEmptyCheck/IsArgLengthEqual0/IsArgEqualStringEmpty, lines 393-449.
    private (StatementSyntax?, string?) MatchArgumentException(IfStatementSyntax node, IConditionalOperation condition, IObjectCreationOperation creation)
    {
        if (!types.HasThrowIfNullOrEmpty)
        {
            return (null, null);
        }

        if (!Matchers.IsNullOrEmptyCheck(types.StringIsNullOrEmpty, types.StringLength, types.StringEmpty, condition.Condition, out var parameter))
        {
            return (null, null);
        }

        // paramName is the SECOND constructor argument for ArgumentException
        // (message is first) — UseExceptionThrowHelpers.cs line 209.
        return BuildOrReport(node, types.ArgumentExceptionType, "ThrowIfNullOrEmpty", parameter.Parameter!, creation, paramNameArgIndex: 1, otherSyntax: null);
    }

    // CA1512: ArgumentOutOfRangeException.ThrowIfZero/ThrowIfNegative/
    // ThrowIfNegativeOrZero/ThrowIfGreaterThan/ThrowIfGreaterThanOrEqual/
    // ThrowIfLessThan/ThrowIfLessThanOrEqual/ThrowIfEqual/ThrowIfNotEqual.
    // Matcher ported from UseExceptionThrowHelpers.cs lines 230-274 (dispatch),
    // IsNegativeAndOrZeroComparison (lines 452-525) and
    // IsGreaterLessEqualThanComparison (lines 527-594).
    private (StatementSyntax?, string?) MatchArgumentOutOfRange(IfStatementSyntax node, IConditionalOperation condition, IObjectCreationOperation creation)
    {
        SyntaxNode? otherSyntax = null;
        string? methodName;
        IParameterReferenceOperation? parameter;

        if (Matchers.IsNegativeAndOrZeroComparison(condition.Condition, out parameter, out methodName))
        {
            // ThrowIfZero/ThrowIfNegative/ThrowIfNegativeOrZero take one argument.
        }
        else if (Matchers.IsGreaterLessEqualThanComparison(condition.Condition, out parameter, out methodName, out var other))
        {
            otherSyntax = other;
        }
        else
        {
            // EXTENSION beyond the stock fixer: `if (i < 0 || i >= count) throw
            // new ArgumentOutOfRangeException(...)` -> two ThrowIf* calls, only
            // when each side of the `||` independently matches one of the two
            // matchers above against the SAME parameter, and every operand
            // involved is provably side-effect-free. See SKILL.md, "Extensions".
            return TryMatchCompoundRange(node, condition, creation);
        }

        if (parameter is null || methodName is null)
        {
            return (null, null);
        }

        // "AvoidComparing": skip nullable value types and enums — comparing
        // those against a bare `T` ThrowIf* overload isn't meaningful/available
        // the same way. UseExceptionThrowHelpers.cs lines 268-270.
        if (Matchers.IsNullableValueType(parameter.Type!) || parameter.Type?.TypeKind == TypeKind.Enum)
        {
            return (null, null);
        }

        if (!HasHelperFor(methodName))
        {
            return (null, null);
        }

        return BuildOrReport(node, types.ArgumentOutOfRangeExceptionType, methodName, parameter.Parameter!, creation, paramNameArgIndex: 0, otherSyntax);
    }

    private bool HasHelperFor(string methodName) => methodName switch
    {
        "ThrowIfZero" => types.HasThrowIfZero,
        "ThrowIfNegative" => types.HasThrowIfNegative,
        "ThrowIfNegativeOrZero" => types.HasThrowIfNegativeOrZero,
        "ThrowIfGreaterThan" => types.HasThrowIfGreaterThan,
        "ThrowIfGreaterThanOrEqual" => types.HasThrowIfGreaterThanOrEqual,
        "ThrowIfLessThan" => types.HasThrowIfLessThan,
        "ThrowIfLessThanOrEqual" => types.HasThrowIfLessThanOrEqual,
        "ThrowIfEqual" => types.HasThrowIfEqual,
        "ThrowIfNotEqual" => types.HasThrowIfNotEqual,
        _ => false,
    };

    // CA1513: ObjectDisposedException.ThrowIf
    // Matcher ported from UseExceptionThrowHelpers.cs lines 276-312. Unlike the
    // other three rules, the analyzer ALWAYS reports here (any boolean condition
    // guarding an ObjectDisposedException throw, reference-type containing type)
    // but the FIXER only rewrites when it can find the `X.GetType().Name`/
    // `.FullName` shape as the first constructor argument (or an implicit
    // `this.GetType()...`) to use as the ThrowIf instance argument — everything
    // else is diagnostic-only in the real tool too (UseExceptionThrowHelpersFixer.cs
    // lines 96-101: the switch case requires `other is not null`).
    private (StatementSyntax?, string?) MatchObjectDisposed(IfStatementSyntax node, IConditionalOperation condition, IObjectCreationOperation creation)
    {
        var containingType = GetContainingTypeSymbol(node);
        if (containingType is null || !containingType.IsReferenceType)
        {
            return (null, null);
        }

        if (!types.HasThrowIf)
        {
            return (null, null);
        }

        SyntaxNode? instanceSyntax = Matchers.TryGetObjectDisposedInstance(creation, types.ObjectGetType, out var isImplicitThis);

        var line = Line(node);
        if (instanceSyntax is null && !isImplicitThis)
        {
            return (null, $"{path}:{line}  ObjectDisposedException guard found, but could not determine the instance argument (expected `X.GetType().Name` or `X.GetType().FullName` as the first constructor argument) -> leaving as-is; add manually: ObjectDisposedException.ThrowIf(<condition>, <instance>);");
        }

        var instanceArg = isImplicitThis ? (ExpressionSyntax)SyntaxFactory.ThisExpression() : (ExpressionSyntax)instanceSyntax!;
        var (typeName, warning) = ResolveInvocationTarget(node.SpanStart, types.ObjectDisposedExceptionType);
        var stmt = BuildInvocationStatement(typeName, "ThrowIf", [(ExpressionSyntax)condition.Condition.Syntax, instanceArg]);
        var suffix = warning is null ? "" : $" ({warning})";
        return (stmt, $"{path}:{line}  converted ObjectDisposedException guard to {typeName}.ThrowIf{suffix}");
    }

    // EXTENSION: `if (i < 0 || i >= count) throw new ArgumentOutOfRangeException(...)`
    // -> two ThrowIf* statements. Both `||` operands must independently match one
    // of the two CA1512 shapes against the SAME parameter symbol, and both the
    // parameter's own uses and every "other" bound must be side-effect-free
    // (no invocations, no assignments, no `await`, no object creation) — a
    // shared parameter appearing in both operands already gets re-evaluated
    // twice by the ORIGINAL `||` when the left side is false, so splitting to
    // two statements changes nothing about how many times a given expression
    // executes as long as none of them has a side effect to begin with.
    private (StatementSyntax?, string?) TryMatchCompoundRange(IfStatementSyntax node, IConditionalOperation condition, IObjectCreationOperation creation)
    {
        if (condition.Condition is not IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } orOp)
        {
            return (null, null);
        }

        if (!Matchers.TryMatchRangeOperand(orOp.LeftOperand, out var leftParam, out var leftMethod, out var leftOther) ||
            !Matchers.TryMatchRangeOperand(orOp.RightOperand, out var rightParam, out var rightMethod, out var rightOther))
        {
            return (null, null);
        }

        if (!SymbolEqualityComparer.Default.Equals(leftParam.Parameter, rightParam.Parameter))
        {
            return (null, null);
        }

        if (!HasHelperFor(leftMethod) || !HasHelperFor(rightMethod))
        {
            return (null, null);
        }

        if (!Matchers.IsSideEffectFree(orOp.LeftOperand.Syntax) || !Matchers.IsSideEffectFree(orOp.RightOperand.Syntax))
        {
            var line0 = Line(node);
            return (null, $"{path}:{line0}  compound ArgumentOutOfRangeException guard `{orOp.Syntax}` matches two ThrowIf* shapes but an operand isn't provably side-effect-free -> leaving as-is (see SKILL.md, Extensions)");
        }

        var (ok, finding) = CheckParamName(creation, 0, leftParam.Parameter!);
        if (!ok)
        {
            var line1 = Line(node);
            return (null, $"{path}:{line1}  {finding}");
        }

        var (typeName, warning) = ResolveInvocationTarget(node.SpanStart, types.ArgumentOutOfRangeExceptionType);
        var stmt1 = leftOther is null
            ? BuildInvocationStatement(typeName, leftMethod, [SyntaxFactory.IdentifierName(leftParam.Parameter!.Name)])
            : BuildInvocationStatement(typeName, leftMethod, [SyntaxFactory.IdentifierName(leftParam.Parameter!.Name), leftOther]);
        var stmt2 = rightOther is null
            ? BuildInvocationStatement(typeName, rightMethod, [SyntaxFactory.IdentifierName(rightParam.Parameter!.Name)])
            : BuildInvocationStatement(typeName, rightMethod, [SyntaxFactory.IdentifierName(rightParam.Parameter!.Name), rightOther]);

        var indent = GetIndent(node);
        var innerIndent = indent + "    ";
        var block = SyntaxFactory.Block(
            SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")),
            SyntaxFactory.List(new StatementSyntax[]
            {
                stmt1.WithLeadingTrivia(SyntaxFactory.Whitespace(innerIndent)).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")),
                stmt2.WithLeadingTrivia(SyntaxFactory.Whitespace(innerIndent)).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n")),
            }),
            SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.Whitespace(indent)));
        var line = Line(node);
        var suffix = warning is null ? "" : $" ({warning})";
        return (block, $"{path}:{line}  EXTENSION: split compound guard into {typeName}.{leftMethod}(...) + {typeName}.{rightMethod}(...){suffix}");
    }

    // Shared build/report step for CA1510-1512: checks the paramName argument
    // for replaceability (ported: HasReplaceableArgumentName,
    // UseExceptionThrowHelpers.cs lines 320-326) and, on top of that, our own
    // DIVERGENCE from the stock fixer — see the comment on CheckParamName.
    private (StatementSyntax?, string?) BuildOrReport(
        IfStatementSyntax node, INamedTypeSymbol exceptionType, string methodName,
        IParameterSymbol parameter, IObjectCreationOperation creation, int paramNameArgIndex, SyntaxNode? otherSyntax)
    {
        var (ok, reason) = CheckParamName(creation, paramNameArgIndex, parameter);
        var line = Line(node);
        if (!ok)
        {
            return (null, $"{path}:{line}  {reason}");
        }

        var (typeName, warning) = ResolveInvocationTarget(node.SpanStart, exceptionType);
        var args = otherSyntax is null
            ? new ExpressionSyntax[] { SyntaxFactory.IdentifierName(parameter.Name) }
            : [SyntaxFactory.IdentifierName(parameter.Name), (ExpressionSyntax)otherSyntax];
        var stmt = BuildInvocationStatement(typeName, methodName, args);
        var suffix = warning is null ? "" : $" ({warning})";
        return (stmt, $"{path}:{line}  converted to {typeName}.{methodName}({string.Join(", ", args.Select(a => a.ToString()))}){suffix}");
    }

    // Ported: HasReplaceableArgumentName, UseExceptionThrowHelpers.cs lines
    // 320-326 — "we only want to replace throws with ThrowIfNull if either there
    // isn't currently a specified parameter name ... or if it's specified as a
    // constant ... primarily to avoid false positives with complicated
    // expressions for computing the parameter name". The stock fixer then
    // DISCARDS whatever that constant said and always uses the checked
    // parameter's real name (see UseExceptionThrowHelpersFixer.ApplyFix, which
    // only ever passes `arg`/`other` from the CONDITION, never anything read out
    // of the exception's own constructor arguments).
    //
    // DIVERGENCE: this tool is more conservative than that. If the constant
    // string given (a literal, or a `nameof(x)`) does not match the parameter
    // actually being null/range/empty-checked, that already-suspicious call site
    // is reported instead of silently rewritten — the original code's exception
    // may have been reporting the wrong `ParamName` on purpose (rare) or by bug
    // (common), and blindly "fixing" it changes runtime-observable behavior
    // (the string in a caught `ArgumentException.ParamName`) without anyone
    // having looked at it. A matching name, or no name argument at all, rewrites
    // exactly as the stock fixer would.
    private static (bool Ok, string? Reason) CheckParamName(IObjectCreationOperation creation, int argIndex, IParameterSymbol checkedParameter)
    {
        var arg = creation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == argIndex);
        if (arg is null)
        {
            return (true, null); // argument omitted entirely — nothing to mismatch
        }

        if (!Matchers.TryGetConstantOrNameofTarget(arg.Value, out var literalValue, out var nameofTarget))
        {
            return (false, $"paramName argument `{arg.Value.Syntax}` is not a constant/`nameof(...)` -> cannot prove it's safe to replace, skipping (matches the analyzer's own HasReplaceableArgumentName skip)");
        }

        var expectedName = checkedParameter.Name;
        var actual = nameofTarget ?? literalValue;
        if (actual is not null && actual != expectedName)
        {
            return (false, $"paramName argument says '{actual}' but the checked parameter is '{expectedName}' -> reporting instead of rewriting (the stock fixer would silently discard '{actual}' and use '{expectedName}'; see SKILL.md 'Divergences')");
        }

        return (true, null);
    }

    // Built via re-parsing the joined argument text rather than hand-assembling
    // SeparatedList separator trivia — args.ToString() for a single expression
    // is already well-formed source text, so this reads naturally (`Foo(a, b)`)
    // without manually managing comma trivia.
    // Resolves the type name to emit at `position` so the output reads like the
    // stock fixer's — a short name (`ArgumentNullException.ThrowIfNull(x)`) when
    // that binds to the intended type there, honouring whatever `using`s
    // (explicit, global, or implicit) are in scope, and only falling back to a
    // qualified form when the short name doesn't resolve to the real type at
    // that point (missing `using System;`) or resolves to something else
    // (a local type/alias shadowing it). Speculative binding — not a text
    // check for `using System;` — because usings, aliases, and nested-type
    // shadowing all affect what a bare identifier means at a given position,
    // and only the binder actually knows.
    //
    // --helper-type mode: the alias type usually isn't declared in the throwaway
    // scan compilation at all (the target project supplies the `<Using Alias>`
    // separately, per `csharp-guard-clauses`'s polyfill convention) — that's
    // the expected, common case, not a problem, so it's not qualified (there's
    // nothing else to qualify it with). Only warn if the short name is already
    // bound to something else at that point — a genuine collision worth flagging.
    private (string TypeName, string? Warning) ResolveInvocationTarget(int position, INamedTypeSymbol exceptionType)
    {
        var shortName = types.HelperTypeName ?? exceptionType.Name;
        var info = SpeculativeBind(position, shortName);

        if (types.HelperTypeName is not null)
        {
            if (info.Symbol is { } bound && bound is not INamedTypeSymbol)
            {
                return (shortName, $"'{shortName}' already resolves to {bound.Kind} '{bound.ToDisplayString()}' at this point, not a type — emitted as-is; add the alias or rename before this compiles");
            }

            return (shortName, null);
        }

        if (SymbolEqualityComparer.Default.Equals(info.Symbol, exceptionType))
        {
            return (shortName, null);
        }

        // Short name doesn't bind to the real type here (no `using System;` in
        // scope, or shadowed) — qualify with the namespace, and check THAT
        // isn't itself shadowed before deciding `global::` is unnecessary.
        var containingNamespace = exceptionType.ContainingNamespace.ToDisplayString();
        var namespaceHeadInfo = SpeculativeBind(position, containingNamespace.Split('.')[0]);
        return namespaceHeadInfo.Symbol is INamespaceSymbol
            ? ($"{containingNamespace}.{shortName}", null)
            : ($"global::{containingNamespace}.{shortName}", null);
    }

    private SymbolInfo SpeculativeBind(int position, string identifier)
    {
        try
        {
            return model.GetSpeculativeSymbolInfo(position, SyntaxFactory.IdentifierName(identifier), SpeculativeBindingOption.BindAsExpression);
        }
        catch (ArgumentException)
        {
            // Position isn't a valid speculative-binding location (shouldn't
            // happen for a position inside a member body, but fail safe to
            // "doesn't bind" rather than throw out of a rewrite pass).
            return default;
        }
    }

    private static ExpressionStatementSyntax BuildInvocationStatement(string typeName, string methodName, IEnumerable<ExpressionSyntax> args)
    {
        var argsText = string.Join(", ", args.Select(a => a.NormalizeWhitespace().ToFullString()));
        var expr = SyntaxFactory.ParseExpression($"{typeName}.{methodName}({argsText})");
        return SyntaxFactory.ExpressionStatement(expr, SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private INamedTypeSymbol? GetContainingTypeSymbol(SyntaxNode node)
    {
        var typeDecl = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return typeDecl is null ? null : model.GetDeclaredSymbol(typeDecl);
    }

    private static int Line(SyntaxNode node) => node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

    private static string GetIndent(SyntaxNode node)
    {
        var leading = node.GetLeadingTrivia().ToFullString();
        var lastNewline = leading.LastIndexOf('\n');
        return lastNewline >= 0 ? leading[(lastNewline + 1)..] : leading;
    }
}

// ===========================================================================
// Pure IOperation-based matchers, ported near-verbatim from
// UseExceptionThrowHelpers.cs (the analyzer already expresses every shape
// against IOperation, not syntax, so this is a direct port rather than a
// re-derivation). Two tiny helpers used throughout the analyzer
// (`HasNullConstantValue`/`WalkDownConversion`) live in the internal
// `Analyzer.Utilities` assembly and aren't public — they're one-liners,
// reimplemented here with the same names and semantics (see each for the
// citation of what they replicate).
static class Matchers
{
    public static bool HasNullConstantValue(IOperation operation) =>
        operation.ConstantValue is { HasValue: true, Value: null };

    // Reimplementation of the internal Analyzer.Utilities `IsNullableValueType`
    // extension used by UseExceptionThrowHelpers.cs's `AvoidComparing` local
    // function (line 268-270).
    public static bool IsNullableValueType(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    public static IOperation WalkDownConversion(IOperation operation)
    {
        while (operation is IConversionOperation { IsImplicit: true } conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    public static IOperation? GetThrownException(IThrowOperation throwOperation) =>
        throwOperation.Exception is { } e ? WalkDownConversion(e) : null;

    // Ported: UseExceptionThrowHelpers.cs, IsParameterNullCheck, lines 365-391.
    public static bool IsParameterNullCheck(IOperation condition, out IParameterReferenceOperation parameterReference)
    {
        parameterReference = null!;

        if (condition is IIsPatternOperation { Pattern: IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } } } isPattern)
        {
            // arg is null
            if (isPattern.Value is IParameterReferenceOperation p)
            {
                parameterReference = p;
            }
        }
        else if (condition is IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals } equalsOp)
        {
            if (HasNullConstantValue(equalsOp.RightOperand))
            {
                // arg == null
                if (WalkDownConversion(equalsOp.LeftOperand) is IParameterReferenceOperation p2)
                {
                    parameterReference = p2;
                }
            }
            else if (HasNullConstantValue(equalsOp.LeftOperand))
            {
                // null == arg
                if (WalkDownConversion(equalsOp.RightOperand) is IParameterReferenceOperation p3)
                {
                    parameterReference = p3;
                }
            }
        }

        return parameterReference is not null;
    }

    // Ported: UseExceptionThrowHelpers.cs, IsNullOrEmptyCheck +
    // IsArgLengthEqual0 + IsArgEqualStringEmpty, lines 393-449.
    public static bool IsNullOrEmptyCheck(ISymbol stringIsNullOrEmpty, ISymbol stringLength, ISymbol stringEmpty, IOperation condition, out IParameterReferenceOperation parameterReference)
    {
        if (condition is IInvocationOperation invocationOperation)
        {
            // string.IsNullOrEmpty(arg)
            if (SymbolEqualityComparer.Default.Equals(invocationOperation.TargetMethod, stringIsNullOrEmpty) &&
                invocationOperation.Arguments is [{ Value: IParameterReferenceOperation p }])
            {
                parameterReference = p;
                return true;
            }
        }
        else if (condition is IBinaryOperation { OperatorKind: BinaryOperatorKind.ConditionalOr } orOp &&
            IsParameterNullCheck(orOp.LeftOperand, out var nullCheckParameter) &&
            orOp.RightOperand is IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals } lengthCheckOperation)
        {
            // arg is null || arg.Length == 0
            if (IsArgLengthEqual0(stringLength, nullCheckParameter.Parameter!, lengthCheckOperation.LeftOperand, lengthCheckOperation.RightOperand) ||
                IsArgLengthEqual0(stringLength, nullCheckParameter.Parameter!, lengthCheckOperation.RightOperand, lengthCheckOperation.LeftOperand))
            {
                parameterReference = nullCheckParameter;
                return true;
            }

            // arg == null || arg == string.Empty
            if (IsArgEqualStringEmpty(stringEmpty, nullCheckParameter.Parameter!, lengthCheckOperation.LeftOperand, lengthCheckOperation.RightOperand) ||
                IsArgEqualStringEmpty(stringEmpty, nullCheckParameter.Parameter!, lengthCheckOperation.RightOperand, lengthCheckOperation.LeftOperand))
            {
                parameterReference = nullCheckParameter;
                return true;
            }
        }

        parameterReference = null!;
        return false;
    }

    private static bool IsArgEqualStringEmpty(ISymbol stringEmpty, IParameterSymbol arg, IOperation left, IOperation right) =>
        left is IParameterReferenceOperation p &&
        SymbolEqualityComparer.Default.Equals(p.Parameter, arg) &&
        p.Type?.SpecialType == SpecialType.System_String &&
        right is IFieldReferenceOperation f &&
        SymbolEqualityComparer.Default.Equals(stringEmpty, f.Member);

    private static bool IsArgLengthEqual0(ISymbol stringLength, IParameterSymbol arg, IOperation left, IOperation right) =>
        WalkDownConversion(right) is ILiteralOperation { ConstantValue: { HasValue: true, Value: 0 } } &&
        left is IPropertyReferenceOperation { Instance: IParameterReferenceOperation refParam } propRef &&
        SymbolEqualityComparer.Default.Equals(refParam.Parameter, arg) &&
        SymbolEqualityComparer.Default.Equals(stringLength, propRef.Member);

    // Ported: UseExceptionThrowHelpers.cs, IsNegativeAndOrZeroComparison, lines 452-525.
    public static bool IsNegativeAndOrZeroComparison(IOperation condition, out IParameterReferenceOperation parameterReferenceOperation, out string? methodName)
    {
        const string ThrowIfZero = "ThrowIfZero";
        const string ThrowIfNegative = "ThrowIfNegative";
        const string ThrowIfNegativeOrZero = "ThrowIfNegativeOrZero";

        if (condition is IIsPatternOperation { Pattern: IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: 0 } } } patternOperation)
        {
            // arg is 0
            methodName = ThrowIfZero;
            parameterReferenceOperation = (patternOperation.Value as IParameterReferenceOperation)!;
            return parameterReferenceOperation is not null;
        }

        if (condition is IBinaryOperation binaryOperation)
        {
            switch (binaryOperation.OperatorKind)
            {
                case BinaryOperatorKind.Equals:
                    if (WalkDownConversion(binaryOperation.LeftOperand) is ILiteralOperation { ConstantValue: { HasValue: true, Value: 0 } })
                    {
                        // arg == 0 — analyzer text says "arg == 0" but matches RightOperand as the parameter.
                        methodName = ThrowIfZero;
                        parameterReferenceOperation = (binaryOperation.RightOperand as IParameterReferenceOperation)!;
                        return parameterReferenceOperation is not null;
                    }

                    if (WalkDownConversion(binaryOperation.RightOperand) is ILiteralOperation { ConstantValue: { HasValue: true, Value: 0 } })
                    {
                        // 0 == arg
                        methodName = ThrowIfZero;
                        parameterReferenceOperation = (binaryOperation.LeftOperand as IParameterReferenceOperation)!;
                        return parameterReferenceOperation is not null;
                    }

                    break;

                case BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.LessThan:
                    if (binaryOperation.LeftOperand is IParameterReferenceOperation leftP &&
                        WalkDownConversion(binaryOperation.RightOperand) is ILiteralOperation { ConstantValue: { HasValue: true, Value: 0 } })
                    {
                        // arg < 0 / arg <= 0
                        methodName = binaryOperation.OperatorKind == BinaryOperatorKind.LessThanOrEqual ? ThrowIfNegativeOrZero : ThrowIfNegative;
                        parameterReferenceOperation = leftP;
                        return true;
                    }

                    break;

                case BinaryOperatorKind.GreaterThanOrEqual or BinaryOperatorKind.GreaterThan:
                    if (binaryOperation.RightOperand is IParameterReferenceOperation rightP &&
                        WalkDownConversion(binaryOperation.LeftOperand) is ILiteralOperation { ConstantValue: { HasValue: true, Value: 0 } })
                    {
                        // 0 > arg / 0 >= arg
                        methodName = binaryOperation.OperatorKind == BinaryOperatorKind.GreaterThanOrEqual ? ThrowIfNegativeOrZero : ThrowIfNegative;
                        parameterReferenceOperation = rightP;
                        return true;
                    }

                    break;
            }
        }

        methodName = null;
        parameterReferenceOperation = null!;
        return false;
    }

    // Ported: UseExceptionThrowHelpers.cs, IsGreaterLessEqualThanComparison, lines 527-594.
    public static bool IsGreaterLessEqualThanComparison(IOperation condition, out IParameterReferenceOperation parameterReferenceOperation, out string? methodName, out SyntaxNode? other)
    {
        if (condition is IBinaryOperation binaryOperation)
        {
            switch (binaryOperation.OperatorKind)
            {
                case BinaryOperatorKind.GreaterThan or BinaryOperatorKind.GreaterThanOrEqual or BinaryOperatorKind.LessThan or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals:
                    if (binaryOperation.LeftOperand is IParameterReferenceOperation leftParameter)
                    {
                        // arg > other / >= / < / <= / == / !=
                        methodName = binaryOperation.OperatorKind switch
                        {
                            BinaryOperatorKind.GreaterThan => "ThrowIfGreaterThan",
                            BinaryOperatorKind.GreaterThanOrEqual => "ThrowIfGreaterThanOrEqual",
                            BinaryOperatorKind.LessThan => "ThrowIfLessThan",
                            BinaryOperatorKind.LessThanOrEqual => "ThrowIfLessThanOrEqual",
                            BinaryOperatorKind.Equals => "ThrowIfEqual",
                            _ => "ThrowIfNotEqual",
                        };
                        other = binaryOperation.RightOperand.Syntax;
                        parameterReferenceOperation = leftParameter;
                        return true;
                    }

                    if (binaryOperation.RightOperand is IParameterReferenceOperation rightParameter)
                    {
                        // other > arg / >= / < / <= / == / != (operator flipped, see analyzer comment)
                        methodName = binaryOperation.OperatorKind switch
                        {
                            BinaryOperatorKind.GreaterThan => "ThrowIfLessThan",
                            BinaryOperatorKind.GreaterThanOrEqual => "ThrowIfLessThanOrEqual",
                            BinaryOperatorKind.LessThan => "ThrowIfGreaterThan",
                            BinaryOperatorKind.LessThanOrEqual => "ThrowIfGreaterThanOrEqual",
                            BinaryOperatorKind.Equals => "ThrowIfEqual",
                            _ => "ThrowIfNotEqual",
                        };
                        other = binaryOperation.LeftOperand.Syntax;
                        parameterReferenceOperation = rightParameter;
                        return true;
                    }

                    break;
            }
        }

        methodName = null;
        parameterReferenceOperation = null!;
        other = null;
        return false;
    }

    // EXTENSION helper: try either CA1512 shape against one operand of a
    // compound `||`, requiring the "other" bound (if any) to also be side-effect
    // free at the call site (checked separately by the caller against the whole
    // operand, this only extracts parameter+method+other).
    public static bool TryMatchRangeOperand(IOperation operand, out IParameterReferenceOperation parameter, out string method, out ExpressionSyntax? other)
    {
        if (IsNegativeAndOrZeroComparison(operand, out parameter, out var m1) && m1 is not null)
        {
            method = m1;
            other = null;
            return true;
        }

        if (IsGreaterLessEqualThanComparison(operand, out parameter, out var m2, out var otherNode) && m2 is not null)
        {
            method = m2;
            other = otherNode as ExpressionSyntax;
            return true;
        }

        method = "";
        other = null;
        return false;
    }

    // Ported: UseExceptionThrowHelpers.cs, HasPossiblyMeaningfulAdditionalArguments,
    // lines 328-361 (local function, hoisted here as a static method taking the
    // ArgumentException type symbol to replicate the one type-specific branch).
    public static bool HasPossiblyMeaningfulAdditionalArguments(IObjectCreationOperation objectCreationOperation, INamedTypeSymbol argumentExceptionType)
    {
        var args = objectCreationOperation.Arguments;
        if (args.Length == 0)
        {
            return false;
        }

        if (args.Length >= 3)
        {
            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(objectCreationOperation.Type, argumentExceptionType))
        {
            // ArgumentException's message is first.
            return !IsNullOrEmptyMessage(args[0]);
        }

        // The other exceptions all have the message second.
        return args.Length >= 2 && !IsNullOrEmptyMessage(args[1]);

        static bool IsNullOrEmptyMessage(IArgumentOperation arg) =>
            WalkDownConversion(arg.Value) is ILiteralOperation { ConstantValue.HasValue: true } literal &&
            literal.ConstantValue.Value is null or "";
    }

    // Ported: UseExceptionThrowHelpers.cs, ObjectDisposedException handling,
    // lines 276-312 — the `X.GetType().Name`/`.FullName` shape the fixer needs
    // to determine the ThrowIf instance argument.
    public static SyntaxNode? TryGetObjectDisposedInstance(IObjectCreationOperation creation, ISymbol getType, out bool isImplicitThis)
    {
        isImplicitThis = false;
        if (creation.Arguments is not [{ Value: IPropertyReferenceOperation { Instance: IInvocationOperation getTypeCall } nameReference }, ..] ||
            nameReference.Member.Name is not ("Name" or "FullName") ||
            !SymbolEqualityComparer.Default.Equals(getType, getTypeCall.TargetMethod))
        {
            return null;
        }

        if (getTypeCall.Instance is IInstanceReferenceOperation { IsImplicit: true })
        {
            isImplicitThis = true;
            return null;
        }

        return getTypeCall.Instance?.Syntax;
    }

    // Ported: HasReplaceableArgumentName's companion — a constant string
    // (literal, or `nameof(x)`), used by CheckParamName above. Returns the
    // literal string value if it's a plain constant, or the identifier name
    // targeted by `nameof(...)` if that's the shape (so the caller can compare
    // either against the checked parameter's real name).
    public static bool TryGetConstantOrNameofTarget(IOperation argValue, out string? literalValue, out string? nameofTarget)
    {
        literalValue = null;
        nameofTarget = null;

        if (argValue.Syntax is InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "nameof" },
                ArgumentList.Arguments: [var nameofArg],
            })
        {
            nameofTarget = (nameofArg.Expression as IdentifierNameSyntax)?.Identifier.Text
                ?? (nameofArg.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;
            return nameofTarget is not null;
        }

        if (argValue.ConstantValue is { HasValue: true, Value: string s })
        {
            literalValue = s;
            return true;
        }

        // Argument present but not provably constant at all (e.g. absent name
        // via a parameterless ctor is handled by the caller before this is
        // reached; ConstantValue.HasValue false covers ComputeName(...), a
        // ternary, string concatenation, etc.) — same set of shapes the real
        // HasReplaceableArgumentName rejects.
        return argValue.ConstantValue.HasValue;
    }

    // EXTENSION safety check: conservative side-effect-free test used only by
    // the compound-`||` extension above. Anything with a call, assignment,
    // increment/decrement, await, or object/array creation is rejected;
    // identifiers, literals, and plain member access chains over them are fine.
    public static bool IsSideEffectFree(SyntaxNode expr) =>
        !expr.DescendantNodesAndSelf().Any(n => n is InvocationExpressionSyntax
            or AssignmentExpressionSyntax
            or PostfixUnaryExpressionSyntax
            or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression }
            or AwaitExpressionSyntax
            or ObjectCreationExpressionSyntax
            or ElementAccessExpressionSyntax);
}
