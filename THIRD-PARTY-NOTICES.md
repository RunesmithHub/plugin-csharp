# Third-party notices

The C# plugin does not ship the software below. It downloads it, from the project's own release page, the first time you need it, and
keeps it in the plugin's storage folder. Its license is reproduced here because it runs on your computer on the plugin's behalf.

The plugin package lists the libraries it does ship in its own `THIRD-PARTY-NOTICES.md`, which the plugin hub writes when it packages a
release.

## netcoredbg

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
