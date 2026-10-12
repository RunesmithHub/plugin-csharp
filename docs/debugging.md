# Debugging .NET

The design of the plugin's debug engine. The model it plugs into, a Debug Adapter Protocol client, is described in Runesmith's own
[debugging design](https://github.com/RunesmithHub/Runesmith/blob/main/docs/design/debugging.md).

## netcoredbg

netcoredbg is Samsung's open-source .NET debugger (MIT), with release builds for Linux (x64, arm64), macOS (arm64) and Windows (x64), and
speaks DAP when started with its DAP interpreter option. The C# plugin downloads it on first use into Runesmith's data folder (`tools/netcoredbg/<version>`),
verified like an SDK, and starts it with the launch plan's program, arguments, working directory and environment. The .NET debugger
included with Microsoft's own tools is licensed only for Microsoft's products and is not an option.
