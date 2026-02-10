using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

        bool mainAlreadyBuilt = await GitHelper.GetCurrentCommitAsync(this, "runtime") == _lastBuiltMainCommit;

        if (!mainAlreadyBuilt)
        {
            DeleteBuildArtifactsForMain();

            await BuildAndCopyRuntimeBranchBitsAsync(this, "main", uploadArtifacts: false);
        }

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync(this, "pr", uploadArtifacts: false);

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

        Task setupZipAndWgetTask = job.RunProcessAsync("apt-get", "install -y zip wget ninja-build", logPrefix: "Setup zip & wget");

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
            job.PendingTasks.Enqueue(job.ZipAndUploadArtifactAsync($"build-artifacts-{branch}", $"artifacts-{branch}"));

            if (buildChecked)
            {
                job.PendingTasks.Enqueue(job.ZipAndUploadArtifactAsync($"build-clr-checked-{branch}", $"clr-checked-{branch}"));
            }
        }

        await copyReleaseBitsTask;

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
            if (!Metadata.TryGetValue("JitDiffExtraAssembliesSasUri", out string? uri) ||
                !TryGetFlag("nuget"))
            {
                return;
            }

            var container = new BlobContainerClient(new Uri(uri));

            await foreach (BlobItem blob in container.GetBlobsAsync(cancellationToken: JobTimeout))
            {
                string fileName = Path.GetFileName(blob.Name);
                string projectName = Path.GetFileNameWithoutExtension(fileName);

                if (ShouldSkip(fileName))
                {
                    await LogAsync($"[Extra assemblies] Skipping {fileName}");
                    continue;
                }

                string projectDirectory = Path.Combine(ExtraProjectsDirectory, projectName);
                Directory.CreateDirectory(projectDirectory);

                var blobClient = container.GetBlobClient(blob.Name);

                if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    await blobClient.DownloadToAsync(Path.Combine(projectDirectory, fileName), JobTimeout);
                }
                else
                {
                    using var zipFile = new TempFile("zip");
                    await blobClient.DownloadToAsync(zipFile.Path, JobTimeout);

                    using var archive = ZipFile.OpenRead(zipFile.Path);
                    archive.ExtractToDirectory(projectDirectory);
                }

                await LogAsync($"[Extra assemblies] Downloaded {fileName}");
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to download extra test assemblies: {ex}");
        }

        static bool ShouldSkip(string fileName)
        {
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return IsArm;
            }

            return !fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<string> CollectFrameworksDiffsAsync(bool skipMain)
    {
        try
        {
            await Task.WhenAll(
                skipMain ? Task.CompletedTask : JitDiffUtils.RunJitDiffOnFrameworksAsync(this, "artifacts-main", "clr-checked-main", DiffsMainDirectory),
                JitDiffUtils.RunJitDiffOnFrameworksAsync(this, "artifacts-pr", "clr-checked-pr", DiffsPrDirectory));

            await Task.WhenAll(
                DiffExtraProjectsAsync("artifacts-main", "clr-checked-main", DiffsMainDirectory),
                DiffExtraProjectsAsync("artifacts-pr", "clr-checked-pr", DiffsPrDirectory));
        }
        finally
        {
            PendingTasks.Enqueue(ZipAndUploadArtifactAsync("jit-diffs-main", DiffsMainDirectory));
            PendingTasks.Enqueue(ZipAndUploadArtifactAsync("jit-diffs-pr", DiffsPrDirectory));
        }

        CombineAllDiffs(DiffsMainDirectory, CombinedDasmMainDirectory);
        CombineAllDiffs(DiffsPrDirectory, CombinedDasmPrDirectory);

        string diffAnalyzeSummary = await JitDiffUtils.RunJitAnalyzeAsync(this, CombinedDasmMainDirectory, CombinedDasmPrDirectory);

        PendingTasks.Enqueue(UploadTextArtifactAsync("diff-frameworks.txt", diffAnalyzeSummary));

        return diffAnalyzeSummary;

        async Task DiffExtraProjectsAsync(string coreRootFolder, string checkedClrFolder, string outputFolder)
        {
            var projectDirs = new Queue<string>(Directory.GetDirectories(ExtraProjectsDirectory));
            if (projectDirs.Count == 0)
            {
                return;
            }

            int coreRootCopies = Math.Min(Math.Min(Environment.ProcessorCount / 2, GetRemainingSystemMemoryGB() / 3), projectDirs.Count / 20);
            coreRootCopies = Math.Max(coreRootCopies, 1);

            await Parallel.ForAsync(0, coreRootCopies, async (index, _) =>
            {
                string newCoreRootFolder = $"{coreRootFolder}_{index}";
                string newCheckedClrFolder = $"{checkedClrFolder}_{index}";

                CopyDirectory(coreRootFolder, newCoreRootFolder);
                CopyDirectory(checkedClrFolder, newCheckedClrFolder);

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

                    try
                    {
                        await JitDiffUtils.RunJitDiffOnAssembliesAsync(this, newCoreRootFolder, newCheckedClrFolder, outputFolder, assemblyPaths);
                    }
                    catch (Exception ex)
                    {
                        await LogAsync($"Failed to diff {Path.GetFileName(projectDir)}: {ex.Message}");
                    }
                }
            });
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

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, true);
        }
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
