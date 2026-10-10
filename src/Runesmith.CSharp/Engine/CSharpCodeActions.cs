using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Runesmith.LanguageServices;
using RoslynCodeAction = Microsoft.CodeAnalysis.CodeActions.CodeAction;
using TextSpan = Runesmith.Text.TextSpan;

namespace Runesmith.CSharp.Engine;

/// <summary>The compiler's own code fixes and refactorings for C#: the providers the C# features export, created once, asked for the compiler
/// problems in a span and, when the user asks, for refactorings of the span.</summary>
internal sealed class CSharpCodeActions
{
    // The compiler's features are exported for their own composition; providers that need more than a parameterless constructor are left out.
    private static readonly Lazy<ImmutableArray<CodeFixProvider>> FixProviders = new(() => Create<CodeFixProvider, ExportCodeFixProviderAttribute>(a => a.Languages));
    private static readonly Lazy<ImmutableArray<CodeRefactoringProvider>> RefactoringProviders =
        new(() => Create<CodeRefactoringProvider, ExportCodeRefactoringProviderAttribute>(a => a.Languages));

    private static readonly Lazy<ILookup<string, CodeFixProvider>> FixersById = new(() =>
        FixProviders.Value.SelectMany(provider => Safe(() => provider.FixableDiagnosticIds, []).Select(id => (id, provider))).ToLookup(e => e.id, e => e.provider));

    /// <summary>Gets the fixes for the compiler problems in a span and, with <paramref name="includeRefactorings"/>, the refactorings of it.</summary>
    public static async Task<IReadOnlyList<CodeActionEntry>> GetAsync(Document document, SourceDocument source, TextSpan span, bool includeRefactorings,
        CancellationToken cancellationToken)
    {
        var fixes = new List<RoslynCodeAction>();
        var roslynSpan = new Microsoft.CodeAnalysis.Text.TextSpan(span.Start, span.Length);
        if (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is { } model)
        {
            foreach (var diagnostic in model.GetDiagnostics(roslynSpan, cancellationToken).Where(d => d.Location.IsInSource))
            {
                foreach (var provider in FixersById.Value[diagnostic.Id])
                {
                    var found = new List<RoslynCodeAction>();
                    var context = new CodeFixContext(document, diagnostic, (action, _) => found.Add(action), cancellationToken);
                    if (!await RunAsync(() => provider.RegisterCodeFixesAsync(context)).ConfigureAwait(false))
                        continue;

                    fixes.AddRange(found.SelectMany(Flatten));
                }
            }
        }

        // Fixes for the same problem from several places, such as one per diagnostic on the line, are offered once.
        List<CodeActionEntry> entries =
        [
            .. fixes.OrderByDescending(action => action.Priority).DistinctBy(action => action.Title)
                .Select(action => new CodeActionEntry(action.Title, IsRefactoring: false, new Pending(action, document.Project.Solution, source))),
        ];
        if (entries.Count > 0)
            entries[0] = entries[0] with { IsPreferred = true };

        if (!includeRefactorings)
            return entries;

        foreach (var provider in RefactoringProviders.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = new List<RoslynCodeAction>();
            var context = new CodeRefactoringContext(document, roslynSpan, action => found.Add(action), cancellationToken);
            if (!await RunAsync(() => provider.ComputeRefactoringsAsync(context)).ConfigureAwait(false))
                continue;

            foreach (var action in found.SelectMany(Flatten))
                entries.Add(new CodeActionEntry(action.Title, IsRefactoring: true, new Pending(action, document.Project.Solution, source)));
        }

        return [.. entries.DistinctBy(entry => (entry.Title, entry.IsRefactoring))];
    }

    /// <summary>Computes the edits of an entry this class made.</summary>
    public static async Task<IReadOnlyList<FileEdit>> ResolveAsync(CodeActionEntry entry, Func<string, int?> versionOf, CancellationToken cancellationToken)
    {
        if (entry.Token is not Pending pending)
            return [];

        var operations = await pending.Action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
        var changed = operations.OfType<ApplyChangesOperation>().LastOrDefault()?.ChangedSolution;
        if (changed is null)
            return [];

        return await CSharpEdits.DiffAsync(pending.Solution, changed, path =>
            CSharpDocuments.PathComparer.Equals(path, pending.Source.Path) ? pending.Source.Version : versionOf(path), cancellationToken).ConfigureAwait(false);
    }

    // Actions that group others, such as "Fix formatting" with a choice of scopes, are offered as their choices; ones that ask for options in
    // a dialog are left out.
    private static IEnumerable<RoslynCodeAction> Flatten(RoslynCodeAction action) =>
        action is CodeActionWithOptions ? [] : action.NestedActions.IsDefaultOrEmpty ? [action] : action.NestedActions.SelectMany(Flatten);

    private static async Task<bool> RunAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return fallback;
        }
    }

    private static ImmutableArray<TProvider> Create<TProvider, TExport>(Func<TExport, string[]> languages)
        where TProvider : class
        where TExport : Attribute
    {
        var assemblies = new[] { typeof(CodeFixProvider).Assembly.GetName().Name, "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features" }
            .Select(name => Safe(() => Assembly.Load(name!), null))
            .OfType<Assembly>()
            .Distinct();
        var providers = ImmutableArray.CreateBuilder<TProvider>();
        foreach (var type in assemblies.SelectMany(assembly => Safe(() => assembly.GetTypes(), [])))
        {
            if (type.IsAbstract || !typeof(TProvider).IsAssignableFrom(type) || type.GetCustomAttribute<TExport>() is not { } export
                || !languages(export).Contains(LanguageNames.CSharp) || type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is null)
            {
                continue;
            }

            if (Safe(() => (TProvider?)Activator.CreateInstance(type, nonPublic: true), null) is { } provider)
                providers.Add(provider);
        }

        return providers.ToImmutable();
    }

    private sealed record Pending(RoslynCodeAction Action, Solution Solution, SourceDocument Source);
}
