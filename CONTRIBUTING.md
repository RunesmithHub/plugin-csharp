# Contributing

Thanks for helping. For a bug, open an issue with the steps to reproduce it; for a security problem, follow [SECURITY.md](SECURITY.md)
instead. For a larger change, open an issue first, so the approach is agreed on before you write the code.

## Build and test

You need the .NET SDK version in `global.json`.

```sh
dotnet build plugin-csharp.slnx -warnaserror
dotnet format plugin-csharp.slnx --verify-no-changes
dotnet test --solution plugin-csharp.slnx
```

`dotnet format plugin-csharp.slnx` fixes the formatting. When you change a package reference, commit the updated `packages.lock.json` files.

## Check the release

The plugin is released through the Runesmith Plugin Hub, which builds, packages and analyses it. Run the same checks before you open a pull
request:

```sh
dotnet tool install --global RunesmithHub.Tool --version 0.1.3
rshub check .
```

## Commits

Write each commit message as a [Conventional Commit](https://www.conventionalcommits.org/en/v1.0.0/), such as
`fix(build): report errors from the solution file`. The types are `feat`, `fix`, `perf`, `refactor`, `test`, `docs`, `ci`, `chore` and
`revert`.

## License

Contributions you submit are licensed under the [Apache License 2.0](LICENSE), as section 5 of the license describes.
