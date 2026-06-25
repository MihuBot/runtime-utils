using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace Runner.Helpers;

internal static partial class JitDiffUtils
{
    // Files inside a core_root that jit-diff overwrites in place when it installs the base/diff JIT
    // (see jitutils PmiDiffTool.InstallBaseJit/InstallDiffJit/RestoreDefaultJit). Each parallel jit-diff
    // worker therefore needs its own writable copy of these; everything else in the core_root is only
    // read while diffing and can be shared between workers via hard links.
    private static readonly HashSet<string> s_jitDiffMutatedCoreRootFiles = new(StringComparer.OrdinalIgnoreCase) { "libclrjit.so", "clrjit.dll" };

    /// <summary>
    /// Creates a lightweight per-worker clone of a core_root for running jit-diff in parallel.
    /// The bulk of the (multi-hundred-MB) shared framework is shared with <paramref name="source"/> via
    /// hard links - a single physical copy on disk (and in RAM, on a ramdisk) - while the JIT libraries
    /// that jit-diff overwrites in place are given to the worker as private, writable copies. This avoids
    /// duplicating the whole core_root for every parallel invocation. Falls back to a full copy where hard
    /// links are unavailable (e.g. across file systems).
    /// </summary>
    public static void CreateCoreRootCloneForJitDiff(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

            // The JIT is overwritten in place by jit-diff, so each worker needs its own real copy.
            // Everything else is shared via hard links. Symbolic links can't be used here: corerun finds
            // its core root via /proc/self/exe and coreclr loads the JIT from libcoreclr.so's directory,
            // both of which canonicalize symlinks back to the shared 'source'. A symlinked corerun /
            // libcoreclr.so therefore makes every worker load 'source's JIT instead of its private copy
            // (and jit-diff's File.Copy onto a symlinked libclrjit.so would clobber the shared original).
            // A hard link is just another directory entry for the same inode, with no such path indirection.
            if (s_jitDiffMutatedCoreRootFiles.Contains(Path.GetFileName(file)) || !TryCreateHardLink(file, destFile))
            {
                File.Copy(file, destFile, overwrite: true);
            }
        }
    }

    private static bool TryCreateHardLink(string source, string destination)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return LinkUnix(source, destination) == 0;
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true)]
    private static partial int LinkUnix(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    public static async Task RunJitDiffOnFrameworksAsync(JobBase job, string coreRootFolder, string checkedClrFolder, string outputFolder)
    {
        await RunJitDiffAsync(job, coreRootFolder, checkedClrFolder, outputFolder, "--frameworks");
    }

    public static async Task RunJitDiffOnAssembliesAsync(JobBase job, string coreRootFolder, string checkedClrFolder, string outputFolder, string[] assemblyPaths, string? logPrefix = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(assemblyPaths.Length);

        await RunJitDiffAsync(job, coreRootFolder, checkedClrFolder, outputFolder, string.Join(' ', assemblyPaths.Select(path => $"--assembly \"{path}\"")), logPrefix, cancellationToken);
    }

    private static async Task RunJitDiffAsync(JobBase job, string coreRootFolder, string checkedClrFolder, string outputFolder, string frameworksOrAssembly, string? logPrefix = null, CancellationToken cancellationToken = default)
    {
        bool useCctors = !job.TryGetFlag("nocctors");
        bool useTier0 = job.TryGetFlag("tier0");
        bool verbose = job.TryGetFlag("verbose");
        bool debugInfo = job.TryGetFlag("debuginfo");
        bool gcInfo = job.TryGetFlag("gcinfo");
        bool sequential = job.TryGetFlag("sequential");

        List<(string, string)> envVars = [];

        if (job.TryGetFlag("JitDisasmWithGC"))
        {
            envVars.Add(("DOTNET_JitDisasmWithGC", "1"));
        }

        if (job.TryGetFlag("DisableOptimizedThreadStaticAccess"))
        {
            envVars.Add(("DOTNET_DisableOptimizedThreadStaticAccess", "1"));
        }

        try
        {
            await job.RunProcessAsync("jitutils/bin/jit-diff",
                $"diff " +
                (debugInfo ? "--debuginfo " : "") +
                (verbose ? "--verbose " : "") +
                (useCctors ? "--cctors " : "") +
                (useTier0 ? "--tier0 " : "") +
                (gcInfo ? "--gcinfo " : "") +
                (sequential ? "--sequential " : "") +
                $"--output {outputFolder} " +
                $"{frameworksOrAssembly} --pmi " +
                $"--core_root {coreRootFolder} " +
                $"--base {checkedClrFolder}",
                logPrefix: $"jit-diff {logPrefix ?? coreRootFolder}",
                envVars: envVars,
                cancellationToken: cancellationToken);
        }
        finally
        {
            // jit-diff backs up the core_root's JIT to 'backup-<jit>' before swapping in the base/diff JIT
            // and restores from it afterwards, but never deletes the backup. Delete it: a leftover backup
            // in a shared core_root would be hard-linked into every parallel worker's clone by
            // CreateCoreRootCloneForJitDiff, and concurrent jit-diff File.Copy writes onto that single
            // shared inode collide with "the file is being used by another process" on Linux.
            foreach (string jitName in s_jitDiffMutatedCoreRootFiles)
            {
                try { File.Delete(Path.Combine(coreRootFolder, $"backup-{jitName}")); }
                catch { }
            }
        }
    }

    public static async Task<string> RunJitAnalyzeAsync(JobBase job, string mainDirectory, string prDirectory, int count = 100)
    {
        List<string> output = [];

        await job.RunProcessAsync("jitutils/bin/jit-analyze",
            $"-b {mainDirectory} -d {prDirectory} -r -c {count}",
            output,
            logPrefix: "jit-analyze",
            checkExitCode: false);

        return string.Join('\n', output);
    }

    public static (string Description, string DasmFile, string Name)[] ParseDiffAnalyzeEntries(string diffSource, bool regressions)
    {
        ReadOnlySpan<char> text = diffSource.ReplaceLineEndings("\n");

        string start = regressions ? "Top method regressions" : "Top method improvements";
        int index = text.IndexOf(start, StringComparison.Ordinal);

        if (index < 0)
        {
            return Array.Empty<(string, string, string)>();
        }

        text = text.Slice(index);
        text = text.Slice(text.IndexOf('\n') + 1);
        text = text.Slice(0, text.IndexOf("\n\n", StringComparison.Ordinal));

        return text
            .ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JitDiffRegressionNameRegex().Match(line))
            .Where(m => m.Success)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .ToArray();
    }

    public static string GetCommentMarkdown(string[] diffs, int lengthLimit, bool regressions, out bool lengthLimitExceeded)
    {
        lengthLimitExceeded = false;

        if (diffs.Length == 0)
        {
            return string.Empty;
        }

        bool someChangesSkipped = false;

        List<string> changesToShow = [];

        if (diffs.Sum(d => d.Length) <= lengthLimit)
        {
            changesToShow.AddRange(diffs);
        }
        else
        {
            int maxLengthPerEntry = lengthLimit;

            if (diffs.Length >= 5 && diffs.Average(d => d.Length) < lengthLimit / 3)
            {
                maxLengthPerEntry = lengthLimit / 3;
            }

            int currentLength = 0;

            foreach (var change in diffs)
            {
                if (change.Length > maxLengthPerEntry)
                {
                    someChangesSkipped = true;
                    lengthLimitExceeded = true;
                    continue;
                }

                if (currentLength + change.Length > lengthLimit)
                {
                    lengthLimitExceeded = true;
                    continue;
                }

                changesToShow.Add(change);
                currentLength += change.Length;
            }
        }

        StringBuilder sb = new();

        sb.AppendLine($"## Top method {(regressions ? "regressions" : "improvements")}");
        sb.AppendLine();

        foreach (string md in changesToShow)
        {
            sb.AppendLine(md);
        }

        sb.AppendLine();

        if (someChangesSkipped)
        {
            sb.AppendLine("Note: some changes were skipped as they were too large to fit into a comment.");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static async Task<(string[] Diffs, bool NoisyDiffsRemoved)> GetDiffMarkdownAsync(
        JobBase job,
        (string Description, string DasmFile, string Name)[] diffs,
        string mainDasmDirectory,
        string prDasmDirectory,
        Func<string, string?>? tryGetExtraInfo,
        Func<string, string> replaceMethodName,
        int maxCount)
    {
        if (diffs.Length == 0)
        {
            return (Array.Empty<string>(), false);
        }

        bool includeKnownNoise = job.TryGetFlag("includeKnownNoise");
        bool includeRemovedMethod = job.TryGetFlag("includeRemovedMethodImprovements");
        bool IncludeNewMethod = job.TryGetFlag("includeNewMethodRegressions");

        bool noisyMethodsRemoved = false;
        string?[] results = new string[diffs.Length];

        await Parallel.ForAsync(0, diffs.Length, async (i, _) =>
        {
            var diff = diffs[i];

            if (!includeRemovedMethod && IsRemovedMethod(diff.Description))
            {
                return;
            }

            if (!IncludeNewMethod && IsNewMethod(diff.Description))
            {
                return;
            }

            string mainDiffsFile = $"{mainDasmDirectory}/{diff.DasmFile}";
            string prDiffsFile = $"{prDasmDirectory}/{diff.DasmFile}";

            await job.LogAsync($"Generating diffs for {diff.Name}");

            StringBuilder sb = new();

            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{diff.Description} - {replaceMethodName(diff.Name)}</summary>");
            sb.AppendLine();

            if (tryGetExtraInfo?.Invoke(diff.Name) is { } extraInfo)
            {
                sb.AppendLine(extraInfo);
                sb.AppendLine();
            }

            sb.AppendLine("```diff");

            using var baseFile = new TempFile("txt");
            using var prFile = new TempFile("txt");

            await File.WriteAllTextAsync(baseFile.Path, await TryGetMethodDumpAsync(mainDiffsFile, diff.Name));
            await File.WriteAllTextAsync(prFile.Path, await TryGetMethodDumpAsync(prDiffsFile, diff.Name));

            List<string> lines = await GitHelper.DiffAsync(job, baseFile.Path, prFile.Path, fullContext: true);

            if (lines.Count == 0)
            {
                return;
            }

            foreach (string line in lines)
            {
                if (line.StartsWith("; ============================================================", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!includeKnownNoise && LineIsIndicativeOfKnownNoise(line.AsSpan().TrimStart()))
                {
                    noisyMethodsRemoved = true;
                    return;
                }

                sb.AppendLine(line);
            }

            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();

            results[i] = sb.ToString();
        });

        results = results
            .Where(r => !string.IsNullOrEmpty(r))
            .Take(maxCount)
            .ToArray();

        return (results, noisyMethodsRemoved)!;

        static bool IsRemovedMethod(ReadOnlySpan<char> description) =>
            description.Contains("-100.", StringComparison.Ordinal);

        static bool IsNewMethod(ReadOnlySpan<char> description) =>
            description.Contains("∞ of base", StringComparison.Ordinal) ||
            description.Contains("Infinity of base", StringComparison.Ordinal);

        static bool LineIsIndicativeOfKnownNoise(ReadOnlySpan<char> line)
        {
            if (line.IsEmpty || line[0] is not ('+' or '-'))
            {
                return false;
            }

            return
                line.Contains("CORINFO_HELP_CLASSINIT_SHARED_DYNAMICCLASS", StringComparison.Ordinal) ||
                line.Contains("ProcessorIdCache:RefreshCurrentProcessorId", StringComparison.Ordinal) ||
                line.Contains("Interop+Sys:SchedGetCpu()", StringComparison.Ordinal);
        }
    }

    public static async Task<string> TryGetMethodDumpAsync(string diffPath, string methodName)
    {
        using var fs = File.Open(diffPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var pipe = PipeReader.Create(fs);

        bool foundPrefix = false;
        bool foundSuffix = false;
        byte[] prefix = Encoding.ASCII.GetBytes($"; Assembly listing for method {methodName}");
        byte[] suffix = Encoding.ASCII.GetBytes("; ============================================================");

        StringBuilder sb = new();

        while (true)
        {
            ReadResult result = await pipe.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;
            SequencePosition? position = null;

            do
            {
                position = buffer.PositionOf((byte)'\n');

                if (position != null)
                {
                    var line = buffer.Slice(0, position.Value);

                    ProcessLine(
                        line.IsSingleSegment ? line.FirstSpan : line.ToArray(),
                        prefix, suffix, ref foundPrefix, ref foundSuffix);

                    if (foundPrefix)
                    {
                        sb.AppendLine(Encoding.UTF8.GetString(line));

                        if (sb.Length > 1024 * 1024)
                        {
                            return string.Empty;
                        }
                    }

                    if (foundSuffix)
                    {
                        return sb.ToString();
                    }

                    buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
                }
            }
            while (position != null);

            pipe.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted)
            {
                return string.Empty;
            }
        }

        static void ProcessLine(ReadOnlySpan<byte> line, byte[] prefix, byte[] suffix, ref bool foundPrefix, ref bool foundSuffix)
        {
            if (foundPrefix)
            {
                if (line.StartsWith(suffix))
                {
                    foundSuffix = true;
                }
            }
            else
            {
                if (line.StartsWith(prefix))
                {
                    foundPrefix = true;
                }
            }
        }
    }

    [GeneratedRegex(@" *(.*?) : (.*?) - ([^ ]*)")]
    private static partial Regex JitDiffRegressionNameRegex();
}
