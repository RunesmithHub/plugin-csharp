### The C# plugin's text in English, the source every translation starts from. The build turns each message into a member of the
### Strings class, such as Strings.RunProjectName.

## The kinds of run configuration, as Run › Edit Configurations lists them.

run-project-name = .NET project
run-project-description = Runs a console, desktop or web project with dotnet run.
run-tests-name = .NET tests
run-tests-description = Runs a test project's tests with dotnet test, optionally only those a filter selects.
run-command-name = .NET command
run-command-description = Runs a dotnet command line in the folder, such as format or ef database update.

## The fields of a run configuration.

option-project = Project
# Shown in the empty field when no project was found in the folder.
option-project-placeholder = A .csproj or .fsproj file
option-project-to-run = The project to run.
option-project-to-test = The test project.
# The build configuration: Debug or Release.
option-configuration = Configuration
option-environment = Environment variables
option-working-directory = Working directory
# Shown in the empty field: what is used when no folder is chosen.
option-working-directory-open-folder = The open folder
option-working-directory-project-folder = The project's folder
option-sdk-description = Empty uses the default .NET SDK.
option-command = Command
option-command-description = What follows dotnet; quote an argument that has spaces.
# Selects which tests run.
option-filter = Filter
option-filter-description = Runs only the tests that match, as dotnet test --filter selects them; empty runs every test.
option-framework = Target framework
option-framework-placeholder = The project's first framework
option-framework-default = Project default
# $framework is a target framework's name, such as net10.0.
option-framework-default-description = The first framework, { $framework }.
option-launch-profile = Launch profile
option-launch-profile-placeholder = The first profile in launchSettings.json
option-launch-profile-description = A profile from Properties/launchSettings.json, with its environment and URLs.
option-launch-profile-none = None
option-launch-profile-none-description = Run without a launch profile.
option-arguments = Program arguments
# Shown in an empty row of the list of arguments.
option-arguments-placeholder = Argument

## What stops a run configuration from starting. $configuration is the configuration's name, $project the path of its project file.

run-no-command = { $configuration } has no command. Type one in Run › Edit Configurations.
run-no-project = { $configuration } has no project. Choose one in Run › Edit Configurations.
run-project-missing = The project { $project } of { $configuration } does not exist.
run-project-unreadable = The project { $project } could not be read.

## The .NET SDKs in Settings › SDKs.

dotnet-sdk = .NET SDK
# Shown under the title of the dialog that downloads a .NET SDK.
dotnet-sdk-source = Releases from Microsoft. Runesmith installs .NET SDKs side by side in one folder, and global.json picks between them.
# The steps of installing an SDK or the debugger, shown next to the progress bar.
progress-downloading = Downloading
progress-verifying = Verifying
progress-unpacking = Unpacking
progress-installing = Installing
# $version is the SDK's version, such as 10.0.100.
sdk-download-incomplete = The download does not contain the .NET SDK { $version }.
sdk-not-managed = Runesmith did not install the .NET SDK { $version } at { $path }, so it does not remove it.
# $algorithm is the checksum's name, such as SHA512.
sdk-checksum-mismatch = The download does not match its published { $algorithm } checksum, so it was not installed.

## The debugger, netcoredbg, which is downloaded the first time a .NET program is debugged.

# The name of the background task. $version is netcoredbg's version.
debugger-downloading = Downloading netcoredbg { $version }
# $platform names the operating system and processor, such as linux-x64.
debugger-unsupported = netcoredbg, the .NET debugger, has no build for { $platform }. Debugging .NET works on Linux x64 and Arm64, Windows x64 and macOS on Apple silicon.
# $reason is the error's own message.
debugger-download-failed = Runesmith could not download netcoredbg { $version }, the .NET debugger, from GitHub: { $reason }

## Why a rename or a quick fix is refused.

# $name is the name of a type or member.
rename-in-library = { $name } is defined in a library, so it cannot be renamed.
# $name is what the user typed as the new name.
rename-invalid-name = "{ $name }" is not a valid C# name.
edit-changes-files = This change adds or removes files, which Runesmith cannot apply yet.
