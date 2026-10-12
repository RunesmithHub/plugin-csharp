# Instant editing

Typing and completion must feel instant: a typed character is on screen in the next frame, and suggestions keep up with typing without
ever making it wait. This document sets measurable goals, records where the time goes today, and describes the design that meets the
goals.

## How it is measured

Every number below comes from Runesmith's own measurements (`System.Diagnostics.Metrics`, meters `Runesmith.Editor`, `Runesmith.Lsp` and
`Runesmith.Languages`), reported by **Help > Performance Report** as percentiles over the last 4,000 samples, and readable live with
`dotnet-counters monitor --counters Runesmith.Editor,Runesmith.Lsp`.

The benchmark session opens the Runesmith solution itself, waits for csharp-ls to load it, and types four lines of ordinary C# into
`src/Runesmith.Editor/TextArea.cs` at 70 ms per key, about 140 words per minute, accepting nothing. It runs on the reference machine (a
mini PC, NixOS, Xvfb at 60 Hz), in the Release build unless noted.

| Measurement | What it covers |
| --- | --- |
| `input_to_render` | From the key event to the end of drawing the changed text. |
| `input_to_frame` | From the key event to the start of the frame after it was drawn; it includes waiting for that frame, so it is up to one frame longer than what the user sees. |
| `edit` | Applying an edit and everything listening to it. |
| `render` | Drawing the visible text. |
| `completion.shown` | From asking for suggestions to the first frame that shows the list. |
| `completion.filter` | Filtering and sorting the list for the typed word. |
| `lsp.request`, `lsp.response_bytes` | A language server's answer time and size, per method. |

## Where the time goes today

| Measurement | p50 | p95 | p99 |
| --- | ---: | ---: | ---: |
| `input_to_render` | 2.8 ms | 16.3 ms | 24.2 ms |
| `input_to_frame` | 18.3 ms | 33.8 ms | 40.2 ms |
| `edit` | 1.3 ms | 10.9 ms | 16.6 ms |
| `render` | 0.2 ms | 0.7 ms | 5.4 ms |
| `completion.filter` | 3.4 ms | 13.6 ms | 17.1 ms |
| `completion.shown` | 46 ms | 156 ms | 156 ms |
| `lsp.request` completion | 26 ms | 58 ms | 127 ms |
| `lsp.response_bytes` completion | 177 KB | 199 KB | 199 KB |
| suggestions per answer | 1,004 | 1,126 | 1,126 |

In the Debug build the editor numbers are about 10 to 20 percent higher; the shape is the same.

What the numbers say:

1. **Completion asks the server again on every key press.** csharp-ls marks every list as incomplete, and the editor took that to mean
   "ask again when the word grows". 120 requests for four lines of code, each about 1,000 suggestions and 177 KB of JSON: at 140 words a
   minute that is 2.5 MB of JSON a second, parsed, copied and converted, while the server recomputes the same list.
2. **The list is filtered twice per key press, on the UI thread, from scratch.** Once for the caret move and once for the typed
   character, scoring all 1,000 suggestions with allocations each time: 3.4 ms typical, 13.6 ms at p95.
3. **The popup rebuilds every row on every update**, because each filter result is a new list.
4. **Drawing text is not the problem**: 0.2 ms typical. The slow key presses are the ones that run completion work inside the edit.

## Goals

The goals are set from human perception and from what this machine and csharp-ls can do: a frame at 60 Hz is 16.7 ms; 100 ms is the limit
for a response to feel instantaneous; a language server cannot answer faster than it computes (csharp-ls: 26 ms typical, 58 ms p95 for
this solution).

| Goal | Measurement | Target |
| --- | --- | --- |
| The work a key press causes is done at once | `edit` | p95 under 2 ms |
| A typed character shows in the next frame | `input_to_render` | p95 within one frame, 16.7 ms at 60 Hz |
| Typing more of a word updates suggestions in the same frame | `completion.filter` | p95 under 2 ms for 2,000 suggestions |
| Typing within a word never asks the server again | requests per typed word | 1 (from 6 or more today) |
| Suggestions appear without delay where the editor already knows them | `completion.shown`, local or cached | p95 under 16 ms (one frame) |
| Suggestions from the server appear well within the instant limit | `completion.shown`, from the server | p95 under 100 ms, of which Runesmith's own share under 10 ms |
| The JSON parsed for completion per key press | `lsp.response_bytes` per typed character | at least 5 times less than today |

The goals are checked with the same benchmark session; the results go at the end of this document.

The first version of this table asked for `input_to_render` under 4 ms at p95. Measuring showed that once the work of a key press is done,
what remains is waiting for the next frame: Avalonia draws at the display's rate, so a key pressed just after a frame waits up to one
frame. No work can remove that, so the goal is now split into the work itself (`edit`) and the frame it lands in.

## Design

### 1. One request per word, filtered in the editor

A completion session starts when a word starts (its first character) or on a trigger character such as `.`. It asks every provider once.
While the user keeps typing the same word, the editor filters the suggestions it has; it does not ask again.

It asks again only when:

