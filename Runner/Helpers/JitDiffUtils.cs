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

    public static async Task RunJitDiffOnAssembliesAsync(JobBase job, string coreRootFolder, string checkedClrFolder, string outputFolder, string[] assemblyPaths, string? logPrefix = null, List<string>? output = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(assemblyPaths.Length);

        await RunJitDiffAsync(job, coreRootFolder, checkedClrFolder, outputFolder, string.Join(' ', assemblyPaths.Select(path => $"--assembly \"{path}\"")), logPrefix, output, cancellationToken);
    }

    // jit-diff prints a line like "Error running <corerun> on <assembly path>" for every assembly whose
    // dasm generation failed (see jitutils DiffTool.RunDasmTool). Extract those assembly file names so the
    // caller can report them and drop their (missing/one-sided) dasm from the cross-branch comparison.
    public static IEnumerable<string> ParseFailedAssemblyNames(List<string> jitDiffOutput)
    {
        foreach (string line in jitDiffOutput)
        {
            Match match = JitDiffAssemblyFailureRegex().Match(line);
            if (match.Success)
            {
                yield return Path.GetFileName(match.Groups[1].Value.Trim());
            }
        }
    }

    [GeneratedRegex(@"Error running \S+ on (.+)$")]
    private static partial Regex JitDiffAssemblyFailureRegex();

    private static async Task RunJitDiffAsync(JobBase job, string coreRootFolder, string checkedClrFolder, string outputFolder, string frameworksOrAssembly, string? logPrefix = null, List<string>? output = null, CancellationToken cancellationToken = default)
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
                output: output,
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

    internal static bool LineIsIndicativeOfKnownNoise(ReadOnlySpan<char> line)
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
