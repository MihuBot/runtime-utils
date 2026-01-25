using System.IO.Compression;

namespace Runner.Jobs;

internal sealed partial class BenchmarkLibrariesJob : JobBase
{
    public BenchmarkLibrariesJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        CoreRootAPI.CoreRootEntry[]? entries = null;

        if (BenchmarkWithCompareRangeRegex().Match(CustomArguments) is { Success: true } rangeMatch)
        {
            entries = await CoreRootAPI.ListAsync(this, rangeMatch.Groups[2].Value, "release");

            if (entries.Length == 0)
            {
                throw new Exception("Failed to get the core run entries");
            }
        }

        await ChangeWorkingDirectoryToRamOrFastestDiskAsync(allowRamDisk: entries is null || entries.Length * 2 <= GetTotalSystemMemoryGB());

        Task clonePerformanceTask = CloneDotnetPerformanceAndSetupToolsAsync();

        string[] coreRuns;

        if (entries is not null)
        {
            await clonePerformanceTask;

            PendingTasks.Enqueue(DotnetHelpers.InstallDotnetSdkAsync(this, "performance/global.json"));

            coreRuns = await DownloadCoreRootsAsync(entries);
        }
        else
        {
            await RuntimeHelpers.CloneRuntimeAsync(this);

            await clonePerformanceTask;

            await JitDiffJob.BuildAndCopyRuntimeBranchBitsAsync(this, "main", uploadArtifacts: false, buildChecked: false);

            await RunProcessAsync("git", "switch pr", workDir: "runtime");

            try
            {
                await JitDiffJob.BuildAndCopyRuntimeBranchBitsAsync(this, "pr", uploadArtifacts: false, buildChecked: false);
            }
            catch
            {
                // Workaround for https://github.com/dotnet/runtime/issues/111731
                await LogAsync($"PR build failed. Retrying ...");
                await Task.Delay(1_000);

                if (await RunProcessAsync("git", "clean -fdx", workDir: "runtime", checkExitCode: false) != 0)
                {
                    await Task.Delay(10_000);
                    await RunProcessAsync("git", "clean -fdx", workDir: "runtime", checkExitCode: false);
                }

                await JitDiffJob.BuildAndCopyRuntimeBranchBitsAsync(this, "pr", uploadArtifacts: false, buildChecked: false, canSkipRebuild: false);
            }

            await DotnetHelpers.InstallDotnetSdkAsync(this, "performance/global.json");

            coreRuns = ["artifacts-main/corerun", "artifacts-pr/corerun"];
        }

        await WaitForPendingTasksAsync();

