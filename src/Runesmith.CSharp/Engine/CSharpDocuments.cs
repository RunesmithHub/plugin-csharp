using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Runesmith.LanguageServices;
using TextChange = Runesmith.Text.TextChange;

namespace Runesmith.CSharp.Engine;

/// <summary>The C# solution as the editor sees it: the loaded projects, a project for files outside them, and the open documents with
/// their recent versions, so a request for an older version is answered from that version's text.</summary>
/// <remarks>The solution is immutable; changes replace it under a short lock, and requests read a document without blocking edits. Until
/// the first solution arrives, open documents are only remembered, by their latest version.</remarks>
internal sealed class CSharpDocuments
{
    private const int VersionsKept = 8;

    // Each edit's syntax tree is parsed lazily from the previous one; an unparsed chain this long gets parsed in the background, before its
    // depth can overflow the stack when something finally asks for a tree.
    private const int UnparsedVersionsAllowed = 64;

    private readonly Lock gate = new();
    private readonly Dictionary<string, OpenDocument> open = new(PathComparer);
    private readonly Dictionary<string, SourceDocument> waiting = new(PathComparer);
    private Solution? solution;
    private ProjectId? adhocProject;

    public static StringComparer PathComparer { get; } = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Gets the current solution.</summary>
    public Solution Solution
    {
        get
        {
            lock (gate)
                return solution ?? throw new InvalidOperationException("The workspace is not created yet.");
        }
    }

    /// <summary>Gets the newest version of each open document.</summary>
    public IReadOnlyList<Document> OpenDocuments
    {
        get
        {
            lock (gate)
                return [.. open.Values.Select(document => document.Versions[^1].Document)];
        }
    }

    /// <summary>Gets the document of a file for a snapshot of its text: the version kept for that snapshot when the file is open, a document
    /// with the snapshot's text when the version is no longer kept, and the project's document when the file is not open; null when no
    /// project has the file.</summary>
    /// <param name="snapshot">The text, or null for the newest version.</param>
    public Document? Find(string path, Runesmith.Text.TextSnapshot? snapshot)
    {
        lock (gate)
        {
            if (solution is null)
                return null;

            // A document that was never saved is known by a name its host chose; its text tells it apart as well.
            if (!open.TryGetValue(path, out var state) && snapshot is not null)
                state = open.Values.FirstOrDefault(candidate => candidate.Versions.Any(version => ReferenceEquals(version.Snapshot, snapshot)));

            if (state is not null)
            {
                if (snapshot is null)
                    return state.Versions[^1].Document;

                foreach (var (_, text, kept) in state.Versions)
                {
                    if (ReferenceEquals(text, snapshot))
                        return kept;
                }

                return solution.WithDocumentText(state.Id, SourceText.From(snapshot.GetText()), PreservationMode.PreserveIdentity).GetDocument(state.Id);
            }

            var full = Path.GetFullPath(path);
            var id = solution.GetDocumentIdsWithFilePath(full).FirstOrDefault(candidate => candidate.ProjectId != adhocProject)
                ?? solution.GetDocumentIdsWithFilePath(full).FirstOrDefault();
            var document = id is null ? null : solution.GetDocument(id);
            return snapshot is null ? document : document?.WithText(SourceText.From(snapshot.GetText()));
        }
    }

    /// <summary>Whether a document belongs to the project of files outside the loaded projects.</summary>
    public bool IsAdhoc(Document document) => document.Project.Id == adhocProject;

    public void Open(SourceDocument document)
    {
        lock (gate)
        {
            if (solution is null)
            {
                waiting[document.Path] = document;
                return;
            }

            OpenInSolution(document);
        }
    }

