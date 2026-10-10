using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using Runesmith.LanguageServices;
using CompletionTriggerKind = Runesmith.LanguageServices.CompletionTriggerKind;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using TextChange = Runesmith.Text.TextChange;
using TextSpan = Runesmith.Text.TextSpan;

namespace Runesmith.CSharp.Engine;

/// <summary>Completion from the compiler's completion service, converted to compact candidates and kept per word, so typing more of the same
/// word filters the kept list instead of asking the compiler again.</summary>
internal sealed class CSharpCompletion
{
    private readonly ConcurrentDictionary<string, KeptList> kept = new(CSharpDocuments.PathComparer);
    private readonly ConcurrentDictionary<string, (int WordStart, int Version, Task<KeptList?> List)> prefetching = new(CSharpDocuments.PathComparer);
    private readonly ConcurrentDictionary<string, EditLog> edits = new(CSharpDocuments.PathComparer);
    private readonly ConcurrentDictionary<string, Speculation> speculations = new(CSharpDocuments.PathComparer);
    private int computed;
    private int speculationsUsed;

    /// <summary>Gets how many lists the compiler computed, as opposed to lists filtered from a kept one.</summary>
    internal int Computed => computed;

    /// <summary>Gets how many speculated member lists were taken when their <c>.</c> was typed.</summary>
    internal int SpeculationsUsed => speculationsUsed;

    /// <summary>Adds the suggestions at the query's position to the sink.</summary>
    public async ValueTask CompleteAsync(Document document, CompletionQuery query, CompletionSink sink, CancellationToken cancellationToken)
    {
        var snapshot = query.Document.Snapshot;
        var word = CompletionSink.DefaultWordSpan(snapshot, query.Offset);
        if (kept.TryGetValue(query.Document.Path, out var list)
            && list.WordStart == word.Start
            && (query.Trigger != CompletionTriggerKind.Character || list.Trigger == query.Character))
        {
            sink.SetWordSpan(TextSpan.FromBounds(word.Start, WordEnd(snapshot, query.Offset)));
            list.AddMatching(sink);
            return;
        }

        if (query.Trigger != CompletionTriggerKind.Character
            && prefetching.TryGetValue(query.Document.Path, out var pending)
            && pending.WordStart == word.Start
            && await Completed(pending.List).ConfigureAwait(false) is { } prefetched
            && Keep(query.Document.Path, prefetched, pending.Version))
        {
            sink.SetWordSpan(TextSpan.FromBounds(word.Start, WordEnd(snapshot, query.Offset)));
            prefetched.AddMatching(sink);
            return;
        }

        if (query is { Trigger: CompletionTriggerKind.Character, Character: '.' }
            && speculations.TryGetValue(query.Document.Path, out var speculation)
            && speculation.IsTyped && speculation.Position + 1 == word.Start
            && await Completed(speculation.List).ConfigureAwait(false) is { } speculated)
        {
            sink.SetWordSpan(TextSpan.FromBounds(word.Start, WordEnd(snapshot, query.Offset)));
            Interlocked.Increment(ref speculationsUsed);
            speculated.AddMatching(sink);
            return;
        }

        var character = query.Trigger == CompletionTriggerKind.Character ? query.Character : null;
        var trigger = character is { } typed ? CompletionTrigger.CreateInsertionTrigger(typed) : CompletionTrigger.Invoke;
        if (await ComputeAsync(document, query.Offset, trigger, character, cancellationToken).ConfigureAwait(false) is not { } computedList)
            return;

        Keep(query.Document.Path, computedList, query.Document.Version);
        var span = computedList.Span.Start <= query.Offset && computedList.Span.End <= snapshot.Length
            ? computedList.Span
            : TextSpan.FromBounds(word.Start, WordEnd(snapshot, query.Offset));
        sink.SetWordSpan(span);
        computedList.AddMatching(sink);
    }