        await RunBenchmarksAsync(coreRuns);
    }

    private async Task CloneDotnetPerformanceAndSetupToolsAsync()
    {
        Task clonePerformanceTask = Task.Run(async () =>
        {
            (string repo, string branch) = GetDotnetPerformanceRepoSource();

            await RunProcessAsync("git", $"clone --no-tags --depth=1 -b {branch} --progress https://github.com/{repo} performance", logPrefix: "Clone performance");

            // Performance's global.json is out of date
            byte[] bytes = await HttpClient.GetByteArrayAsync("https://raw.githubusercontent.com/dotnet/runtime/refs/heads/main/global.json");
            await File.WriteAllBytesAsync("performance/global.json", bytes);

            if (TryGetFlag("medium") || TryGetFlag("long"))
            {
                string? path = Directory.EnumerateFiles("performance", "*.cs", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.EndsWith("RecommendedConfig.cs", StringComparison.Ordinal));

                if (string.IsNullOrEmpty(path))
                {
                    await LogAsync("Failed to find RecommendedConfig.cs");
                }
                else
                {
                    string jobType = $"Job.{(TryGetFlag("medium") ? "Medium" : "Long")}Run";

                    string source = File.ReadAllText(path);
                    string newSource = RecommendedConfigJobTypeRegex().Replace(source, $"job = {jobType};");

                    if (source == newSource)
                    {
                        await LogAsync("Failed to find the existing Job type");
                    }
                    else
                    {
                        File.WriteAllText(path, newSource);
                        await LogAsync($"Replaced Job type with {jobType}");
                    }
                }
            }

            {
                const string Path = "performance/src/Directory.Build.props";
                string source = File.ReadAllText(Path);
                source = source.Replace(
                    "<TreatWarningsAsErrors>True</TreatWarningsAsErrors>",
                    "<TreatWarningsAsErrors>false</TreatWarningsAsErrors>",
                    StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(Path, source);
            }

            {
                const string Path = "performance/src/benchmarks/micro/MicroBenchmarks.csproj";
                string source = File.ReadAllText(Path);
                source = source.Replace(
                    "<OutputType>Exe</OutputType>",
                    "<NoWarn>$(NoWarn);NU1605</NoWarn>\r\n    <OutputType>Exe</OutputType>",
                    StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(Path, source);
            }

            if (TryGetFlag("parallel"))
            {
                {
                    var customNugetFeedDirectory = new DirectoryInfo("CustomNugetFeed");
                    customNugetFeedDirectory.Create();

                    const string ZipPath = "BenchmarkDotNet-custom.zip";
                    await PersistentStateClient.GetBlobClient(ZipPath).DownloadToAsync(ZipPath, JobTimeout);
                    ZipFile.ExtractToDirectory(ZipPath, customNugetFeedDirectory.FullName);

                    string version = customNugetFeedDirectory.GetFiles()
                        .Select(f => BenchmarkDotNetPackageVersionRegex().Match(f.Name))
                        .First(m => m.Success)
                        .Groups[1].Value;

                    {
                        const string NuGetConfigPath = "performance/NuGet.config";
                        string source = File.ReadAllText(NuGetConfigPath);
                        source = source.Replace(
                            "<clear />",
                            $"<clear />\r\n    <add key=\"local\" value=\"{customNugetFeedDirectory.FullName}\" />",
                            StringComparison.OrdinalIgnoreCase);
                        File.WriteAllText(NuGetConfigPath, source);
                    }

                    {
                        const string VersionsPath = "performance/eng/Versions.props";
                        string source = File.ReadAllText(VersionsPath);
                        source = BenchmarkDotNetReferenceVersionRegex().Replace(source, $"<BenchmarkDotNetVersion>{version}</BenchmarkDotNetVersion>");
                        File.WriteAllText(VersionsPath, source);
                    }

                    await LogAsync($"Configured custom BenchmarkDotNet version: {version}");
                }
            }
        });

        Task aptGetTask = RunProcessAsync("apt-get", "install -y zip wget p7zip-full", logPrefix: "Install tools");

        Directory.CreateDirectory("artifacts-main");
        Directory.CreateDirectory("artifacts-pr");

        await aptGetTask;
        await clonePerformanceTask;

        (string Repo, string Branch) GetDotnetPerformanceRepoSource()
        {
            foreach (string arg in CustomArguments.Split(' '))
            {
                if (!arg.Contains("/compare/", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(arg, UriKind.Absolute, out Uri? uri) &&
                    uri.IsAbsoluteUri &&
                    uri.Scheme == Uri.UriSchemeHttps &&
                    GitHubBranchRegex().Match(arg) is { Success: true } match)
                {
                    Group branch = match.Groups[2];
                    return (match.Groups[1].Value, branch.Success ? branch.Value : "main");
                }
            }

            return ("dotnet/performance", "main");
        }
    }

    private async Task<string[]> DownloadCoreRootsAsync(CoreRootAPI.CoreRootEntry[] entries)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            CoreRootAPI.CoreRootEntry entry = entries[i];
            entry.Directory = $"cr-{i.ToString().PadLeft(4, '0')}-{entry.Sha}";
        }

        await LogAsync($"Downloading {entries.Length} CoreRoots ...");

        await Parallel.ForEachAsync(entries, async (entry, _) =>
        {
            Directory.CreateDirectory(entry.Directory!);

            using var archive = new TempFile("7z");
            byte[] archiveBytes = await SendAsyncCore(HttpMethod.Get, entry.Url!, content: null, async response => await response.Content.ReadAsByteArrayAsync());
            File.WriteAllBytes(archive.Path, archiveBytes);

            await RunProcessAsync("7z", $"x {archive.Path} -o{entry.Directory} ", logPrefix: $"Extract {entry.Sha}");
        });

        return entries.Select(entry => $"{entry.Directory}/corerun").ToArray();
    }

    private async Task RunBenchmarksAsync(string[] coreRunPaths)
    {
        const string HiddenColumns = "Job StdDev RatioSD Median Min Max OutlierMode MemoryRandomization Gen0 Gen1 Gen2";

        string filter = FilterNameRegex().Match(CustomArguments).Groups[1].Value;
        filter = filter.Trim().Trim('`').Trim();

        if (!string.IsNullOrWhiteSpace(filter) && !filter.Contains('*'))
        {
            filter = $"*{filter}*";
        }

        int dotnetVersion = DotnetHelpers.GetDotnetVersion("performance");

        string coreRuns = string.Join(' ', coreRunPaths.Select(p => $"\"{Path.GetFullPath(p)}\""));

        int coreCount = (int)(HardwareInfo?.CpuList.First().NumberOfCores ?? 0);
        if (coreCount > Environment.ProcessorCount)
        {
            await LogAsync($"Warning: Core count {coreCount} is greater than the number of logical processors {Environment.ProcessorCount}??");
            coreCount = Environment.ProcessorCount / 2;
        }

        string parallelSuffix = TryGetFlag("parallel") ? $"--parallel {coreCount}" : "";

        string? artifactsDir = null;

        await RunProcessAsync("/usr/lib/dotnet/dotnet",
            $"run -c Release --framework net{dotnetVersion}.0 -- --filter {filter} -h {HiddenColumns} --corerun {coreRuns} {parallelSuffix}",
            workDir: "performance/src/benchmarks/micro",
            processLogs: line =>
            {
                // Example:
                // ramdisk/performance/artifacts/bin/MicroBenchmarks/Release/net9.0/BenchmarkDotNet.Artifacts/results/TestName-report-github.md
                // we want performance/artifacts/bin/MicroBenchmarks/Release/net9.0/BenchmarkDotNet.Artifacts
                if (artifactsDir is null &&
                    line.AsSpan().TrimEnd().EndsWith("-report-github.md", StringComparison.Ordinal) &&
                    Path.GetDirectoryName(Path.GetDirectoryName(line.AsSpan().Trim())).TrimEnd(['/', '\\']).ToString() is { } dir &&
                    dir.EndsWith("BenchmarkDotNet.Artifacts", StringComparison.Ordinal))
                {
                    const string PerformanceDir = "/performance/";

                    if (dir.Contains(PerformanceDir, StringComparison.Ordinal))
                    {
                        dir = dir.Substring(dir.IndexOf(PerformanceDir, StringComparison.Ordinal) + 1);
                    }

                    artifactsDir = dir;
                }

                // ** Remained 420 (74.5 %) benchmark(s) to run. Estimated finish 2024-06-20 2:54 (0h 40m from now) **
                if (line.Contains("benchmark(s) to run. Estimated finish", StringComparison.Ordinal) &&
                    BdnProgressSummaryRegex().Match(line) is { Success: true } match)
                {
                    LastProgressSummary = $"{match.Groups[1].ValueSpan} ({match.Groups[2].ValueSpan} %) benchmarks remain. Estimated time: {match.Groups[3].ValueSpan}";
                }

                return line;
            });

        LastProgressSummary = null;

        if (string.IsNullOrEmpty(artifactsDir))
        {
            throw new Exception("Couldn't find the artifacts directory");
        }

        await ZipAndUploadArtifactAsync("BDN_Artifacts", artifactsDir);

        List<string> results = new();

        foreach (var resultsMd in Directory.GetFiles(artifactsDir, "*-report-github.md", SearchOption.AllDirectories))
        {
            await LogAsync($"Reading {resultsMd} ...");

            StringBuilder result = new();

            string friendlyName = Path.GetFileName(resultsMd);
            friendlyName = friendlyName.Substring(0, friendlyName.Length - "-report-github.md".Length);

            result.AppendLine("<details>");
            result.AppendLine($"<summary>{friendlyName}</summary>");
            result.AppendLine();

            foreach (string rawLine in await File.ReadAllLinesAsync(resultsMd))
            {
                string line = rawLine.Trim();

                if (string.IsNullOrEmpty(line) ||
                    line.StartsWith(".NET SDK ", StringComparison.Ordinal) ||
                    line.StartsWith("[Host]", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("Job-"))
                    line = "  " + line;

                if (line.Contains('|'))
                {
                    // Workaround for BDN's bug: https://github.com/dotnet/BenchmarkDotNet/issues/2545
                    if (line.EndsWith(":|-"))
                        line = line.Remove(line.Length - 1);

                    line = PipeCharInTableCellRegex().Replace(line, static match =>
                        $"{match.Groups[1].ValueSpan}\\|{match.Groups[2].ValueSpan}");
                }

                line = line.Replace("/artifacts-main/corerun", "Main");
                line = line.Replace("/artifacts-pr/corerun", "PR");

                line = CommitCoreRunReplacementRegex().Replace(line, match =>
                {
                    ReadOnlySpan<char> sha = match.Groups[1].ValueSpan;
                    return $"[`{sha[..10].ToString()}`](https://github.com/dotnet/runtime/commit/{sha})";
                });

                result.AppendLine(line);
            }

            result.AppendLine();
            result.AppendLine("</details>");

            results.Add(result.ToString());
        }

        string combinedMarkdown = string.Join("\n\n", results);

        await UploadTextArtifactAsync("results.md", combinedMarkdown);
    }

    [GeneratedRegex(@"^benchmark ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FilterNameRegex();

    // @MihuBot benchmark GetUnicodeCategory https://github.com/dotnet/runtime/compare/4bb0bcd9b5c47df97e51b462d8204d66c7d470fc...c74440f8291edd35843f3039754b887afe61766e
    [GeneratedRegex(@"^benchmark ([^ ]+) https:\/\/github\.com\/dotnet\/runtime\/compare\/([a-f0-9]{40}\.\.\.[a-f0-9]{40})", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BenchmarkWithCompareRangeRegex();

    // ** Remained 420 (74.5 %) benchmark(s) to run. Estimated finish 2024-06-20 2:54 (0h 40m from now) **
    // 420    74.5    0h 40m
    [GeneratedRegex(@"Remained (\d+) \((.*?) %\).*?\(([\dhms ]+) from", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BdnProgressSummaryRegex();

    // https://github.com/MihaZupan/performance
    // https://github.com/MihaZupan/performance/tree/regex
    // https://github.com/MihaZupan/performance/blob/regex/.gitignore#L5
    // we want 'MihaZupan/performance' and optionally 'regex'
    [GeneratedRegex(@"https://github\.com/([A-Za-z\d-_\.]+/[A-Za-z\d-_\.]+)(?:/(?:tree|blob)/([A-Za-z\d-_\.]+)(?:[\?#/].*)?)?", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GitHubBranchRegex();

    // | Count  | Main | (?i)Sher[a-z]+|Hol[a-z]+ |
    // We want '+|H' to replace it with '+\|H'
    [GeneratedRegex(@"([^ \n:\\])\|([^ \n:])")]
    private static partial Regex PipeCharInTableCellRegex();

    // Matches https://github.com/dotnet/performance/blob/d0d7ea34e98ca19f8264a17abe05ef6f73e888ba/src/harness/BenchmarkDotNet.Extensions/RecommendedConfig.cs#L33-L38
    [GeneratedRegex(@"job = Job\..*?;", RegexOptions.Singleline)]
    private static partial Regex RecommendedConfigJobTypeRegex();

    [GeneratedRegex("/cr-[0-9]+-([a-f0-9]{40})/corerun")]
    private static partial Regex CommitCoreRunReplacementRegex();

    [GeneratedRegex(@"<BenchmarkDotNetVersion>.*?<\/BenchmarkDotNetVersion>")]
    private static partial Regex BenchmarkDotNetReferenceVersionRegex();

    [GeneratedRegex(@"BenchmarkDotNet\.(\d.*?)\.nupkg")]
    private static partial Regex BenchmarkDotNetPackageVersionRegex();
}
