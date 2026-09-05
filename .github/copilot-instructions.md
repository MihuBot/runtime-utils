# Repository instructions

## Build and execution

The single executable project is `Runner\Runner.csproj` (`Runner\Runner.slnx`). It targets `net11.0` and requires a current **daily .NET 11 SDK**: `CoreRootArchive` uses in-box ZStandard APIs that are newer than the SDK in the tagged `11.0-preview` Docker image. The Dockerfile installs the daily SDK on top of that image; GitHub Actions also requests `dotnet-quality: daily`.

From the repository root:

```powershell
dotnet build .\Runner\Runner.csproj -c Release
dotnet publish .\Runner\Runner.csproj -c Release -o .\Runner\bin\publish
docker build -t runtime-utils-runner .\Runner
```

Run actual jobs only in a disposable runner environment. Jobs install system dependencies, modify global Git configuration, clean cloned trees, and can terminate other .NET processes or push GitHub branches. The working directory is scratch space, not the application source directory. For an authorized job, use a fresh directory outside this checkout and an absolute project path:

```powershell
# From a fresh scratch directory; credentials and JOB_ID supplied by the environment.
dotnet run -c Release --project C:\path\to\runtime-utils\Runner\Runner.csproj
```

`Program.cs` accepts `JOB_ID`, or a single GitHub issue-event JSON path containing the `RUN_AS_GITHUB_ACTION_` marker. Metadata retrieval uses `RUNTIME_UTILS_TOKEN`. Prepared runners instead require `JOB_TYPE`, `RUNNER_ID`, and `RUNNER_TOKEN`; only `JitDiffJob` and `RegexDiffJob` support this mode.

Every process creates `.runner-in-use` in its initial working directory and refuses a directory marked by an earlier process. Do not bypass this guard without cleaning the scratch state. Docker keeps the application in `/app` and scratch data in `/runner`; `entrypoint.sh` deletes everything inside `/runner` on startup, so never mount source code or valuable data there.

### Targeted upstream tests

`RegexDiffJob` runs an individual test in its cloned `dotnet/runtime` tree using the runtime repository's MSBuild test target. After the job has built the runtime and injected its test, the equivalent command, from the scratch `runtime` directory, is:

```powershell
bash dotnet.sh build src/libraries/System.Text.RegularExpressions/tests/FunctionalTests /t:Test -c Release /p:XUnitMethodName=System.Text.RegularExpressions.Tests.InjectedGenerateAllSourcesTestClass.GenerateAllSourcesAsync
```

For another existing test in that upstream project, replace `XUnitMethodName` with its fully qualified method name. This is an upstream runtime test, not a test of the Runner executable.

## Architecture

`Program.cs` is the composition root and dispatches explicit job-type switches. The runner is a client of MihuBot's `RuntimeUtils` HTTP API, not the server: job metadata, assignment, logs, completion, and artifacts flow through that API. Azure Blob Storage SAS URLs in metadata provide persistent state and CoreRoot storage.

`JobBase` owns the job lifecycle: metadata initialization, deadline cancellation, streamed logs and hardware telemetry, subprocess execution, pending work, artifact uploads, and completion notification. Implement work in `RunJobCoreAsync`; let `RunJobAsync` manage lifecycle and failures. Prepared jobs build and cache baseline artifacts before advertising availability, then switch the same instance to live metadata and run one assigned job.

The comparison pipeline clones `dotnet/runtime`, builds baseline and PR artifacts, and invokes external tooling. `RuntimeHelpers` owns cloning, branch/patch setup, and artifact copying; `JitDiffJob` exposes build/setup helpers also used by regex comparisons, library benchmarks, and NuGet assembly generation. `JitDiffUtils` handles JIT tooling and disassembly processing. Changes to these shared build helpers affect more than JIT diff jobs.

`BenchmarkLibrariesJob` uses `dotnet/performance` with either freshly built CoreRoots or archived commit-range CoreRoots. `CoreRootGenerationJob` produces those archives, `CoreRootAPI` defines their service metadata, and `CoreRootArchive` implements the shared format. `NuGetExtraAssembliesJob` and `NuGetClient` gather license-approved packages and dependencies for extra JIT inputs. Fuzzing uses the runtime's DotnetFuzzing deployment; rebase/backport jobs perform Git operations and can publish changes.

GitHub issue-triggered workflows run Windows x64/ARM64 jobs; Azure Pipelines accepts MihuBot webhook jobs; Docker Compose starts prepared runners. Platform support is job-specific: the JIT/benchmark tooling assumes Linux, while fuzzing explicitly requires Windows and rebase/backport scripts use Windows batch files.

