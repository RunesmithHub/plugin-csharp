# Third-party notices

The C# plugin is licensed under the Apache License 2.0; see [LICENSE](LICENSE). It uses the third-party software below.

The plugin package lists every library it ships in its own `THIRD-PARTY-NOTICES.md`, which the plugin hub writes when it packages a
release.

## Dependencies

| Component | License | Used by |
| --- | --- | --- |
| The C#, Razor, F# and Visual Basic TextMate grammars, in `grammars`, as [TextMateSharp.Grammars](https://github.com/danipen/TextMateSharp) 2.0.4 carries them from [Visual Studio Code](https://github.com/microsoft/vscode/tree/main/extensions) | MIT | Syntax highlighting |
| [Roslyn](https://github.com/dotnet/roslyn), the C# compiler: Microsoft.CodeAnalysis.CSharp.Features, Microsoft.CodeAnalysis.CSharp.Workspaces and Microsoft.CodeAnalysis.Workspaces.MSBuild 5.9.0 | MIT | The C# analyzer |
| What Roslyn brings with it: [MSBuild](https://github.com/dotnet/msbuild)'s Microsoft.Build.Framework, [Humanizer](https://github.com/Humanizr/Humanizer), [Microsoft.DiaSymReader](https://github.com/dotnet/symreader), [Microsoft.VisualStudio.SolutionPersistence](https://github.com/microsoft/vs-solutionpersistence) and Microsoft.Extensions packages from the [.NET runtime](https://github.com/dotnet/runtime) | MIT | The C# analyzer |
| [System.Composition](https://github.com/dotnet/runtime) 10.0.12 and the [.NET runtime](https://github.com/dotnet/runtime) | MIT | The C# analyzer's compiler services |

Roslyn and the libraries it brings are licensed under the MIT License, whose text is in the .NET section below.

## Downloaded when needed

The plugin does not ship the software below. It downloads it, from the project's own release page, the first time you need it, and keeps
it in the plugin's storage folder. Its license is reproduced here because it runs on your computer on the plugin's behalf.

### netcoredbg

The .NET debugger, downloaded the first time you debug a .NET project: version 3.2.0-1092, from
https://github.com/Samsung/netcoredbg/releases/tag/3.2.0-1092, built from https://github.com/Samsung/netcoredbg. The release also carries
the C# compiler libraries (Microsoft.CodeAnalysis) and dbgshim, both MIT licensed by the .NET Foundation, and netcoredbg builds in the
components of its `third_party` folder under their own licenses.

```text
MIT License

Copyright (c) 2017 Samsung Electronics Co., LTD

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## .NET

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

The .NET runtime includes further third-party components; their notices are at https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT.
