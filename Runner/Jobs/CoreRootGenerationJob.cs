namespace Runner.Jobs;

internal sealed class CoreRootGenerationJob : JobBase
{
    private readonly HashSet<(string? Sha, string? Type)> _toSkip = [];
    private int _builtThisSession;

    public CoreRootGenerationJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamOrFastestDiskAsync();

        await CloneRuntimeAndSetupToolsAsync();

        string type = "release";

        foreach (var entry in await CoreRootAPI.AllAsync(this, type))
        {
            _toSkip.Add((entry.Sha, entry.Type));
        }

        while (true)
        {
            int built = _builtThisSession;
            await BuildCoreRootsAsync(type);

            if (built == _builtThisSession)
            {
                break;
            }

            await WaitForPendingTasksAsync();
            await RunProcessAsync("git", "checkout main", workDir: "runtime");
            await RunProcessAsync("git", "pull origin", workDir: "runtime");
        }
    }

    private async Task CloneRuntimeAndSetupToolsAsync()
    {
        Task cloneRuntimeTask = RuntimeHelpers.CloneRuntimeMainAsync(this);

        Task aptGetTask = RunProcessAsync("apt-get", "install -y zip wget p7zip-full", logPrefix: "Install tools");

        await aptGetTask;
        await cloneRuntimeTask;
    }

    private async Task BuildCoreRootsAsync(string type)
    {
        int lastNDays = int.Parse(GetArgument(nameof(lastNDays), "60"));

        List<string> commits = await GitHelper.ListCommitsAsync(this, lastNDays, "runtime");
        commits.Reverse();

        await LogAsync($"Found {commits.Count} commits in the last {lastNDays} days");

        for (int i = 0; i < commits.Count; i++)
        {
            if (MaxRemainingTime.TotalHours < 1)
            {
                await LogAsync("Approaching job duration limit. Stopping ...");
                break;
            }

            if (PendingTasks.Count > 5)
            {
                await LogAsync("Waiting for pending tasks before continuing ...");
                await WaitForPendingTasksAsync(2);
            }

            string progressMessage = $"Processing commit {i + 1}/{commits.Count}. Built {_builtThisSession} in this session.";
            LastProgressSummary = progressMessage;
            await LogAsync(progressMessage);

            string commit = commits[i];

            if (!_toSkip.Add((commit, type)))
            {
                await LogAsync($"[{commit}] Skipping build");
                continue;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            await RunProcessAsync("git", $"checkout {commit}", workDir: "runtime");

            List<string> changedFiles = await GitHelper.GetChangedFilesAsync(this, "HEAD~1", "runtime");

            if (CanSkipBuilding(changedFiles))
            {
                await LogAsync($"[{commit}] Skipping build (docs-only changes)");
                continue;
            }

            if (await CoreRootAPI.GetAsync(this, commit, type) is not null)
            {
                await LogAsync($"[{commit}] Skipping build");
                continue;
            }

            string logPrefix = $"{commit[..20]} {type}";

            if (!await TryBuildAsync(logPrefix, type))
            {
                await LogAsync($"[{logPrefix}] Build failed. Retrying ...");

                await Task.Delay(1_000);

                if (await RunProcessAsync("git", "clean -fdx", logPrefix: logPrefix, workDir: "runtime", checkExitCode: false) != 0)
                {
                    await Task.Delay(10_000);
                    await RunProcessAsync("git", "clean -fdx", logPrefix: logPrefix, workDir: "runtime", checkExitCode: false);
                }

                if (!await TryBuildAsync(logPrefix, type))
                {
                    await LogAsync($"[{logPrefix}] Build failed again. Skipping ...");
                    continue;
                }
            }

            string artifactsDir = await CopyArtifactsAsync(logPrefix, commit, type);
            _builtThisSession++;

            PendingTasks.Enqueue(Task.Run(async () =>
            {
                string archivePath = await CompressArtifactsAsync(logPrefix, type, artifactsDir);

                await LogAsync($"[{logPrefix}] Uploading CoreRoot ...");
                string blobName = $"{commit}_{Arch}_{Os}_{type}.7z";
                await UploadCoreRootAsync(blobName, archivePath);
                await CoreRootAPI.SaveAsync(this, commit, type, blobName);

                File.Delete(archivePath);

                await LogAsync($"[{logPrefix}] Done in {FormatElapsedTime(stopwatch.Elapsed)}");
            }));
        }

        static bool CanSkipBuilding(List<string> changedFiles)
        {
            foreach (string file in changedFiles)
            {
                if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file is "LICENSE.TXT" or "PATENTS.TXT" or "THIRD-PARTY-NOTICES.TXT")
                {
                    continue;
                }    

                return false;
            }

            return true;
        }
    }

    private async Task UploadCoreRootAsync(string blobName, string filePath)
    {
        string containerSasUrl = Metadata["CoreRootSasUri"];
        int queryOffset = containerSasUrl.IndexOf('?');
        string url = $"{containerSasUrl.AsSpan(0, queryOffset)}/{blobName}{containerSasUrl.AsSpan(queryOffset)}";

        await using var fs = File.OpenRead(filePath);
        using var content = new StreamContent(fs);

        using var response = await HttpClient.PostAsync(url, content, JobTimeout);
        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> TryBuildAsync(string logPrefix, string type)
    {
        await LogAsync($"[{logPrefix}] Building ...");

        try
        {
            string targets = $"clr+libs -rc {type} -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}";

            if (OperatingSystem.IsWindows())
            {
                await RunProcessAsync("build.cmd", targets, logPrefix: logPrefix, workDir: "runtime");
            }
            else
            {
                await RunProcessAsync("bash", $"build.sh {targets}", logPrefix: logPrefix, workDir: "runtime");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> CopyArtifactsAsync(string logPrefix, string commit, string type)
    {
        await LogAsync($"[{logPrefix}] Compressing {type} artifacts ...");

        string artifactsDir = $"artifacts-{commit}-{type}";
        Directory.CreateDirectory(artifactsDir);

        await RuntimeHelpers.CopyReleaseArtifactsAsync(this, logPrefix, artifactsDir, runtimeConfig: type == "release" ? "Release" : "Checked");

        foreach (string file in Directory.EnumerateFiles(artifactsDir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("SuperFileCheck/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("R2RTest/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("PDB/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("PdbChecker/", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }

        return artifactsDir;
    }

    private async Task<string> CompressArtifactsAsync(string logPrefix, string type, string artifactsDir)
    {
        await LogAsync($"[{logPrefix}] Compressing {type} artifacts ...");

        string archiveName = $"{artifactsDir}.7z";

        await RunProcessAsync("7z", $"a -mx9 -md512m {archiveName} .", logPrefix: logPrefix, workDir: artifactsDir, priority: ProcessPriorityClass.BelowNormal);
        File.Move(Path.Combine(artifactsDir, archiveName), archiveName);

        Directory.Delete(artifactsDir, recursive: true);

        return archiveName;
    }
}