## Companion server: MihuBot

The server is in the sibling checkout `..\MihuBot`. Read its `.github\copilot-instructions.md` before changing server code. For changes to job types, arguments, metadata, logs, artifacts, startup, or API behavior, inspect both repositories rather than treating the runner as self-contained. Paths below are relative to that sibling checkout.

### Ownership and job lifecycle

- `MihuBot\RuntimeUtils\Jobs\XJob.cs` pairs with `Runner\Jobs\XJob.cs` by class name. The server class chooses resources, adds metadata in `InitializeAsync`, waits for completion, and turns runner output into GitHub reports. The runner class performs the actual work. Adding/renaming a job requires coordinated changes to server construction/dispatch and the runner's `Program.cs`.
- `MihuBot\RuntimeUtils\RuntimeUtilsService.cs` accepts GitHub mentions and web/REST/MCP submissions, validates permissions/arguments and capacity, constructs jobs, tracks them by private and public IDs, and matches prepared runners. It also schedules CoreRoot and NuGet corpus generation. Public patch/commit submissions currently support JitDiff, BenchmarkLibraries, and RegexDiff; new runner options may also need changes to server-side submission validation and usage text.
- `MihuBot\RuntimeUtils\JobBase.cs` owns metadata, tracking issues, deadlines/idle timeouts, artifact storage, completion records, and provisioning. `RunOnNewVirtualMachineAsync` contains Azure/Hetzner/Helix selection and startup scripts; `HelixAvailabilityService.cs` selects queues. Machine flags and provisioning policy belong here, not just in the runner. The server waits on `JobCompletionTcs`, which the runner's `Jobs/Complete` request signals.
- `MihuBot\API\RuntimeUtilsController.cs` implements `Jobs/Metadata`, `Patch`, `Logs`, `SystemInfo`, `Artifact`, `Complete`, and `AnnounceRunner`, plus public submission/status/progress APIs. `MihuBot\API\CoreRootController.cs` and `MihuBot\RuntimeUtils\CoreRootService.cs` own CoreRoot validation, persistence, and download URLs; keep them aligned with `CoreRootAPI` and `CoreRootArchive`.

### Wire contracts

`JobId` is the private runner identifier; `ExternalId` is the public dashboard/tracking identifier. GitHub Actions and Azure Pipelines bootstrap with the public ID, which requires `X-Runtime-Utils-Token` to retrieve metadata; metadata then supplies the private `JobId` for runner operations. Do not substitute the public ID into private runner endpoints or expose the private ID in reports.

Prepared-runner matching compares all five capabilities, case-insensitively: job type, OS, architecture, base repository, and base branch (`RunnerCapabilities.cs`). Announcements authenticate with `X-Runner-Announce-Token`, checked against the server's dynamic `RuntimeUtils.RunnerAnnounceToken.<runnerId>` setting. Assignment also has permission and flag restrictions in server `TrySignalAvailableRunnerAsync`; capability equality alone does not guarantee reuse.

Logs are part of the protocol: receiving them resets the server idle timeout, and an exact leading `ERROR: ` marks a fatal runner error. Preserve heartbeats and the unprefixed fatal-error format. Completed-job responses can carry `X-Job-Completed`, which runner `SendAsyncCore` uses to cancel work.

Artifact names are consumed by each server job's `InterceptArtifactAsync`, not merely displayed as downloads. Examples:

| Job | Server-consumed output or metadata |
| --- | --- |
| JitDiff | `JitDiffExamples.json` drives the hosted diff browser; `diff-frameworks.txt` contains the analyzer output. Metadata supplies full/subset extra-assembly URLs. |
| BenchmarkLibraries | Case-sensitive `results.md` becomes the benchmark report/comment. |
| RegexDiff | `RegexSourceDiffExamples.json` and optional `JitDiffExamples.json` drive the hosted diff browser; `JitAnalyzeSummary.txt` contains the analyzer output and `Results.zip` contains full source results. |
| FuzzLibraries | `-stack.txt`, `-inputs.zip`, and `-coverage.zip` suffixes identify crashes, persistent input corpora, and coverage. |
| NuGetExtraAssemblies | `nuget-extra-assemblies.zip` and `nuget-extra-assemblies-subset.zip` update blobs subsequently supplied to JitDiff jobs. |
| CoreRootGeneration | Server supplies `CoreRootSasUri`; save requests use Unix-second `commitTime` and require an existing standalone prefix entry, never a chain of deltas. |
| Rebase / Backport | Server supplies `MihuBotPushToken`; backports additionally use the `BackportJob_*` metadata family. These jobs run through GitHub Actions. |

