using System.IO.Compression;

namespace Runner.Jobs;

internal sealed partial class FuzzLibrariesJob : JobBase
{
    private const string DeploymentPath = "runtime/src/libraries/Fuzzing/DotnetFuzzing/deployment";

    public FuzzLibrariesJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new Exception("This job is only supported on Windows");
        }

        Match match = FuzzerNameRegex().Match(CustomArguments);
        if (!match.Success)
        {
            throw new Exception("Invalid arguments. Expected 'fuzz <fuzzer name>'");
        }

        string fuzzerNamePattern = match.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(fuzzerNamePattern))
        {
            throw new Exception("Invalid fuzzer name. Expected something like 'fuzz HttpHeaders'");
        }

        await ChangeWorkingDirectoryToRamOrFastestDiskAsync();

        await RuntimeHelpers.CloneRuntimeAsync(this);

        await BuildRuntimeAndPrepareFuzzerAsync();

        await RunFuzzersAsync(fuzzerNamePattern);
    }

    private async Task BuildRuntimeAndPrepareFuzzerAsync()
    {
        const string BuildRuntimeBat = "build-runtime.bat";
        const string PrepareFuzzerBat = "prepare-fuzzer.bat";

        File.WriteAllText(BuildRuntimeBat,
            $$"""
            cd runtime
            git switch pr

            call .\build.cmd clr+libs -rc Checked -c Debug {{RuntimeHelpers.LibrariesExtraBuildArgs}}

            .dotnet\dotnet build-server shutdown
            cd ..
            dotnet build-server shutdown
            """);

        File.WriteAllText(PrepareFuzzerBat,
            $$"""
            cd runtime\src\libraries\Fuzzing\DotnetFuzzing
            ..\..\..\..\.dotnet\dotnet build
            ..\..\..\..\.dotnet\dotnet tool install --tool-path . SharpFuzz.CommandLine
            call run.bat
            """);

        if (IsArm)
        {
            Patch("runtime/src/libraries/Fuzzing/DotnetFuzzing/run.bat",
                content => content.Replace("win-x64", "win-arm64", StringComparison.Ordinal));

            Patch("runtime/src/libraries/Fuzzing/DotnetFuzzing/DotnetFuzzing.csproj",
                content => content.Replace("win-x64", "win-arm64", StringComparison.Ordinal));
        }

        await RunProcessAsync(BuildRuntimeBat, string.Empty);

        await DotnetHelpers.KillRemainingDotnetProcessesAsync(this);

        await RunProcessAsync(PrepareFuzzerBat, string.Empty);

        static void Patch(string path, Func<string, string> patch)
        {
            string content = File.ReadAllText(path);
            content = patch(content);
            File.WriteAllText(path, content);
        }
    }

    private async Task RunFuzzersAsync(string fuzzerNamePattern)
    {
        string[] availableFuzzers = Directory.GetDirectories(DeploymentPath)
            .Select(Path.GetFileName)
            .ToArray()!;

        await LogAsync($"Available fuzzers: {string.Join(", ", availableFuzzers)}");

        var matchingFuzzers = availableFuzzers
            .Where(f => f.Contains(fuzzerNamePattern, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matchingFuzzers.Length == 0)
        {
            try
            {
                var pattern = new Regex(fuzzerNamePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                matchingFuzzers = availableFuzzers
                    .Where(f => pattern.IsMatch(f))
                    .ToArray();
            }
            catch { }
        }

        if (matchingFuzzers.Length == 0)
        {
            throw new Exception($"Fuzzer '{fuzzerNamePattern}' not found. Available fuzzers: {string.Join(", ", availableFuzzers)}");
        }

        await LogAsync($"Matched: {string.Join(", ", matchingFuzzers)}");

        int maxDurationPerFuzzer =
            TryGetFlag("long") ? int.MaxValue :
            TryGetFlag("short") ? 600 :
            TryGetFlag("dry") ? 60 :
            TryGetArgument("duration", out int sec) ? sec :
            3600;

        for (int i = 0; i < matchingFuzzers.Length; i++)
        {
            string fuzzerName = matchingFuzzers[i];

            int remainingFuzzers = matchingFuzzers.Length - i;
            TimeSpan remainingTime = MaxRemainingTime - TimeSpan.FromMinutes(5);
            int durationSeconds = (int)(remainingTime / remainingFuzzers).TotalSeconds;
            durationSeconds = Math.Clamp(durationSeconds, 60, maxDurationPerFuzzer);

            await RunFuzzerAsync(fuzzerName, durationSeconds);
        }
    }

    private async Task<bool> RunFuzzerAsync(string fuzzerName, int durationSeconds)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        string nameWithoutFuzzerSuffix = fuzzerName.EndsWith("Fuzzer", StringComparison.OrdinalIgnoreCase)
            ? fuzzerName.Substring(0, fuzzerName.Length - "Fuzzer".Length)
            : fuzzerName;

        string fuzzerDirectory = $"{DeploymentPath}/{fuzzerName}";
        string inputsDirectory = $"{fuzzerName}-inputs";
        string artifactPathPrefix = $"{fuzzerName}-artifact-";
        string coverageDirectory = $"{fuzzerName}-coverage";

        Directory.CreateDirectory(inputsDirectory);

        try
        {
            var remoteInputsZipBlob = PersistentStateClient.GetBlobClient($"{inputsDirectory}.zip");
            if (!TryGetFlag("skipExistingInputs") && await remoteInputsZipBlob.ExistsAsync(JobTimeout))
            {
                var content = (await remoteInputsZipBlob.DownloadContentAsync(JobTimeout)).Value;
                ZipFile.ExtractToDirectory(content.Content.ToStream(), inputsDirectory);

                await LogAsync($"Downloaded {Directory.EnumerateFiles(inputsDirectory).Count()} inputs from previous fuzzing runs");
            }
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to download previous inputs archive: {ex}");
        }

        int parallelism = Environment.ProcessorCount;
        if (parallelism > 4)
        {
            parallelism--;
        }

        await LogAsync($"Starting {parallelism} parallel fuzzer instances");

        using CancellationTokenSource failureCts = new();
        int failureStackUploaded = 0;

        Task forEachAsyncTask = Parallel.ForEachAsync(Enumerable.Range(1, parallelism), JobTimeout, async (i, _) =>
        {
            List<string> output = [];
            string number = i.ToString().PadLeft(parallelism.ToString().Length, '0');
            string artifactPath = $"{artifactPathPrefix}{number}";

            try
            {
                await RunProcessAsync(
                    $"{fuzzerDirectory}/local-run.bat",
                    $"-timeout=60 -max_total_time={durationSeconds} {inputsDirectory} -exact_artifact_path={artifactPath} -print_final_stats=1",
                    output,
                    $"{nameWithoutFuzzerSuffix} {number}",
                    cancellationToken: failureCts.Token);
            }
            catch (Exception ex)
            {
                await LogAsync($"Fuzzer {i} failed: {ex.Message}");

                if (ex is not OperationCanceledException &&
                    !failureCts.IsCancellationRequested &&
                    File.Exists(artifactPath))
                {
                    string[] stack = output
                        .AsEnumerable()
                        .Reverse()
                        .TakeWhile(line => !(line.Contains("cov: ", StringComparison.Ordinal) && line.Contains("exec/s: ", StringComparison.Ordinal)))
                        .SkipWhile(line => string.IsNullOrWhiteSpace(line) || line.StartsWith("stat::", StringComparison.Ordinal))
                        .Reverse()
                        .ToArray();

                    if (stack.Length > 0 && Interlocked.Exchange(ref failureStackUploaded, 1) == 0)
                    {
                        await UploadTextArtifactAsync($"{fuzzerName}-stack.txt", string.Join('\n', stack));
                        await UploadArtifactAsync(artifactPath, $"{fuzzerName}-input.bin");
                    }
                }

                failureCts.Cancel();
            }
        });

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(0.5));

            while (!forEachAsyncTask.IsCompleted && await timer.WaitForNextTickAsync(failureCts.Token))
            {
                int remaining = durationSeconds - (int)stopwatch.Elapsed.TotalSeconds;
                if (remaining > 0)
                {
                    LastProgressSummary = $"Remaining time for {nameWithoutFuzzerSuffix}: ~{remaining / 60} min";
                }
            }
        }
        catch { }

        await forEachAsyncTask;

        if (Directory.EnumerateFiles(inputsDirectory).Any())
        {
            await ZipAndUploadArtifactAsync(inputsDirectory, inputsDirectory);

            await RunProcessAsync(
                "powershell.exe",
                $"-File \"{DeploymentPath}/../collect-coverage.ps1\" {fuzzerName} {inputsDirectory} -OutputDir {coverageDirectory}",
                logPrefix: $"{nameWithoutFuzzerSuffix} coverage",
                checkExitCode: false);

            await ZipAndUploadArtifactAsync(coverageDirectory, coverageDirectory);
        }

        return failureStackUploaded == 0;
    }

    [GeneratedRegex(@"^fuzz ([^ ]+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FuzzerNameRegex();
}
