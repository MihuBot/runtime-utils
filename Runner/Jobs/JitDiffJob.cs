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

    public JitDiffJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamOrFastestDiskAsync();

        await CloneRuntimeAndSetupToolsAsync(this);

        await BuildAndCopyRuntimeBranchBitsAsync(this, "main");

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync(this, "pr");

        Task downloadExtraAssemblies = DownloadExtraTestAssembliesAsync();

        await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);

        await downloadExtraAssemblies;

        string diffAnalyzeSummary = await CollectFrameworksDiffsAsync();

        await UploadJitDiffExamplesAsync(diffAnalyzeSummary, regressions: true);
        await UploadJitDiffExamplesAsync(diffAnalyzeSummary, regressions: false);
    }

    public static async Task CloneRuntimeAndSetupToolsAsync(JobBase job)
    {
        Task cloneRuntimeTask = RuntimeHelpers.CloneRuntimeAsync(job);

        Task setupZipAndWgetTask = job.RunProcessAsync("apt-get", "install -y zip wget", logPrefix: "Setup zip & wget");

        Task setupJitutilsTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup jitutils";
            await setupZipAndWgetTask;

            string repo = job.GetArgument("jitutils-repo", "dotnet/jitutils");
            string branch = job.GetArgument("jitutils-branch", "main");

            await job.RunProcessAsync("git", $"clone --no-tags --single-branch -b {branch} --progress https://github.com/{repo}.git", logPrefix: LogPrefix);

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
        (bool rebuildClr, bool rebuildLibs) = await ShouldRebuildAsync();

        string targets = (rebuildClr, rebuildLibs) switch
        {
            (true, true) => "clr+libs",
            (true, false) => "clr",
            _ => "libs"
        };

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

        async Task<(bool Clr, bool Libs)> ShouldRebuildAsync()
        {
            if (canSkipRebuild && branch == "pr" && !job.TryGetFlag("forceRebuildAll"))
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
                    else if (!file.StartsWith("src/tests/", StringComparison.OrdinalIgnoreCase))
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
            if (!Metadata.TryGetValue("JitDiffExtraAssembliesSasUri", out string? uri))
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

    private async Task<string> CollectFrameworksDiffsAsync()
    {
        try
        {
            await Task.WhenAll(
                JitDiffUtils.RunJitDiffOnFrameworksAsync(this, "artifacts-main", "clr-checked-main", DiffsMainDirectory),
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
            foreach (string projectDir in Directory.GetDirectories(ExtraProjectsDirectory))
            {
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
                    testedAssemblies.Add($"{Path.GetFileNameWithoutExtension(projectDir)}.dll");
                }

                string[] assemblyPaths = testedAssemblies.Select(a => Path.GetFullPath(Path.Combine(projectDir, a))).ToArray();

                await JitDiffUtils.RunJitDiffOnAssembliesAsync(this, coreRootFolder, checkedClrFolder, outputFolder, assemblyPaths);
            }
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