The runner produces diff artifacts and MihuBot displays them in its web UI. Changes to diff reporting may require updating both repositories.

### Deployment coupling

VM and Helix startup scripts clone the unpinned default branch of `MihaZupan/runtime-utils` and build/run it from a separate `runner-work` directory. MihuBot and the runner, including prepared runners, are assumed to use matching, up-to-date versions.

Linux Helix uses **Helix-specific prerequisite images**, selected by server `GetHelixDockerImage`, not necessarily this repository's prepared-runner Docker image. Defaults are Ubuntu 24.04 `dotnet-buildtools/prereqs` images with `-helix-amd64` / `-helix-arm64v8` tags; dynamic configuration and an admin-only `-docker <image>` override can change them. The `helixbot` user and Helix scripts are required. Consult the implementation if the companion instructions describe a different default.

Tracking issues and issue-triggered Actions use `MihuBot/runtime-utils`, whereas VM/Helix source clones use `MihaZupan/runtime-utils`. Docker publication in this repository is gated to `MihuBot/runtime-utils`, `main`, and a commit message containing `docker`. Do not confuse source deployment, Actions checkout, and Docker publication.

When changing the server, build from `..\MihuBot` with `dotnet build MihuBot.slnx`; its build enforces code style and treats warnings as errors. Follow its own instructions for targeted tests and configuration.

## Cross-cutting conventions

- **Job registration and metadata are a protocol.** Add new job types to the appropriate switches in `Program.cs`. Metadata fetched from the host is a case-insensitive string dictionary; shared keys commonly use `nameof` properties on `JobBase`. Keep key names, job class names, and artifact names compatible with the external host. Job options come from metadata's `CustomArguments`, parsed with `TryGetFlag`, `TryGetArgument`, and `GetArgument`, not from a general CLI parser.
- **Baseline refs and artifact labels differ.** `BaselineRef` is `BaseBranch`, or `baseline` when `BaseCommit` pins a commit. The PR tree is on `pr`; output labels remain `main`/`pr` (`artifacts-main`, `artifacts-pr`, `clr-checked-main`, `clr-checked-pr`). Do not assume an artifact labeled `main` means the Git branch is literally `main`. Preserve the `dependsOn`, `combineWith`, and `HasPatch` setup in `RuntimeHelpers`.
- **Use the lifecycle helpers.** Route subprocesses through `RunProcessAsync` for linked job cancellation, output collection, logging, and exit-code handling. Nonzero exits throw by default; callers using `checkExitCode: false` must interpret the result. Use `JobTimeout` for job-bound work and enqueue overlapping uploads/work in `PendingTasks` so they finish before completion or reuse of their artifacts.
- **Keep credentials out of process logs.** `RunProcessAsync` redacts the job ID, but not arbitrary tokens. It logs arguments and explicitly supplied environment variables; token-bearing operations must supply `processLogs` redaction, as the rebase/backport jobs do.
- **Preserve partial-failure reporting.** Use `ReportUserVisibleErrorAsync` when a job can finish but a failure must reach the user. It uploads the host-recognized `UserVisibleError.json` contract. Pass plain Markdown; the host owns presentation, tracking-issue-only details, and optional comments.
- **SDK selection applies to child apphosts too.** When launching with an installed SDK, use `DotnetHelpers.GetSdkEnvVars` to align `DOTNET_ROOT`, its architecture-specific variant, and `PATH`. Invoking an absolute `dotnet` executable alone does not pin the runtime used by its child apphost.
- **Do not inherit the runner's MSBuild SDK paths.** `RunProcessAsync` removes inherited `MSBuildSDKsPath` and `MSBuildExtensionsPath` before applying explicit environment overrides. `dotnet run` can leak these variables, mixing the runner's SDK targets and task hosts with a cloned repository's pinned MSBuild.
- **Serialize package installation.** Use `AptHelper.RunAptGetAsync`, or `RunWithAptLockAsync` for scripts that invoke apt internally. Parallel setup tasks otherwise contend for apt's system locks.
- **Temporary runtime patches must not leak between builds.** `RuntimePatches` skips files changed by the PR and reverts its edits in the shared build helper's `finally` block. Preserve both safeguards when extending deterministic-build patches.
- **CoreRoot archive changes span producers and consumers.** Archives are `.tar.zst`, preserving Unix executable modes. A delta's prefix is the entire uncompressed standalone reference tarball, identified by `PrefixBlobName` and downloaded through `PrefixUrl`; decompression must use that same prefix. Keep generation, API metadata, and benchmark downloads aligned. Reference age/order uses `CommitTime`, not upload `CreatedOn`; compression runs between builds to avoid overlapping large memory allocations.
