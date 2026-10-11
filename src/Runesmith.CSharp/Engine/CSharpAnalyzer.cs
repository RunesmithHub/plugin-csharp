using System.Buffers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Runesmith.LanguageServices;
using TextChange = Runesmith.Text.TextChange;

namespace Runesmith.CSharp.Engine;

/// <summary>Understands C#, every version the compiler does: completion, hover, go to definition, signature help, problems, code fixes and
/// refactorings, rename, formatting and inlay hints.</summary>
/// <remarks>The folder's solution, or its projects, load in the background; until a document's project is loaded, the document belongs to a
/// project of its own with the newest language version and the .NET reference assemblies.</remarks>
public sealed class CSharpAnalyzer : ILanguageAnalyzer, ICodeActionAnalyzer, IRenameAnalyzer, IFormattingAnalyzer, IInlayHintAnalyzer
{
    /// <summary>The language id of C#.</summary>
    public const string LanguageId = "csharp";

    private readonly CSharpCompletion completion = new();
    private AnalyzerContext? context;
    private readonly CSharpDocuments documents = new();
    private readonly Task created;
    private MSBuildWorkspace? workspace;
    private CancellationTokenSource loading = new();
    private CancellationTokenSource? prefetch;
    private CancellationTokenSource? speculation;

    // Work ahead of the user starts after this pause, so a burst of edits, such as a paste of many lines, cancels it before it costs anything.
    private static readonly TimeSpan AheadOfTimeDelay = TimeSpan.FromMilliseconds(15);

    private static readonly SearchValues<char> Separators = SearchValues.Create(" \t\n(,=;{[!&|+-*/:?<>");

    /// <summary>Creates the analyzer; the compiler's services take about half a second to compose the first time, so that happens on a
    /// background thread, and requests wait for it.</summary>
    public CSharpAnalyzer() => created = Task.Run(() =>
    {
        var first = MSBuildWorkspace.Create();
        SwitchTo(first, first.CurrentSolution);
    });

    public IReadOnlyList<string> LanguageIds { get; } = [LanguageId];

    public IReadOnlyList<char> CompletionTriggerCharacters { get; } = ['.'];

    public IReadOnlyList<char> SignatureHelpTriggerCharacters { get; } = ['(', ','];

    public IReadOnlyList<char> SignatureHelpRetriggerCharacters { get; } = [','];

    /// <summary>Gets the solution as it is now, for tests and measurements.</summary>
    internal Solution Solution => documents.Solution;

    /// <summary>Gets how many completion lists the compiler computed.</summary>
    internal int ComputedCompletionLists => completion.Computed;

    /// <summary>Gets how many speculated member lists were taken when their <c>.</c> was typed.</summary>
    internal int SpeculationsUsed => completion.SpeculationsUsed;

    /// <summary>Raised, on a background thread, when the solution is replaced, such as when the folder's projects finish loading.</summary>
    internal event Action? SolutionReplaced;

    /// <summary>Gets a task that completes when the projects of the open folder are loaded.</summary>
    internal Task Loaded { get; private set; } = Task.CompletedTask;

    public void Initialize(AnalyzerContext context)
    {
        this.context = context;
        _ = context.RunInBackground("Warm up C#", WarmUpCompilerAsync);
    }

    public Task OpenWorkspaceAsync(string? rootPath, CancellationToken cancellationToken)
    {
        var context = Context;
        loading.Cancel();
        loading.Dispose();
        loading = new CancellationTokenSource();
        var token = loading.Token;
        Loaded = context.RunInBackground("Load C# projects", async background =>
        {
            await created.ConfigureAwait(false);
            var next = MSBuildWorkspace.Create();
            if (rootPath is null)
            {
                SwitchTo(next, next.CurrentSolution);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, background);
            var loaded = await ProjectLoader.LoadAsync(next, rootPath, context.Log, linked.Token).ConfigureAwait(false);
            if (linked.IsCancellationRequested)
                return;

            SwitchTo(next, loaded ?? next.CurrentSolution);
            context.InvalidateDiagnostics();
            await WarmUpAsync(linked.Token).ConfigureAwait(false);
        });
        return Task.CompletedTask;
    }

    public void Open(SourceDocument document) => documents.Open(document);

