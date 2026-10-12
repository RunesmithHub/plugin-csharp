using System.Composition;
using Runesmith.Sdk.Workspace;

namespace Runesmith.CSharp;

/// <summary>Leaves the folders of .NET builds and of Visual Studio out of the Explorer, Quick Open and search.</summary>
[Export(typeof(IFileExcludeContributor))]
public sealed class DotnetFolders : IFileExcludeContributor
{
    public IEnumerable<string> Globs => ["**/bin", "**/obj", "**/.vs"];
}