    public void Change(SourceDocument document, IReadOnlyList<TextChange> changes)
    {
        lock (gate)
        {
            if (solution is null)
            {
                waiting[document.Path] = document;
                return;
            }

            if (!open.TryGetValue(document.Path, out var state))
            {
                Open(document);
                return;
            }

            var latest = state.Versions[^1].Document;
            var text = latest.TryGetText(out var previous)
                ? previous.WithChanges(changes.Select(change => change.ToRoslyn()))
                : SourceText.From(document.Snapshot.GetText());
            if (text.Length != document.Snapshot.Length)
                text = SourceText.From(document.Snapshot.GetText());

            solution = solution.WithDocumentText(state.Id, text, PreservationMode.PreserveIdentity);
            var changed = solution.GetDocument(state.Id)!;
            state.Versions.Add((document.Version, document.Snapshot, changed));
            if (state.Versions.Count > VersionsKept)
                state.Versions.RemoveAt(0);

            state.Unparsed = latest.TryGetSyntaxTree(out _) ? 1 : state.Unparsed + 1;
            if (state.Unparsed >= UnparsedVersionsAllowed)
            {
                _ = Task.Run(() => changed.GetSyntaxTreeAsync());
                state.Unparsed = 0;
            }
        }
    }

    public void Close(string path)
    {
        var disk = File.Exists(path) ? SourceText.From(File.ReadAllText(path)) : null;
        lock (gate)
        {
            waiting.Remove(path);
            if (solution is null || !open.Remove(path, out var state))
                return;

            solution = state.Id.ProjectId == adhocProject || disk is null
                ? solution.RemoveDocument(state.Id)
                : solution.WithDocumentText(state.Id, disk, PreservationMode.PreserveIdentity);
        }
    }

    /// <summary>Gets the document for a version of an open document, or a document with that version's text when the version is no longer
    /// kept; null when the document is not open.</summary>
    public Document? Get(SourceDocument document)
    {
        lock (gate)
        {
            if (solution is null || !open.TryGetValue(document.Path, out var state))
                return null;

            foreach (var (version, _, kept) in state.Versions)
            {
                if (version == document.Version)
                    return kept;
            }

            return solution.WithDocumentText(state.Id, SourceText.From(document.Snapshot.GetText()), PreservationMode.PreserveIdentity).GetDocument(state.Id);
        }
    }

    /// <summary>Replaces the projects with loaded ones, moving each open document into the project that has it, with its text.</summary>
    public void Replace(Solution loaded)
    {
        lock (gate)
        {
            (solution, adhocProject) = AdhocProject.AddTo(loaded);
            foreach (var (path, state) in open.ToList())
            {
                var latest = state.Versions[^1];
                var text = latest.Document.TryGetText(out var current) ? current : SourceText.From(File.Exists(path) ? File.ReadAllText(path) : "");
                var id = FindOrAdd(path, text);
                solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                open[path] = new OpenDocument(id, [(latest.Version, latest.Snapshot, solution.GetDocument(id)!)]);
            }

            foreach (var document in waiting.Values)
                OpenInSolution(document);
            waiting.Clear();
        }
    }

    // Callers hold the lock and have checked that there is a solution.
    private void OpenInSolution(SourceDocument document)
    {
        var text = SourceText.From(document.Snapshot.GetText());
        var id = open.TryGetValue(document.Path, out var existing) ? existing.Id : FindOrAdd(document.Path, text);
        solution = solution!.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
        open[document.Path] = new OpenDocument(id, [(document.Version, document.Snapshot, solution.GetDocument(id)!)]);
    }

    private DocumentId FindOrAdd(string path, SourceText text)
    {
        // Projects list their files with the system's separators; an editor path may use the other one on Windows.
        path = Path.GetFullPath(path);
        var current = solution!;
        var existing = current.GetDocumentIdsWithFilePath(path).FirstOrDefault(id => id.ProjectId != adhocProject)
            ?? current.GetDocumentIdsWithFilePath(path).FirstOrDefault();
        if (existing is not null)
            return existing;

        var id = DocumentId.CreateNewId(adhocProject!, path);
        solution = current.AddDocument(id, Path.GetFileName(path), text, filePath: path);
        return id;
    }

    private sealed record OpenDocument(DocumentId Id, List<(int Version, Runesmith.Text.TextSnapshot Snapshot, Document Document)> Versions)
    {
        /// <summary>Gets or sets how many versions in a row have no parsed syntax tree.</summary>
        public int Unparsed { get; set; }
    }
}
