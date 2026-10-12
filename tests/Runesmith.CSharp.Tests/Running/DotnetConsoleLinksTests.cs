using Runesmith.CSharp.Running;

namespace Runesmith.CSharp.Tests.Running;

public sealed class DotnetConsoleLinksTests
{
    [Theory]
    [InlineData("   at App.Program.Main(String[] args) in /home/me/App/Program.cs:line 17", "/home/me/App/Program.cs", 17, 0)]
    [InlineData(@"   at App.Program.Main() in C:\src\App\Program.cs:line 9", @"C:\src\App\Program.cs", 9, 0)]
    public void FindsDotnetStackFrames(string line, string path, int number, int column)
    {
        var link = Assert.Single(new DotnetConsoleLinks().FindLinks(line));

        Assert.Equal(path, link.Path);
        Assert.Equal(number, link.Line);
        Assert.Equal(column, link.Column);
        Assert.False(link.IsSourceRelative);
        Assert.StartsWith(path, line[link.Start..], StringComparison.Ordinal);
    }
}
