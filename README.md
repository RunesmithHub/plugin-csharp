# C# for Runesmith

The official C# plugin for [Runesmith](https://github.com/RunesmithHub/Runesmith), `runesmith.csharp`. It adds:

- C# highlighting for `.cs` and `.csx` files, and highlighting for the other .NET languages: Razor, F# and Visual Basic;
- completion, hover, go to definition, signature help, problems, quick fixes, rename, formatting and inlay hints from Runesmith's own C#
  analyzer, built on the C# compiler and shipped with the plugin;
- completion that inserts the parentheses a call needs, such as after a method's name or a type after `new`, with the caret between them
  when the call takes arguments;
- find references, go to implementation, the occurrences of the name under the caret, the outline and breadcrumbs, go to symbol in the
  file and in the folder, folding, expand selection, call and type hierarchies, and names colored by what they mean;
- **Build** with `dotnet build`, with every error and warning in the **Problems** panel;
- run configurations for .NET projects, .NET tests and any `dotnet` command;
- debugging .NET projects with [netcoredbg](https://github.com/Samsung/netcoredbg), which it downloads the first time you debug;
- finding the .NET SDKs on your computer, and downloading new ones from Microsoft's release metadata, checked against their SHA-512
  checksums.

The [C# documentation](https://docs.runesmith.dev/plugins/csharp) covers every feature, how projects are found and what the
analyzer does not do yet.

## Capabilities

| Capability | Why |
| --- | --- |
| `network` | Downloads the .NET SDKs you choose to install, from `builds.dotnet.microsoft.com`, and netcoredbg the first time you debug, from `github.com` and `release-assets.githubusercontent.com`. |
| `process` | Runs `dotnet` to build, run and test your projects and to load them for the C# analyzer, and starts netcoredbg to debug them. |
| `filesystem` | Reads your projects' build files and keeps the .NET SDKs it installs. |
| `environment` | Finds the .NET SDKs on your `PATH` and in `DOTNET_ROOT`. |
| `native` | The MSBuild library the C# analyzer uses to load your projects calls into the operating system. |
| `dynamic-code` | The C# compiler services it hosts load their own components when they start. |

## Debugging

**Debug** in the run widget, on a .NET project configuration, builds the project and starts its assembly under netcoredbg, Samsung's .NET
debugger. The first time you debug, the plugin downloads netcoredbg 3.2.0-1092 from its GitHub release page, checks the download against
the SHA-256 checksum pinned in `src/Runesmith.CSharp/Debugging/NetcoredbgRelease.cs`, and keeps it in the plugin's storage folder, in
`tools/netcoredbg/<version>/<platform>`. It is available for Linux x64 and Arm64, Windows x64 and macOS on Apple silicon.

netcoredbg is MIT licensed. The plugin downloads it rather than ships it, so its license notice is in this repository's
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) rather than in the plugin package.

## Install

Install **C#** from Runesmith's plugin manager (**Tools › Plugins**), or from its page on the plugin hub, https://hub.runesmith.dev.
The analyzer needs the [.NET SDK](https://dot.net) to load your projects; you can also download one in **File › SDKs...**.

## Build and test

You need the .NET SDK version in `global.json`. The Runesmith packages come from the plugin hub's feed, which `nuget.config` sets up.

```sh
dotnet build plugin-csharp.slnx -warnaserror
dotnet format plugin-csharp.slnx --verify-no-changes
dotnet test --solution plugin-csharp.slnx
```

The C# analyzer, the engine behind completion, navigation, problems and the other editor features, lives in this repository, in
`src/Runesmith.CSharp/Engine`, with its tests in `tests/Runesmith.CSharp.Tests/Engine`.

The test that downloads netcoredbg and debugs a console app runs only with `RUNESMITH_TEST_DEBUGGER=1` set.

`dotnet build src/Runesmith.CSharp -t:InstallPlugin` installs your build into Runesmith's plugins folder; restart Runesmith to load it.

## Translations

The plugin's text is in English in `src/Runesmith.CSharp/locales/en/runesmith.csharp.ftl`, a [Fluent](https://projectfluent.org) file.
Text for a new message goes there, and the code reads it from the generated `Strings` class. A language pack translates the file; see
[Language packs](https://docs.runesmith.dev/plugins/language-packs).

## Documentation

Runesmith's documentation, including how plugins work and how to write one, is at https://docs.runesmith.dev.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE); [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) covers the C# compiler
libraries the analyzer is built on, and netcoredbg, which the plugin downloads.