- the caret leaves the word, or the user deletes back to before its start (a new session);
- nothing matches any more and a provider said its list was incomplete;
- the user stops typing for 150 ms and a provider's list was incomplete, to refresh the list in the background without changing what is
  selected.

A provider that marks every list as incomplete therefore costs one request per word plus at most one refresh per pause, instead of one
per key press.

### 2. Filtering that narrows

Each suggestion's filter text is prepared once per session: lower-cased, and with a 64-bit mask of the characters it contains. Filtering:

- rejects a suggestion in one AND when the typed word has a character it lacks;
- scores the rest with the existing matcher, without allocating;
- when the typed word only grew, filters the previous matches instead of the whole list, because a suggestion that did not match a
  shorter word cannot match a longer one;
- keeps the top results sorted with a partial sort of the visible rows plus a full sort only when the user scrolls.

### 3. A list control that draws its rows

The suggestion list is drawn by one control, the way the editor draws text: it formats only the visible rows and reuses their text
layouts, so updating it for a new filter costs a redraw of about twelve rows and no new controls. It handles the keyboard through the
editor, the mouse itself, and keeps the selected suggestion selected while the list changes underneath it.

### 4. Suggestions the editor knows without asking

Some suggestions need no server and are ready in the frame the session starts:

- **Words in the document**: identifiers in the open document, kept up to date from the changed lines of each edit.
- **Keywords** of the document's language, which language definitions can declare.
- **Recent member lists**: after a `.`, the list the server gave the last time for the same text before the dot in the same file, such as
  `System.Console.`, is shown at once and replaced when the new answer arrives.

Server results are merged into the open list as they arrive: a server suggestion replaces a document word with the same name, the
selection stays on the same suggestion, and the list never jumps while the user types.

### 5. Cheaper answers from the server

- The answer is parsed once, straight from the message into the result type, on a background thread, instead of being parsed, copied
  and parsed again.
- Converting it to the editor's suggestions happens on the background thread too, and prepares the filter data at the same time.
- An answer for an old version of the text is still used: its suggestions are moved through the edits made since, and filtered for the
  word as it is now.

### 6. Nothing waits for completion while typing

The edit path does no completion work: the text change, the caret move and drawing come first. Completion work for a key press runs after
the edit, once per key press, and costs at most the filtering budget above.

## Later: completion in the process

The largest remaining cost is the server itself: computing and sending a list it computed a moment ago. A C# plugin that hosts the
compiler's completion service in the process would answer from the editor's own text, with no serialization and no copying, and could
compute lists for the next likely word ahead of time. It costs memory (a mid-sized solution loaded in the process) and a slower start, and
a crash in it must not take the editor down.

It is worth doing when the measurements after this design show that the server's share of `completion.shown` is what stands between
Runesmith and the goals. The gate for starting it: `completion.shown` from the server above 100 ms at p95 on the benchmark session, with
Runesmith's own share under 10 ms.

## Results

The benchmark session after steps 1, 2, 3 and 6, still with the external C# server, in the Release build:

| Measurement | Before | After | Goal |
| --- | ---: | ---: | --- |
| `edit` p95 | 10.9 ms | 0.6 ms | under 2 ms: met |
| `input_to_render` p50 | 2.8 ms | 0.8 ms | |
| `input_to_render` p95 | 16.3 ms | 12.2 ms | within one frame: met |
| `completion.filter` p95 | 13.6 ms | 0.6 ms | under 2 ms: met |
| `completion.popup_update` p95 | 17.7 ms | 2.3 ms | |
| Completion requests for the session | 120 | 33 | one per word: met |
| `completion.shown` p95 | 156 ms | 203 ms | under 100 ms: not met, bound by the server |

What made the difference:

- One request per word instead of one per key press, with the list filtered in the editor and narrowed as the word grows.
- A list control that draws its rows instead of creating controls for them.
- Completion updates after the typed character is drawn, never inside the edit.
- The Problems panel rebuilt itself every time the server published problems, which it does after almost every key press while code is
  half typed: 84 rebuilds of up to 9 ms in one session. It now rebuilds at most once per 250 ms pause, and not while it is hidden.

`completion.shown` is the time the server takes to answer the first request of a word, which the editor cannot shorten: that is what the
[language services](language-services.md) replace.

### With the in-process analyzers

After [language services](language-services.md) replaced the external server, measured with the typing benchmark on the loaded Runesmith
solution, the median of five runs:

| Measurement | With the external server | With the C# analyzer | Goal |
| --- | ---: | ---: | --- |
| `edit` p95 | 0.6 ms | 0.8 ms | under 2 ms: met |
| `input_to_render` p95 | 12.2 ms | 6.8 ms | within one frame: met |
| `completion.filter` p95 | 0.6 ms | 0.1 ms | under 2 ms: met |
| `completion.shown` p95 | 203 ms | 38.2 ms | under 100 ms: met |
| Runesmith's own share of a request | | 1.3 ms | under 10 ms: met |

The first benchmark session typed with an external tool at 70 ms per key; the typing benchmark types inside Runesmith on the same
schedule, so the two columns are close but not identical setups.
