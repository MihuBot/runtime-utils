using System.IO.Compression;

namespace Runner.Jobs;

internal sealed class JitDiffJob : JobBase
{
    public const string DiffsDirectory = "jit-diffs/frameworks";
    public const string DiffsMainDirectory = $"{DiffsDirectory}/main";
    public const string DiffsPrDirectory = $"{DiffsDirectory}/pr";
    public const string DasmSubdirectory = "dasmset_1/base";
    public const string ExtraProjectsDirectory = "extra-projects";

    private const string CombinedDasmMainDirectory = "diffs-main-combined";
    private const string CombinedDasmPrDirectory = "diffs-pr-combined";

    private string _lastBuiltMainCommit = "none";

    public JitDiffJob(HttpClient client, string runnerId, string runnerToken) : base(client, runnerId, runnerToken) { }
    public JitDiffJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    public override async Task RunPreparedRunnerAsync()
    {
        Metadata[nameof(BaseRepo)] = Metadata[nameof(PrRepo)] = "dotnet/runtime";
        Metadata[nameof(BaseBranch)] = Metadata[nameof(PrBranch)] = "main";

        await RunProcessAsync("apt-get", "update");

        int failedBuilds = 0;

        while (true)
        {
            await WaitForPendingTasksAsync();

            await CloneRuntimeAndSetupToolsAsync(this);

            if (await GitHelper.GetCurrentCommitAsync(this, "runtime") != _lastBuiltMainCommit)
            {
                DeleteBuildArtifactsForMain();

                try
                {
                    await BuildAndCopyRuntimeBranchBitsAsync(this, "main");
                    failedBuilds = 0;
                }
                catch when (failedBuilds <= 10)
                {
                    failedBuilds++;

                    await LogAsync("Build failed. Retrying ...");
                    await Task.Delay(TimeSpan.FromMinutes(1) * Math.Pow(2, failedBuilds));

                    if (await RunProcessAsync("git", "clean -fdx", workDir: "runtime", checkExitCode: false) != 0)
                    {
                        await Task.Delay(10_000);
                        await RunProcessAsync("git", "clean -fdx", workDir: "runtime", checkExitCode: false);
                    }

                    continue;
                }

                await JitDiffUtils.RunJitDiffOnFrameworksAsync(this, "artifacts-main", "clr-checked-main", DiffsMainDirectory);

                await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);

                _lastBuiltMainCommit = await GitHelper.GetCurrentCommitAsync(this, "runtime");
            }

            await WaitForPendingTasksAsync();

            Stopwatch timeSinceLastBuild = Stopwatch.StartNew();

            while (timeSinceLastBuild.Elapsed.TotalMinutes < 30)
            {
                if (await TryAnnounceAndSwitchToLiveJobAsync())
                {
                    return;
                }
            }
        }
    }

    private static void DeleteBuildArtifactsForMain()
    {
        DeleteAndCreateEmptyDir("artifacts-main");
        DeleteAndCreateEmptyDir("clr-checked-main");
        DeleteAndCreateEmptyDir(DiffsMainDirectory);

        static void DeleteAndCreateEmptyDir(string path)
        {
            Directory.Delete(path, recursive: true);
            Directory.CreateDirectory(path);
        }
    }

    protected override async Task RunJobCoreAsync()
    {
        if (!UsingPreparedRunner)
        {
            await ChangeWorkingDirectoryToRamOrFastestDiskAsync();
        }

        await CloneRuntimeAndSetupToolsAsync(this);

        bool uploadCoreRoot = TryGetFlag("UploadCoreRoot");

        bool mainAlreadyBuilt = await GitHelper.GetCurrentCommitAsync(this, "runtime") == _lastBuiltMainCommit;

        if (!mainAlreadyBuilt)
        {
            DeleteBuildArtifactsForMain();

            await BuildAndCopyRuntimeBranchBitsAsync(this, "main", uploadCoreRoot);
        }

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync(this, "pr", uploadCoreRoot);

        Task downloadExtraAssemblies = DownloadExtraTestAssembliesAsync();

        if (!UsingPreparedRunner)
        {
            await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);
        }

        await downloadExtraAssemblies;

        string diffAnalyzeSummary = await CollectFrameworksDiffsAsync(skipMain: mainAlreadyBuilt);

        await UploadJitDiffExamplesAsync(diffAnalyzeSummary, regressions: true);
        await UploadJitDiffExamplesAsync(diffAnalyzeSummary, regressions: false);
    }

    public static async Task CloneRuntimeAndSetupToolsAsync(JobBase job)
    {
        Task cloneRuntimeTask = RuntimeHelpers.CloneRuntimeAsync(job);

        Task setupZipAndWgetTask = job.RunProcessAsync("apt-get", "install -y zip wget p7zip-full ninja-build", logPrefix: "Setup zip & wget");

        Task setupJitutilsTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup jitutils";
            await setupZipAndWgetTask;

            if (Directory.Exists("jitutils"))
            {
                return;
            }

            string repo = job.GetArgument("jitutils-repo", "dotnet/jitutils");
            string branch = job.GetArgument("jitutils-branch", "main");

            await job.RunProcessAsync("git", $"clone --no-tags --single-branch -b {branch} --progress https://github.com/{repo}.git jitutils", logPrefix: LogPrefix);

            if (IsArm)
            {
                const string ToolsLink = "https://raw.githubusercontent.com/MihaZupan/runtime-utils/clang-tools";
                Directory.CreateDirectory("jitutils/bin");
                await job.RunProcessAsync("wget", $"-O jitutils/bin/clang-format {ToolsLink}/clang-format", logPrefix: LogPrefix);
                await job.RunProcessAsync("wget", $"-O jitutils/bin/clang-tidy {ToolsLink}/clang-tidy", logPrefix: LogPrefix);
                await job.RunProcessAsync("chmod", "751 jitutils/bin/clang-format", logPrefix: LogPrefix);
                await job.RunProcessAsync("chmod", "751 jitutils/bin/clang-tidy", logPrefix: LogPrefix);
            }

            await job.RunProcessAsync("bash", "bootstrap.sh", logPrefix: LogPrefix, workDir: "jitutils");
        });

        Task createDirectoriesTask = Task.Run(() =>
        {
            Directory.CreateDirectory("artifacts-main");
            Directory.CreateDirectory("artifacts-pr");
            Directory.CreateDirectory("clr-checked-main");
            Directory.CreateDirectory("clr-checked-pr");
            Directory.CreateDirectory("jit-diffs");
            Directory.CreateDirectory(DiffsDirectory);
            Directory.CreateDirectory(DiffsMainDirectory);
            Directory.CreateDirectory(DiffsPrDirectory);
            Directory.CreateDirectory(ExtraProjectsDirectory);
        });

        await createDirectoriesTask;
        await setupJitutilsTask;
        await setupZipAndWgetTask;
        await cloneRuntimeTask;
    }

    public static async Task BuildAndCopyRuntimeBranchBitsAsync(JobBase job, string branch, bool uploadArtifacts = true, bool buildChecked = true, bool canSkipRebuild = true)
    {
        canSkipRebuild &= !ForceRebuildAll();
        canSkipRebuild &= branch == "pr";

        (bool rebuildClr, bool rebuildLibs) = await ShouldRebuildAsync();

        string targets = (rebuildClr, rebuildLibs) switch
        {
            (true, true) => "clr+libs",
            (true, false) => "clr",
            _ => "libs"
        };

        if (branch == "pr" && ForceRebuildAll())
        {
            await job.RunProcessAsync("bash", "build.sh -clean", logPrefix: $"{branch} clean", workDir: "runtime");
        }

        bool runPatches = !job.TryGetFlag("skipRuntimePatches");

        if (runPatches)
        {
            await RuntimePatches.ApplyPatchesAsync(job);
        }

        try
        {
            await job.RunProcessAsync("bash", $"build.sh {targets} -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}", logPrefix: $"{branch} release", workDir: "runtime");

            Task copyReleaseBitsTask = RuntimeHelpers.CopyReleaseArtifactsAsync(job, $"{branch} release", $"artifacts-{branch}");

            if (buildChecked)
            {
                if (rebuildClr)
                {
                    await job.RunProcessAsync("bash", "build.sh clr.jit -c Checked", logPrefix: $"{branch} checked", workDir: "runtime");
                }

                await job.RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{Arch}.Checked/. clr-checked-{branch}", logPrefix: $"{branch} checked");
            }

            if (uploadArtifacts)
            {
                job.PendingTasks.Enqueue(job.SevenZipAndUploadArtifactAsync($"build-artifacts-{branch}", $"artifacts-{branch}"));

                if (buildChecked)
                {
                    job.PendingTasks.Enqueue(job.SevenZipAndUploadArtifactAsync($"build-clr-checked-{branch}", $"clr-checked-{branch}"));
                }
            }

            await copyReleaseBitsTask;
        }
        finally
        {
            if (runPatches)
            {
                await RuntimePatches.RevertPatchesAsync(job);
            }
        }

        bool ForceRebuildAll() => job.TryGetFlag("forceRebuildAll");

        async Task<(bool Clr, bool Libs)> ShouldRebuildAsync()
        {
            if (canSkipRebuild)
            {
                bool clr = false;
                bool libs = false;

                foreach (string file in await GitHelper.GetChangedFilesAsync(job, "main", "runtime"))
                {
                    if (file.Contains("/System.Private.CoreLib/", StringComparison.OrdinalIgnoreCase))
                    {
                        clr = true;
                        libs = true;
                    }
                    else if (file.StartsWith("src/coreclr/", StringComparison.OrdinalIgnoreCase))
                    {
                        clr = true;
                    }
                    else if (file.Contains("Common", StringComparison.OrdinalIgnoreCase))
                    {
                        clr = true;
                        libs = true;
                    }
                    else if (file.StartsWith("src/libraries/", StringComparison.OrdinalIgnoreCase))
                    {
                        libs = true;
                    }
                    else if (
                        file.StartsWith("src/tests/", StringComparison.OrdinalIgnoreCase) ||
                        file.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                        file is "LICENSE.TXT" or "PATENTS.TXT" or "THIRD-PARTY-NOTICES.TXT")
                    {
                        // Not interesting
                    }
                    else
                    {
                        clr = true;
                        libs = true;
                    }
                }

                if (!clr && !libs)
                {
                    await job.LogAsync($"WARNING: Don't need to rebuild anything? What is this PR?");
                }

                return (clr, libs);
            }

            return (true, true);
        }
    }

    private async Task DownloadExtraTestAssembliesAsync()
    {
        try
        {
            string? url;

            if (TryGetFlag("nuget") && Metadata.TryGetValue("JitDiffExtraAssembliesUrl", out url))
            {
                // Use the full archive when explicitly requested
            }
            else if (!TryGetFlag("nonuget") && !IsArm && Metadata.TryGetValue("JitDiffExtraAssembliesSubsetUrl", out url))
            {
                // Fall back to the smaller subset archive on x64
            }
            else
            {
                return;
            }

            await LogAsync("[Extra assemblies] Downloading archive ...");

            using var tempZip = new TempFile("zip");

            using (var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, JobTimeout))
            {
                response.EnsureSuccessStatusCode();

                using var fs = File.Create(tempZip.Path);
                await response.Content.CopyToAsync(fs, JobTimeout);
            }

            ZipFile.ExtractToDirectory(tempZip.Path, ExtraProjectsDirectory);

            int count = Directory.GetDirectories(ExtraProjectsDirectory).Length;
            await LogAsync($"[Extra assemblies] Extracted {count} packages");

            string packageListPath = Path.Combine(ExtraProjectsDirectory, "PackageList.json");
            if (File.Exists(packageListPath))
            {
                PendingTasks.Enqueue(UploadArtifactAsync(packageListPath));
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to download extra test assemblies: {ex}");
        }
    }

    private async Task<string> CollectFrameworksDiffsAsync(bool skipMain)
    {
        List<ExtraAssemblyDiffFailure> extraAssemblyFailures = new();

        try
        {
            await Task.WhenAll(
                skipMain ? Task.CompletedTask : JitDiffUtils.RunJitDiffOnFrameworksAsync(this, "artifacts-main", "clr-checked-main", DiffsMainDirectory),
                JitDiffUtils.RunJitDiffOnFrameworksAsync(this, "artifacts-pr", "clr-checked-pr", DiffsPrDirectory));

            await Task.WhenAll(
                RuntimeHelpers.CopyAspNetSharedFrameworkToCoreRootAsync(this, "artifacts-main"),
                RuntimeHelpers.CopyAspNetSharedFrameworkToCoreRootAsync(this, "artifacts-pr"));

            int memoryAvailableGB = GetRemainingSystemMemoryGB();

            List<ExtraAssemblyDiffFailure>[] failuresByBranch = await Task.WhenAll(
                DiffExtraProjectsAsync("artifacts-main", "clr-checked-main", DiffsMainDirectory, memoryAvailableGB),
                DiffExtraProjectsAsync("artifacts-pr", "clr-checked-pr", DiffsPrDirectory, memoryAvailableGB));

            extraAssemblyFailures.AddRange(failuresByBranch.SelectMany(branchFailures => branchFailures));
        }
        finally
        {
            int parallelism = Math.Max(1, Environment.ProcessorCount / 2 - 1);
            PendingTasks.Enqueue(SevenZipAndUploadArtifactAsync("jit-diffs-main", DiffsMainDirectory, maxCompression: true, parallelism: parallelism));
            PendingTasks.Enqueue(SevenZipAndUploadArtifactAsync("jit-diffs-pr", DiffsPrDirectory, maxCompression: true, parallelism: parallelism));
        }

        CombineAllDiffs(DiffsMainDirectory, CombinedDasmMainDirectory);
        CombineAllDiffs(DiffsPrDirectory, CombinedDasmPrDirectory);

        // Drop the dasm of any assembly that failed to diff on either branch. Otherwise its one-sided
        // (missing / partial) dasm shows up in jit-analyze as a flood of spurious +100% / -100% diffs.
        RemoveFailedAssemblyDasm(extraAssemblyFailures, CombinedDasmMainDirectory, CombinedDasmPrDirectory);

        string diffAnalyzeSummary = await JitDiffUtils.RunJitAnalyzeAsync(this, CombinedDasmMainDirectory, CombinedDasmPrDirectory);

        // Surface any extra-assembly diff failures to the user (rendered prominently by the host) rather
        // than burying them inside the collapsed diff summary code block.
        await ReportExtraAssemblyFailuresAsync(extraAssemblyFailures);

        PendingTasks.Enqueue(UploadTextArtifactAsync("diff-frameworks.txt", diffAnalyzeSummary));

        return diffAnalyzeSummary;

        async Task<List<ExtraAssemblyDiffFailure>> DiffExtraProjectsAsync(string coreRootFolder, string checkedClrFolder, string outputFolder, int memoryAvailableGB)
        {
            var failures = new List<ExtraAssemblyDiffFailure>();

            string projectsRoot = ExtraProjectsDirectory;
            string branch = coreRootFolder.Contains("main", StringComparison.Ordinal) ? "main" : "pr";

            // Handle archives with an extra wrapper subdirectory
            string[] topDirs = Directory.GetDirectories(projectsRoot);
            if (topDirs.Length == 1 && Directory.GetFiles(projectsRoot).Length == 0)
            {
                projectsRoot = topDirs[0];
            }

            // Skip projects whose main DLL shares a name with a system library in core_root
            var systemDlls = new HashSet<string>(
                Directory.GetFiles(coreRootFolder, "*.dll").Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase);

            var projectDirs = new Queue<string>(Directory.GetDirectories(projectsRoot)
                .Where(d =>
                {
                    string diffAssembliesPath = Path.Combine(d, "DiffAssemblies.txt");
                    string mainDll = File.Exists(diffAssembliesPath)
                        ? File.ReadLines(diffAssembliesPath).FirstOrDefault(l => l.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ?? $"{Path.GetFileName(d)}.dll"
                        : $"{Path.GetFileName(d)}.dll";
                    return !systemDlls.Contains(mainDll);
                })
                .OrderByDescending(d => Directory.GetFiles(d, "*.dll").Sum(f => new FileInfo(f).Length)));
            if (projectDirs.Count == 0)
            {
                return failures;
            }

            int coreRootCopies = Math.Min(Math.Min(Environment.ProcessorCount, memoryAvailableGB * 2), projectDirs.Count);
            coreRootCopies = Math.Max(coreRootCopies / 2 + 1, 1); // / 2 because we run for main and pr in parallel
            await LogAsync($"Running JIT diffs with parallelism {coreRootCopies}");

            var countdown = new CountdownEvent(coreRootCopies);

            await Parallel.ForAsync(0, coreRootCopies, async (index, _) =>
            {
                string newCoreRootFolder = coreRootFolder;

                if (index > 0)
                {
                    // jit-diff only reads the base JIT from checkedClrFolder, so every worker shares one
                    // copy. Only the core_root is mutated (jit-diff installs the JIT into it in place), so
                    // give each worker a cheap linked clone with a private copy of just the JIT.
                    newCoreRootFolder = $"{newCoreRootFolder}_{index}";
                    JitDiffUtils.CreateCoreRootCloneForJitDiff(coreRootFolder, newCoreRootFolder);
                }

                countdown.Signal();
                countdown.Wait(JobTimeout);

                while (true)
                {
                    string? projectDir;
                    lock (projectDirs)
                    {
                        if (!projectDirs.TryDequeue(out projectDir))
                        {
                            break;
                        }
                    }

                    List<string> testedAssemblies = new();

                    string diffAssembliesListPath = Path.Combine(projectDir, "DiffAssemblies.txt");

                    if (File.Exists(diffAssembliesListPath))
                    {
                        foreach (string line in File.ReadAllLines(diffAssembliesListPath))
                        {
                            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line) || !line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            testedAssemblies.Add(line);
                        }
                    }
                    else
                    {
                        testedAssemblies.Add($"{Path.GetFileName(projectDir)}.dll");
                    }

                    string[] assemblyPaths = testedAssemblies.Select(a => Path.GetFullPath(Path.Combine(projectDir, a))).ToArray();

                    string projectName = Path.GetFileName(projectDir);
                    List<string> jitDiffOutput = new();

                    try
                    {
                        await JitDiffUtils.RunJitDiffOnAssembliesAsync(this, newCoreRootFolder, checkedClrFolder, outputFolder, assemblyPaths, logPrefix: $"{branch} {projectName}", output: jitDiffOutput);
                    }
                    catch (Exception ex)
                    {
                        await LogAsync($"Failed to diff {projectName} on {branch}: {ex.Message}");

                        // Prefer the specific assemblies jit-diff named as failing; fall back to all of the
                        // project's assemblies if it failed without naming one (e.g. a crash before dasm).
                        string[] failedAssemblies = JitDiffUtils.ParseFailedAssemblyNames(jitDiffOutput)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();

                        if (failedAssemblies.Length == 0)
                        {
                            failedAssemblies = testedAssemblies.Select(Path.GetFileName).ToArray()!;
                        }

                        string[] errorLines = ExtractJitDiffErrorLines(jitDiffOutput);
                        if (errorLines.Length == 0)
                        {
                            errorLines = [ex.Message];
                        }

                        lock (failures)
                        {
                            failures.Add(new ExtraAssemblyDiffFailure(branch, projectName, failedAssemblies, errorLines));
                        }
                    }
                }

                if (index > 0)
                {
                    try { Directory.Delete(newCoreRootFolder, recursive: true); } catch { }
                }
            });

            return failures;
        }

        void CombineAllDiffs(string directory, string destination)
        {
            Directory.CreateDirectory(destination);

            Parallel.ForEach(Directory.EnumerateFiles(directory, "*.dasm", SearchOption.AllDirectories).ToArray(), file =>
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            });
        }
    }

    private sealed record ExtraAssemblyDiffFailure(string Branch, string Project, string[] Assemblies, string[] ErrorLines);

    private static readonly string[] s_jitDiffErrorMarkers =
    [
        "Error running",
        "errors compiling set",
        "returned with",
        "Dasm commands returned",
        "Dasm task failed",
        "Failures detected generating asm",
        "Assert failure",
        "Unhandled exception",
    ];

    private static string[] ExtractJitDiffErrorLines(List<string> jitDiffOutput)
    {
        return jitDiffOutput
            .Where(line => s_jitDiffErrorMarkers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToArray();
    }

    private static void RemoveFailedAssemblyDasm(List<ExtraAssemblyDiffFailure> failures, params string[] combinedDasmDirectories)
    {
        foreach (string dasmFile in failures
            .SelectMany(failure => failure.Assemblies)
            .Select(assembly => $"{Path.GetFileNameWithoutExtension(assembly)}.dasm")
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string directory in combinedDasmDirectories)
            {
                try { File.Delete(Path.Combine(directory, dasmFile)); }
                catch { }
            }
        }
    }

    private async Task ReportExtraAssemblyFailuresAsync(List<ExtraAssemblyDiffFailure> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        bool failedOnMain = failures.Any(failure => failure.Branch == "main");
        bool failedOnPr = failures.Any(failure => failure.Branch == "pr");

        // Only the PR branch failing (nothing on main) points at the PR itself; failures that also happen
        // on main are more likely environmental/pre-existing, so they're reported but not flagged on the PR.
        bool likelyCausedByPr = failedOnPr && !failedOnMain;

        var failedAssemblies = failures
            .SelectMany(failure => failure.Assemblies.Select(assembly => (failure.Branch, Assembly: assembly)))
            .GroupBy(entry => entry.Assembly, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Plain Markdown - the host owns presentation (alert block, comment, ...). The failed-assembly list
        // is capped so it stays readable; this concise summary is all that's posted as a comment.
        const int MaxListedAssemblies = 10;

        StringBuilder summary = new();

        summary.AppendLine($"**{failedAssemblies.Length} extra test {(failedAssemblies.Length == 1 ? "assembly" : "assemblies")} failed to produce JIT diffs** and {(failedAssemblies.Length == 1 ? "was" : "were")} excluded from the diff analysis:");

        foreach (var group in failedAssemblies.Take(MaxListedAssemblies))
        {
            string branches = string.Join(", ", group.Select(entry => entry.Branch).Distinct().OrderBy(branch => branch, StringComparer.Ordinal));
            summary.AppendLine($"- `{group.Key}` ({branches})");
        }

        if (failedAssemblies.Length > MaxListedAssemblies)
        {
            summary.AppendLine($"- ... and {failedAssemblies.Length - MaxListedAssemblies} more");
        }

        if (likelyCausedByPr)
        {
            summary.AppendLine();
            summary.AppendLine("All failures happened only on the PR branch, which likely indicates a bug introduced by the PR (e.g. a JIT assert or crash while compiling these assemblies).");
        }

        string summaryMarkdown = summary.ToString().TrimEnd();

        // The full per-assembly output can be large, so it's only shown in the (collapsible) tracking issue
        // details - never in a posted comment.
        StringBuilder details = new();

        details.AppendLine("<details>");
        details.AppendLine("<summary>Relevant output</summary>");
        details.AppendLine();
        details.AppendLine("```");

        const int MaxOutputLines = 60;
        int outputLines = 0;
        bool truncated = false;

        foreach (ExtraAssemblyDiffFailure failure in failures
            .OrderBy(failure => failure.Branch, StringComparer.Ordinal)
            .ThenBy(failure => failure.Project, StringComparer.OrdinalIgnoreCase))
        {
            foreach (string line in failure.ErrorLines)
            {
                if (outputLines++ == MaxOutputLines)
                {
                    truncated = true;
                    break;
                }

                details.AppendLine($"[{failure.Branch} {failure.Project}] {line}");
            }

            if (truncated)
            {
                break;
            }
        }

        if (truncated)
        {
            details.AppendLine("... (truncated, see the full logs for details)");
        }

        details.AppendLine("```");
        details.AppendLine();
        details.AppendLine("</details>");

        string detailsMarkdown = details.ToString().TrimEnd();

        await LogAsync($"{summaryMarkdown}\n\n{detailsMarkdown}");

        // Post a comment (summary only, no verbose output) when the PR is the likely cause; the full report
        // always goes to the tracking issue.
        await ReportUserVisibleErrorAsync(summaryMarkdown, details: detailsMarkdown, postComment: likelyCausedByPr);
    }

    private async Task UploadJitDiffExamplesAsync(string diffAnalyzeSummary, bool regressions)
    {
        var (diffs, noisyDiffsRemoved) = await JitDiffUtils.GetDiffMarkdownAsync(
            this,
            JitDiffUtils.ParseDiffAnalyzeEntries(diffAnalyzeSummary, regressions),
            CombinedDasmMainDirectory,
            CombinedDasmPrDirectory,
            tryGetExtraInfo: null,
            replaceMethodName: name => name,
            maxCount: 20);

        string changes = JitDiffUtils.GetCommentMarkdown(diffs, GitHubHelpers.CommentLengthLimit, regressions, out bool truncated);

        await LogAsync($"Found {diffs.Length} changes, comment length={changes.Length} for {nameof(regressions)}={regressions}");

        if (changes.Length != 0)
        {
            if (noisyDiffsRemoved)
            {
                changes = $"{changes}\n\nNote: some changes were skipped as they were likely noise.";
            }

            PendingTasks.Enqueue(UploadTextArtifactAsync($"ShortDiffs{(regressions ? "Regressions" : "Improvements")}.md", changes));

            if (truncated)
            {
                changes = JitDiffUtils.GetCommentMarkdown(diffs, GitHubHelpers.GistLengthLimit, regressions, out _);

                PendingTasks.Enqueue(UploadTextArtifactAsync($"LongDiffs{(regressions ? "Regressions" : "Improvements")}.md", changes));
            }
        }
    }
}
