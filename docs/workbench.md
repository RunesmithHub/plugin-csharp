# C# in the workbench

What the plugin adds to Runesmith's run configurations, New Project dialog and SDKs page, whose design is in Runesmith's own
[workbench design](https://github.com/RunesmithHub/Runesmith/blob/main/docs/design/workbench.md).

## Run configurations

.NET project (project, target framework, launch profile from `launchSettings.json`, configuration, arguments, environment,
  working directory, SDK), .NET tests (`dotnet test` with a filter) and .NET command (any `dotnet` command).

## SDKs

Releases come from Microsoft's release metadata: the release index lists the channels (11.0, 10.0, ...) with
  their support phase and latest SDK, and each channel's `releases.json` lists every SDK with its download per platform and its SHA-512
  hash. Runesmith installs SDKs side by side in one .NET root, as the official install script does, so one `dotnet` serves them all and
  `global.json` picks between them. Found SDKs come from `DOTNET_ROOT`, the `dotnet` on the `PATH` and the usual install folders. Update
  means a newer patch in the same channel; Runesmith offers it and keeps the older one until the user removes it.

## Templates

The templates are in their own plugin, `runesmith.csharp-templates`, so a user can switch them off without losing the language. It
reads the templates the .NET SDK itself ships and the ones the user installed with
  `dotnet new install`: the template packages in the SDK's `templates/<version>` folder and in the template engine's packages folder. Each
  package's `.template.config/template.json` describes the template's name, identity, short name, classifications, language and
  parameters, and its `localize` files the display text. Parameters become options: a `choice` becomes a `Choice`, a `bool` a `Toggle`,
  text and numbers a `Text` or `Number`; a template's `Framework` parameter becomes Target framework next to the SDK picker. Creating runs
  `dotnet new` with the SDK the user picked, the template's identity and the options, then creates a `.slnx` solution and adds the
  project unless the user put both in one folder. Every template a user has installed appears without Runesmith knowing about it.
