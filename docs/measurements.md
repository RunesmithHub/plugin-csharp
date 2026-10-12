# The editor with the analyzers

How the C# and Java analyzers were tested, delivered and measured together in the editor. Each analyzer's own design, goals and results
are in its plugin: [C#](design.md) and [Java](https://github.com/RunesmithHub/plugin-java/blob/main/docs/design.md).

## Performance goals

The goals hold on the reference machine (a mini PC with NixOS) in the Release build, and the benchmarks check them.

### The editor with the analyzers

| Measurement | Target |
| --- | --- |
| `completion.shown` from an analyzer, C# | p95 under 50 ms |
| `completion.shown` from an analyzer, Java | p95 under 25 ms |
| `input_to_render` while analyzers work | p95 under 4 ms |

## Testing and benchmarks

- **Unit tests** for each project, with xUnit v3: the core's version mapping, scheduler and completion ranking; Java's lexer and parser for
  every feature of every version, error recovery and incremental reparsing; the class file reader against real `ct.sym` and JARs; Java
  semantics on small projects; C# on small temporary projects for each request.
- **Conformance**: parse every file of a JDK's `src.zip`; complete at every member access in a corpus of Java and C# files and check the
  expected member is in the list.
- **Benchmarks** with BenchmarkDotNet in `benchmarks/Runesmith.Benchmarks`: lexing and parsing, reparsing, completion for both languages,
  JDK loading cold and cached, and the core's ranking.
- **End to end**: the editor benchmark session from [Instant editing](instant-editing.md), on a C# and a Java project, compared against the
  targets above.

## Delivery

1. The core, with the in-process bridge, the scheduler and the metrics.
2. The C# analyzer, replacing the external server in the C# plugin.
3. The Java syntax, with the `src.zip` conformance test.
4. The Java class library, projects and semantics, and the Java plugin.
5. The LSP host executable.
6. The editor's completion engine from [Instant editing](instant-editing.md), on top of the analyzers.

Each step is tested and benchmarked before the next, and the results are added to this document.

## Results

Measured on the reference machine in the Release build. The analyzers' own numbers come from BenchmarkDotNet and the tests; the editor's
from the typing benchmark (`benchmarks/Runesmith.TypingBenchmark`), which types sixteen lines into a method at about 140 words a minute
once the folder's projects are loaded, and reports the median over five runs of each percentile.

### The editor with the analyzers

| Measurement | C# | Java | Target | Met |
| --- | --- | --- | --- | --- |
| `completion.shown` p95 | 38.2 ms | 27.6 ms | C# under 50 ms, Java under 25 ms | C# yes, Java no |
| `edit` p95 | 0.8 ms | 0.4 ms | under 2 ms | Yes |
| `input_to_render` p95 | 6.8 ms | 7.4 ms | under 4 ms | No, see below |
| `ui.stall` p95 | 0.9 ms | 0.5 ms | | |

`input_to_render` stays within one frame, which is the goal [Instant editing](instant-editing.md) settled on after measuring that the
rest is waiting for the display's next frame; the 4 ms target above predates that finding.

### What the measurements changed

- **The JIT was the largest cost.** A profile of the typing session showed the runtime recompiling the compiler's code on one core the
  whole time. Turning off profile-guided recompilation halved the processor time and the C# requests' p95; precompiling with ReadyToRun
  made no measurable difference and was left out.
- **Work ahead of the user pays for itself.** Computing the member list after every identifier character looks wasteful, but limiting
  it to names with members made completion requests slower (13 ms to 22 ms at p95), because the speculation also kept the compiler's
  model of the method current.
- **Garbage collection was not the problem.** Full collections came from the software renderer allocating a framebuffer per frame, which
  a retained framebuffer removed (69 full collections per session to 3); with that, collections pause the process for under 1 ms per
  second of typing, so the analyzers stay in the editor's process.
- **Composing the compiler's services took 520 ms on the UI thread** at startup, and now happens in the background.

### Not met yet

- **Java completion in a large project** (11.7 ms, against 0.1 ms on the generated project): the requests in `java.util` spend their
  time on scopes and imports of a large source set; caching them per file version is the next step.
- **C# hover** (36.8 ms): the compiler's description service is slower than the completion path; a cache of descriptions per symbol is
  the next step.