    /// <summary>Computes the list for the word that starts at a position, before the user types it, and keeps it for that word.</summary>
    public async Task PrefetchAsync(string path, int version, Document document, int position, CancellationToken cancellationToken)
    {
        var task = ComputeAsync(document, position, CompletionTrigger.Invoke, null, cancellationToken);
        prefetching[path] = (position, version, task);
        try
        {
            if (await task.ConfigureAwait(false) is { } list && list.WordStart == position)
                Keep(path, list, version);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Computes the members after a <c>.</c> typed at a position, before it is typed, on a copy of the document that has it; the list
    /// is used when the next edit types exactly that <c>.</c>.</summary>
    public async Task SpeculateMemberAccessAsync(string path, int version, Document document, int position, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var withDot = document.WithText(text.WithChanges(new Microsoft.CodeAnalysis.Text.TextChange(new Microsoft.CodeAnalysis.Text.TextSpan(position, 0), ".")));
        var speculation = new Speculation(position, version, ComputeAsync(withDot, position + 1, CompletionTrigger.CreateInsertionTrigger('.'), '.', cancellationToken));
        speculations[path] = speculation;
        if (await Completed(speculation.List).ConfigureAwait(false) is { } list
            && speculation.IsTyped
            && speculations.TryGetValue(path, out var current) && ReferenceEquals(current, speculation)
            && list.WordStart == position + 1)
        {
            Interlocked.Increment(ref speculationsUsed);
            kept[path] = list;
        }
    }

    private async Task<KeptList?> ComputeAsync(Document document, int offset, CompletionTrigger trigger, char? character, CancellationToken cancellationToken)
    {
        if (CompletionService.GetService(document) is not { } service)
            return null;

        var completions = await service.GetCompletionsAsync(document, offset, trigger, cancellationToken: cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref computed);
        if (completions.ItemsList.Count == 0)
            return null;

        var span = completions.Span.ToRunesmith();
        var candidates = new CompletionCandidate[completions.ItemsList.Count];
        for (var i = 0; i < candidates.Length; i++)
            candidates[i] = ToCandidate(completions.ItemsList[i], new CompletionData(completions.ItemsList[i], document, span));
        return new KeptList(span, candidates, character);
    }

    // A prefetch that failed or was cancelled leaves the request to compute its own list.
    private static async Task<KeptList?> Completed(Task<KeptList?> list)
    {
        try
        {
            return await list.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Forgets a document's kept list when an edit changes the text before its word, which can change what the word may be.</summary>
    public void OnChanged(string path, int version, IReadOnlyList<TextChange> changes)
    {
        if (changes.Count == 0)
            return;

        var first = changes.Min(change => change.Span.Start);
        edits.GetOrAdd(path, _ => new EditLog()).Add(version, first);
        if (speculations.TryGetValue(path, out var speculation))
        {
            if (!speculation.IsTyped && changes is [{ Span.Length: 0, NewText: "." } dot] && dot.Span.Start == speculation.Position && version == speculation.Version + 1)
            {
                speculation.IsTyped = true;
                if (speculation.List.IsCompletedSuccessfully && speculation.List.Result is { } ready && ready.WordStart == dot.Span.Start + 1)
                {
                    Interlocked.Increment(ref speculationsUsed);
                    kept[path] = ready;
                }
                return;
            }

            speculations.TryRemove(path, out _);
        }

        if (kept.TryGetValue(path, out var list) && first < list.WordStart)
            kept.TryRemove(path, out _);
        if (prefetching.TryGetValue(path, out var pending) && first < pending.WordStart)
            prefetching.TryRemove(path, out _);
    }

    public void Forget(string path)
    {
        kept.TryRemove(path, out _);
        prefetching.TryRemove(path, out _);
        speculations.TryRemove(path, out _);
        edits.TryRemove(path, out _);
    }

    // A list computed for an older version is kept only when no edit since then changed the text before its word.
    private bool Keep(string path, KeptList list, int version)
    {
        if (edits.TryGetValue(path, out var log) && log.EarliestSince(version) < list.WordStart)
            return false;

        kept[path] = list;
        return true;
    }

    /// <summary>Gets a suggestion's description and the edits accepting it makes besides inserting it, such as a <c>using</c> directive.</summary>
    public static async ValueTask<CompletionDetails?> ResolveAsync(CompletionEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Data is not CompletionData data || CompletionService.GetService(data.Document) is not { } service)
            return null;

        var description = await service.GetDescriptionAsync(data.Document, data.Item, cancellationToken).ConfigureAwait(false);
        var text = description?.Text.ReplaceLineEndings("\n").Trim() ?? "";
        var lineBreak = text.IndexOf('\n', StringComparison.Ordinal);
        var detail = lineBreak < 0 ? text : text[..lineBreak];
        var documentation = lineBreak < 0 ? null : text[(lineBreak + 1)..].Trim();

        var change = await service.GetChangeAsync(data.Document, data.Item, commitCharacter: null, cancellationToken).ConfigureAwait(false);
        var oldText = await data.Document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var additional = AdditionalChanges(oldText, change.TextChanges, data.Span, entry.InsertText ?? entry.Label);
        return new CompletionDetails(detail.Length > 0 ? detail : null, string.IsNullOrEmpty(documentation) ? null : documentation, additional);
    }

    // The compiler may merge the inserted word and a using directive into one change; this splits off what lies before the word.
    internal static IReadOnlyList<TextChange> AdditionalChanges(SourceText oldText, ImmutableArray<Microsoft.CodeAnalysis.Text.TextChange> changes, TextSpan word, string inserted)
    {
        if (changes.IsDefaultOrEmpty)
            return [];

        var newText = oldText.WithChanges(changes).ToString();
        var old = oldText.ToString();
        var suffix = old.AsSpan(word.End);
        if (!newText.AsSpan().EndsWith(suffix) || newText.Length < suffix.Length + inserted.Length)
            return [];

        var mainStart = newText.Length - suffix.Length - inserted.Length;
        if (!newText.AsSpan(mainStart, inserted.Length).SequenceEqual(inserted))
            return [];

        var oldPrefix = old.AsSpan(0, word.Start);
        var newPrefix = newText.AsSpan(0, mainStart);
        if (oldPrefix.SequenceEqual(newPrefix))
            return [];

        var head = oldPrefix.CommonPrefixLength(newPrefix);
        var tail = 0;
        while (tail < oldPrefix.Length - head && tail < newPrefix.Length - head && oldPrefix[^(tail + 1)] == newPrefix[^(tail + 1)])
            tail++;

        var replaced = TextSpan.FromBounds(head, oldPrefix.Length - tail);
        return [new TextChange(replaced, newPrefix[head..^tail].ToString())];
    }

    private static CompletionCandidate ToCandidate(RoslynCompletionItem item, CompletionData data)
    {
        var label = item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix;
        var insert = item.Properties.TryGetValue("InsertionText", out var insertion) ? insertion : item.DisplayText;
        var (kind, group) = Classify(item.Tags);
        return new CompletionCandidate(
            label,
            kind,
            group,
            Detail: string.IsNullOrEmpty(item.InlineDescription) ? null : item.InlineDescription,
            InsertText: insert == label ? null : insert,
            FilterText: item.FilterText == label ? null : item.FilterText,
            Data: data);
    }

    private static (CompletionKind Kind, byte Group) Classify(ImmutableArray<string> tags)
    {
        foreach (var tag in tags)
        {
            switch (tag)
            {
                case WellKnownTags.Local or WellKnownTags.RangeVariable:
                    return (CompletionKind.Variable, 0);
                case WellKnownTags.Parameter:
                    return (CompletionKind.Variable, 0);
                case WellKnownTags.Method or WellKnownTags.ExtensionMethod:
                    return (CompletionKind.Method, 1);
                case WellKnownTags.Property:
                    return (CompletionKind.Property, 1);
                case WellKnownTags.Field:
                    return (CompletionKind.Field, 1);
                case WellKnownTags.Event:
                    return (CompletionKind.Event, 1);
                case WellKnownTags.Constant:
                    return (CompletionKind.Constant, 1);
                case WellKnownTags.EnumMember:
                    return (CompletionKind.EnumMember, 1);
                case WellKnownTags.Class:
                    return (CompletionKind.Class, 2);
                case WellKnownTags.Structure:
                    return (CompletionKind.Struct, 2);
                case WellKnownTags.Interface:
                    return (CompletionKind.Interface, 2);
                case WellKnownTags.Enum:
                    return (CompletionKind.Enum, 2);
                case WellKnownTags.Delegate:
                    return (CompletionKind.Class, 2);
                case WellKnownTags.TypeParameter:
                    return (CompletionKind.TypeParameter, 2);
                case WellKnownTags.Namespace:
                    return (CompletionKind.Module, 2);
                case WellKnownTags.Keyword:
                    return (CompletionKind.Keyword, 3);
                case WellKnownTags.Snippet:
                    return (CompletionKind.Snippet, 4);
                case WellKnownTags.Operator:
                    return (CompletionKind.Operator, 1);
            }
        }

        return (CompletionKind.Text, 5);
    }

    private static int WordEnd(Runesmith.Text.TextSnapshot snapshot, int offset)
    {
        var end = offset;
        while (end < snapshot.Length && (char.IsLetterOrDigit(snapshot[end]) || snapshot[end] == '_'))
            end++;
        return end;
    }

    /// <summary>What resolving a suggestion needs: the compiler's item and the document version it was made for.</summary>
    internal sealed record CompletionData(RoslynCompletionItem Item, Document Document, TextSpan Span);

    /// <summary>A member list computed before its <c>.</c> was typed, and whether the next edit typed it.</summary>
    private sealed class Speculation(int position, int version, Task<KeptList?> list)
    {
        private volatile bool isTyped;

        public int Position => position;

        public int Version => version;

        public Task<KeptList?> List => list;

        public bool IsTyped
        {
            get => isTyped;
            set => isTyped = value;
        }
    }

    /// <summary>The earliest offset each recent version's edits started at.</summary>
    private sealed class EditLog
    {
        private const int Kept = 64;
        private readonly Queue<(int Version, int Start)> entries = new();

        public void Add(int version, int start)
        {
            lock (entries)
            {
                entries.Enqueue((version, start));
                if (entries.Count > Kept)
                    entries.Dequeue();
            }
        }

        public int EarliestSince(int version)
        {
            lock (entries)
            {
                if (entries.Count == Kept && entries.Peek().Version > version + 1)
                    return 0;
                return entries.Where(entry => entry.Version > version).Select(entry => entry.Start).DefaultIfEmpty(int.MaxValue).Min();
            }
        }
    }

    /// <summary>A computed list kept for its word, with each candidate's character mask, and the candidates that could still match the
    /// word as typed so far: a candidate that lacks a typed character cannot match a longer word either.</summary>
    private sealed class KeptList
    {
        private readonly CompletionCandidate[] candidates;
        private readonly ulong[] masks;
        private int[] survivors;
        private string typed = "";

        public KeptList(TextSpan span, CompletionCandidate[] candidates, char? trigger)
        {
            Span = span;
            Trigger = trigger;
            this.candidates = candidates;
            masks = new ulong[candidates.Length];
            for (var i = 0; i < candidates.Length; i++)
                masks[i] = CompletionMatcher.MaskOf(candidates[i].FilterText ?? candidates[i].Label);
            survivors = [.. Enumerable.Range(0, candidates.Length)];
        }

        public TextSpan Span { get; }

        public int WordStart => Span.Start;

        /// <summary>Gets the character typed to ask for the list, such as <c>.</c>, or null when it was asked for otherwise.</summary>
        public char? Trigger { get; }

        public void AddMatching(CompletionSink sink)
        {
            var now = sink.Typed;
            lock (candidates)
            {
                var pool = now.StartsWith(typed, StringComparison.Ordinal) ? survivors : [.. Enumerable.Range(0, candidates.Length)];
                var mask = CompletionMatcher.MaskOf(now);
                var next = new List<int>(pool.Length);
                foreach (var index in pool)
                {
                    if ((mask & ~masks[index]) != 0)
                        continue;

                    next.Add(index);
                    sink.Add(candidates[index]);
                }

                survivors = [.. next];
                typed = now;
            }
        }
    }

    internal static string Describe(IEnumerable<TaggedText> parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
            text.Append(part.Tag == TextTags.LineBreak ? "\n" : part.Text);
        return text.ToString();
    }
}
