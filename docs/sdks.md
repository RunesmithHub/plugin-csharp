# .NET SDKs

What the C# plugin adds to Runesmith's **SDKs** page; the page itself is described in
[SDKs](https://docs.runesmith.dev/guide/sdks).

## The default

When the default .NET SDK is one Runesmith installed, **Build** runs the `dotnet` of Runesmith's .NET folder, with `DOTNET_ROOT` set to it
and the folder first on the `PATH`. Otherwise **Build** uses the `dotnet` on the `PATH`.

## Downloads

Pick the **Channel**, such as 10.0, then the **Version**. Releases come from Microsoft's .NET release metadata, with a SHA-512 checksum
for each archive. Runesmith installs them in one .NET folder, `sdks/dotnet`, with every SDK side by side, so `global.json` picks between
them. The list leaves out channels that are out of support, unless you have an SDK of that channel.

An update is a newer SDK in the same channel. Removing an SDK deletes its folder from the .NET folder, and the whole folder with the last
SDK.

## Where the plugin looks

Runesmith's own .NET folder, `DOTNET_ROOT`, the folder of the `dotnet` on the `PATH` (following links), `~/.dotnet`, and the usual
install folders: `/usr/share/dotnet`, `/usr/lib/dotnet` and `/usr/local/share/dotnet` on Linux, `/usr/local/share/dotnet` on macOS and
`Program Files\dotnet` on Windows.
