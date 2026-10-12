# The C# analyzer

The design of the C# analyzer, which runs on Runesmith's language services. The shared core it builds on, with its scheduler, request
pipeline and completion ranking, is described in Runesmith's own
[language services design](https://github.com/RunesmithHub/Runesmith/blob/main/docs/design/language-services.md).

## Design

The C# analyzer is built on Roslyn, the C# compiler, used as a library. It supports every C# version Roslyn does, from C# 1 to the
newest (C# 14 with Roslyn 5.9), chosen per project from its `LangVersion`.

- **Loading.** `MSBuildWorkspace`, whose build host finds the installed .NET SDK, opens the folder's solution (`.slnx` or `.sln`) or its
  projects, in the background. The compiler's services are composed on a background thread too, since that takes about half a second. Until
  a project is loaded, its files get a default project with the newest language version and the runtime's reference assemblies, so
  completion works from the first second, without project references.
- **Edits.** A change is applied with `SourceText.WithChanges` and `Solution.WithDocumentText`, so Roslyn reparses incrementally and keeps
  everything it can.
- **Completion.** Roslyn's `CompletionService` produces the candidates. The analyzer turns them into the core's compact items, filters,
  ranks and caps them, and keeps Roslyn's items for `ResolveAsync`, which uses `GetDescriptionAsync` for documentation and `GetChangeAsync`
  for extra edits such as adding a `using`. Types from namespaces that are not imported yet are not suggested in this version.
- **Hover** uses the symbol at the position and its documentation comment, formatted as Markdown.
- **Definitions** come from `SymbolFinder`; a symbol from a library without source has no location in this version.
- **Signature help** finds the invocation or object creation around the offset, its candidate methods from the semantic model, and the
  active parameter from the argument list.
- **Diagnostics** are the compiler's for the document, in the background lane, without analyzers.
- **Warming up.** At start, a scratch document runs the completion pipeline once, so the JIT has compiled it. After loading, the analyzer
  completes and describes once inside a method body of each open document, and types a few characters on a copy, so the first real
  requests find the compiler's caches filled.
- **Work ahead of the user.** After a separator, the list for the next word is computed before its first letter. While an identifier is
  typed, the member list after a `.` that may follow is computed on a copy with the dot, and used if the next edit types exactly that dot.
  This costs processor time on every key, and measurements showed that it is also what keeps completion requests fast, so it stays.

## Performance goals

| Measurement | Target |
| --- | --- |
| Completion, warm, Runesmith solution, member access and statement starts | p95 under 40 ms, of which the core's share under 2 ms |
| Completion, the same word asked again | p95 under 5 ms |
| First completion after the solution loaded | under 300 ms |
| Hover and signature help, warm | p95 under 30 ms |
| Diagnostics of an open document after an edit | under 300 ms after the 150 ms pause |

## Results

On the Runesmith solution (31 projects):

| Measurement | Result | Target | Met |
| --- | --- | --- | --- |
| Completion at a word start, measured directly | p95 5.0 ms | p95 under 40 ms | Yes |
| Completion after a dot, measured directly | p95 13.4 ms | p95 under 40 ms | Yes |
| Completion requests while typing in the editor | p95 29.7 ms | p95 under 40 ms | Yes |
| The core's share of a request (queue and ranking) | p95 1.3 ms | under 2 ms | Yes |
| The same word asked again | p95 2.6 ms | p95 under 5 ms | Yes |
| First completion after the solution loaded | 142 ms | under 300 ms | Yes |
| Signature help while typing | p95 16.3 ms | p95 under 30 ms | Yes |
| Hover while typing | p95 36.8 ms | p95 under 30 ms | No |
| Diagnostics of an open document after an edit | p95 52.5 ms | under 300 ms | Yes |