    public void Change(SourceDocument document, IReadOnlyList<TextChange> changes)
    {
        completion.OnChanged(document.Path, document.Version, changes);
        documents.Change(document, changes);
        if (IdentifierEndAfter(changes, document.Snapshot) is { } end && documents.Get(document) is { } typed)
        {
            Renew(ref speculation);
            var token = speculation!.Token;
            _ = Context.RunInBackground("Speculate C# member access", async background =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, background);
                await Task.Delay(AheadOfTimeDelay, linked.Token).ConfigureAwait(false);
                await completion.SpeculateMemberAccessAsync(document.Path, document.Version, typed, end, linked.Token).ConfigureAwait(false);
            });
        }

        if (WordStartAfter(changes) is { } position && documents.Get(document) is { } current)
        {
            Renew(ref prefetch);
            var token = prefetch!.Token;
            _ = Context.RunInBackground("Prefetch C# completion", async background =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, background);
                await Task.Delay(AheadOfTimeDelay, linked.Token).ConfigureAwait(false);
                await completion.PrefetchAsync(document.Path, document.Version, current, position, linked.Token).ConfigureAwait(false);
            });
        }
    }

    public void Close(string path)
    {
        completion.Forget(path);
        documents.Close(path);
    }

    public async ValueTask CompleteAsync(CompletionQuery query, CompletionSink sink, CancellationToken cancellationToken)
    {
        if (await GetAsync(query.Document).ConfigureAwait(false) is { } document)
            await completion.CompleteAsync(document, query, sink, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<CompletionDetails?> ResolveAsync(CompletionEntry entry, CancellationToken cancellationToken) =>
        CSharpCompletion.ResolveAsync(entry, cancellationToken);

    public async ValueTask<HoverResult?> HoverAsync(DocumentPosition position, CancellationToken cancellationToken) =>
        await GetAsync(position.Document).ConfigureAwait(false) is { } document ? await CSharpNavigation.HoverAsync(document, position.Offset, cancellationToken).ConfigureAwait(false) : null;

    public async ValueTask<IReadOnlyList<SourceLocation>> DefinitionAsync(DocumentPosition position, CancellationToken cancellationToken) =>
        await GetAsync(position.Document).ConfigureAwait(false) is { } document ? await CSharpNavigation.DefinitionAsync(document, position.Offset, cancellationToken).ConfigureAwait(false) : [];

    public async ValueTask<SignatureHelpResult?> SignatureHelpAsync(DocumentPosition position, CancellationToken cancellationToken) =>
        await GetAsync(position.Document).ConfigureAwait(false) is { } document ? await CSharpSignatureHelp.GetAsync(document, position.Offset, cancellationToken).ConfigureAwait(false) : null;

    public async ValueTask<IReadOnlyList<Problem>> DiagnosticsAsync(SourceDocument document, CancellationToken cancellationToken) =>
        await GetAsync(document).ConfigureAwait(false) is { } current
            ? await CSharpNavigation.DiagnosticsAsync(current, documents.IsAdhoc(current), Context.YieldAsync, cancellationToken).ConfigureAwait(false)
            : [];

    public async ValueTask<IReadOnlyList<CodeActionEntry>> CodeActionsAsync(SourceDocument document, Runesmith.Text.TextSpan span, bool includeRefactorings,
        CancellationToken cancellationToken) =>
        await GetAsync(document).ConfigureAwait(false) is { } current
            ? await CSharpCodeActions.GetAsync(current, document, span, includeRefactorings, cancellationToken).ConfigureAwait(false)
            : [];

    public async ValueTask<IReadOnlyList<FileEdit>> ResolveCodeActionAsync(CodeActionEntry entry, CancellationToken cancellationToken) =>
        await CSharpCodeActions.ResolveAsync(entry, OpenVersion, cancellationToken).ConfigureAwait(false);

    public async ValueTask<RenameSite?> PrepareRenameAsync(DocumentPosition position, CancellationToken cancellationToken) =>
        await GetAsync(position.Document).ConfigureAwait(false) is { } document
            ? await CSharpRefactoring.PrepareRenameAsync(document, position.Offset, cancellationToken).ConfigureAwait(false)
            : null;

    public async ValueTask<IReadOnlyList<FileEdit>> RenameAsync(DocumentPosition position, string newName, CancellationToken cancellationToken) =>
        await GetAsync(position.Document).ConfigureAwait(false) is { } document
            ? await CSharpRefactoring.RenameAsync(document, position.Document, position.Offset, newName, OpenVersion, cancellationToken).ConfigureAwait(false)
            : [];

    public async ValueTask<IReadOnlyList<TextChange>> FormatAsync(SourceDocument document, Runesmith.Text.TextSpan? span, int tabSize, bool insertSpaces,
        CancellationToken cancellationToken) =>
        await GetAsync(document).ConfigureAwait(false) is { } current
            ? await CSharpRefactoring.FormatAsync(current, span, tabSize, insertSpaces, cancellationToken).ConfigureAwait(false)
            : [];

    public async ValueTask<IReadOnlyList<InlayHintEntry>> InlayHintsAsync(SourceDocument document, CancellationToken cancellationToken) =>
        await GetAsync(document).ConfigureAwait(false) is { } current ? await CSharpInlayHints.GetAsync(current, cancellationToken).ConfigureAwait(false) : [];

    public async ValueTask DisposeAsync()
    {
        prefetch?.Cancel();
        prefetch?.Dispose();
        speculation?.Cancel();
        speculation?.Dispose();
        loading.Cancel();
        loading.Dispose();
        try
        {
            await created.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A workspace that failed to be created has nothing to dispose.
        }

        workspace?.Dispose();
    }

    /// <summary>Gets the document of a file for a snapshot of its text, or its newest version without one; null when no project has the
    /// file.</summary>
    internal async ValueTask<Document?> FindAsync(string path, Runesmith.Text.TextSnapshot? snapshot)
    {
        await created.ConfigureAwait(false);
        return documents.Find(path, snapshot);
    }

    /// <summary>Gets the solution as it is now, once the workspace is created.</summary>
    internal async ValueTask<Solution> GetSolutionAsync()
    {
        await created.ConfigureAwait(false);
        return documents.Solution;
    }

    private async ValueTask<Document?> GetAsync(SourceDocument document)
    {
        await created.ConfigureAwait(false);
        return documents.Get(document);
    }

    private void SwitchTo(MSBuildWorkspace next, Solution solution)
    {
        var previous = workspace;
        workspace = next;
        documents.Replace(solution);
        if (previous is not null && !ReferenceEquals(previous, next))
            previous.Dispose();
        SolutionReplaced?.Invoke();
    }

    // After typing a separator, the next word most likely starts right after it; its list is computed before its first letter is typed.
    private static int? WordStartAfter(IReadOnlyList<TextChange> changes)
    {
        if (changes.Count != 1 || changes[0].NewText is not { Length: > 0 } inserted || !Separators.Contains(inserted[^1]))
            return null;
        return changes[0].Span.Start + inserted.Length;
    }

    // While an identifier is typed, the member list after a dot that may follow is computed in the pause before the next key.
    private static int? IdentifierEndAfter(IReadOnlyList<TextChange> changes, Runesmith.Text.TextSnapshot snapshot)
    {
        if (changes.Count != 1 || changes[0].NewText is not { Length: > 0 } inserted || !IsIdentifierCharacter(inserted[^1]))
            return null;

        var end = changes[0].Span.Start + inserted.Length;
        return end < snapshot.Length && (IsIdentifierCharacter(snapshot[end]) || snapshot[end] == '.') ? null : end;
    }

    private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private static void Renew(ref CancellationTokenSource? source)
    {
        source?.Cancel();
        source?.Dispose();
        source = new CancellationTokenSource();
    }

    private AnalyzerContext Context => context ?? throw new InvalidOperationException("The analyzer has not been initialized.");

    private int? OpenVersion(string path) => Context.GetOpenDocument(path)?.Version;

    // Completing and describing once inside a method body of each open document fills the caches the first requests there need.
    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        foreach (var document in documents.OpenDocuments)
        {
            await Context.YieldAsync(cancellationToken).ConfigureAwait(false);
            if (Microsoft.CodeAnalysis.Completion.CompletionService.GetService(document) is not { } service
                || await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
            {
                continue;
            }

            var body = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax>().FirstOrDefault();
            var position = body is null ? root.FullSpan.End : body.OpenBraceToken.Span.End;
            var list = await service.GetCompletionsAsync(document, position, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (list.ItemsList.Count > 0)
                await service.GetDescriptionAsync(document, list.ItemsList[0], cancellationToken).ConfigureAwait(false);

            // Typing a few characters on a copy takes the path of the first real edits: a new compilation, then reuse of its semantic model.
            var typed = document;
            for (var i = 0; i < 3; i++)
            {
                await Context.YieldAsync(cancellationToken).ConfigureAwait(false);
                var text = await typed.GetTextAsync(cancellationToken).ConfigureAwait(false);
                typed = typed.WithText(text.WithChanges(new Microsoft.CodeAnalysis.Text.TextChange(new TextSpan(position + i, 0), "a")));
                await service.GetCompletionsAsync(typed, position + i + 1, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The first completion compiles the completion pipeline itself; doing it once on a scratch document keeps that off the user's first request.
    private static async Task WarmUpCompilerAsync(CancellationToken cancellationToken)
    {
        using var scratch = MSBuildWorkspace.Create();
        var (solution, project) = AdhocProject.AddTo(scratch.CurrentSolution);
        const string Text = "class C { void M() { var list = new List<string>(); list. } }";
        var id = DocumentId.CreateNewId(project);
        var document = solution.AddDocument(id, "WarmUp.cs", SourceText.From(Text)).GetDocument(id)!;
        var position = Text.IndexOf("list. ", StringComparison.Ordinal) + "list.".Length;
        if (Microsoft.CodeAnalysis.Completion.CompletionService.GetService(document) is { } service)
        {
            var list = await service.GetCompletionsAsync(document, position, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (list.ItemsList.Count > 0)
                await service.GetDescriptionAsync(document, list.ItemsList[0], cancellationToken).ConfigureAwait(false);
        }

        await CSharpNavigation.DiagnosticsAsync(document, isAdhoc: true, static _ => ValueTask.CompletedTask, cancellationToken).ConfigureAwait(false);
    }
}
