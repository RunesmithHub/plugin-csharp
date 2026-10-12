# Troubleshooting C#

## C# does not see a project's references

When completion in a C# file does not know the types of the project's packages or other projects, or problems report names that build
fine, the analyzer has not loaded the file's project. The **Language analyzers** channel of the **Output** panel says which solution or
projects it loaded, and why one failed.

- Loading projects needs the [.NET SDK](https://dot.net), as building does.
- Open the folder that holds the solution, or a folder with the projects at most three levels below it. See
  [C# support](csharp.mdx#projects).
- Until the projects finish loading, files have a project of their own without your references.
