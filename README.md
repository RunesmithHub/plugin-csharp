# C# for Runesmith

The official C# plugin for [Runesmith](https://github.com/RunesmithHub/Runesmith), `runesmith.csharp`. It adds:

- C# highlighting for `.cs` and `.csx` files;
- completion, hover, go to definition, signature help, problems, quick fixes, rename, formatting and inlay hints from Runesmith's own C#
  analyzer, built on the C# compiler and shipped with the plugin;
- **Build** with `dotnet build`, with every error and warning in the **Problems** panel;
- run configurations for .NET projects, .NET tests and any `dotnet` command;
- finding the .NET SDKs on your computer, and downloading new ones from Microsoft's release metadata, checked against their SHA-512
  checksums.

The [C# documentation](https://runesmithhub.github.io/Runesmith/plugins/csharp) covers every feature, how projects are found and what the
analyzer does not do yet.

## Capabilities

| Capability | Why |
| --- | --- |
| `network` | Downloads the .NET SDKs you choose to install, from `builds.dotnet.microsoft.com`. |
| `process` | Runs `dotnet` to build, run and test your projects, and to load them for the C# analyzer. |
| `filesystem` | Reads your projects' build files and keeps the .NET SDKs it installs. |
| `environment` | Finds the .NET SDKs on your `PATH` and in `DOTNET_ROOT`. |
| `native` | The MSBuild library the C# analyzer uses to load your projects calls into the operating system. |
| `dynamic-code` | The C# compiler services it hosts load their own components when they start. |

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

`dotnet build src/Runesmith.CSharp -t:InstallPlugin` installs your build into Runesmith's plugins folder; restart Runesmith to load it.

## Documentation

Runesmith's documentation, including how plugins work and how to write one, is at https://runesmithhub.github.io/Runesmith/.

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
