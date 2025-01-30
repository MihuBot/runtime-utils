using Azure.Storage.Blobs;

namespace Runner.Jobs;

internal sealed class CoreRootGenerationJob : JobBase
{
    public CoreRootGenerationJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamOrFastestDiskAsync();

        await CloneRuntimeAndSetupToolsAsync();

        await BuildCoreRootsAsync();
    }

    private async Task CloneRuntimeAndSetupToolsAsync()
    {
        Task cloneRuntimeTask = RuntimeHelpers.CloneRuntimeMainAsync(this);

        Task aptGetTask = RunProcessAsync("apt-get", "install -y zip wget p7zip-full", logPrefix: "Install tools");

        await aptGetTask;
        await cloneRuntimeTask;
    }

    private async Task BuildCoreRootsAsync()
    {
        const int LastNDays = 60;
        string type = "release";
        string os = OperatingSystem.IsWindows() ? "windows" : "linux";
        string archOsType = $"&arch={Arch}&os={os}&type={type}";

        List<string> commits = await GitHelper.ListCommitsAsync(this, LastNDays, "runtime");
        commits.Reverse();

        await LogAsync($"Found {commits.Count} commits in the last {LastNDays} days");

        var container = new BlobContainerClient(new Uri(Metadata["CoreRootSasUri"]));

        for (int i = 0; i < commits.Count; i++)
        {
            if (MaxRemainingTime.TotalHours < 1)
            {
                await LogAsync("Approaching job duration limit. Stopping ...");
                break;
            }

            LastProgressSummary = $"Processing commit {i + 1}/{commits.Count}";

            Stopwatch stopwatch = Stopwatch.StartNew();

            string commit = commits[i];
            await RunProcessAsync("git", $"checkout {commit}", workDir: "runtime");

            List<string> changedFiles = await GitHelper.GetChangedFilesAsync(this, "HEAD~1", "runtime");

            if (CanSkipBuilding(changedFiles))
            {
                await LogAsync($"[{commit}] Skipping build (docs-only changes)");
                continue;
            }

            string logPrefix = $"{commit[..20]} {type}";

            CoreRootEntry? entry = await SendAsyncCore(HttpMethod.Get, $"CoreRoot/Get?sha={commit}{archOsType}", content: null,
                async response => await response.Content.ReadFromJsonAsync<CoreRootEntry>(JobTimeout));

            if (entry is not null)
            {
                await LogAsync($"[{logPrefix}] Skipping build (CoreRoot already exists)");
                continue;
            }

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

            string archivePath = await CompressArtifactsAsync(logPrefix, commit, type);

            await LogAsync($"[{logPrefix}] Uploading CoreRoot ...");
            var blob = container.GetBlobClient($"{commit}_{Arch}_{os}_{type}.7z");
            await blob.UploadAsync(archivePath, overwrite: true, JobTimeout);
            await SendAsyncCore<object>(HttpMethod.Get, $"CoreRoot/Save?jobId={JobId}&sha={commit}{archOsType}&blobName={blob.Name}");

            File.Delete(archivePath);

            await LogAsync($"[{logPrefix}] Done in {FormatElapsedTime(stopwatch.Elapsed)}");
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

    private async Task<string> CompressArtifactsAsync(string logPrefix, string commit, string type)
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

        string archiveName = $"{artifactsDir}.7z";

        await RunProcessAsync("7z", $"a -mx9 -md512m {archiveName} .", logPrefix: logPrefix, workDir: artifactsDir);
        File.Move(Path.Combine(artifactsDir, archiveName), archiveName);

        Directory.Delete(artifactsDir, recursive: true);

        return archiveName;
    }

    private sealed class CoreRootEntry { }
}
