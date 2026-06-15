namespace Runner.Helpers;

internal static partial class RuntimePatches
{
    private const string RuntimeDir = "runtime";

    private const string MarvinFile = "src/libraries/System.Private.CoreLib/src/System/Marvin.cs";

    [GeneratedRegex(@"private static unsafe ulong GenerateSeed\(\)[\s\S]*?\n        \}")]
    private static partial Regex MarvinGenerateSeedRegex();

    private const string MarvinGenerateSeedReplacement =
        "private static ulong GenerateSeed() => 0xD1FFAB11Eul;";

    private static readonly string[] s_patchedFiles = [MarvinFile];

    public static async Task ApplyPatchesAsync(JobBase job)
    {
        HashSet<string> prChangedFiles = new(
            await GitHelper.GetChangedFilesAsync(job, "main..pr", RuntimeDir),
            StringComparer.OrdinalIgnoreCase);

        await TryPatchMarvinAsync(job, prChangedFiles);
    }

    public static async Task RevertPatchesAsync(JobBase job)
    {
        // Restore any patched source files so subsequent `git switch`/builds see a clean tree.
        // Already-built artifacts are unaffected.
        foreach (string file in s_patchedFiles)
        {
            string path = $"{RuntimeDir}/{file}";
            if (!File.Exists(path))
            {
                continue;
            }

            await job.RunProcessAsync("git",
                $"checkout -- {file}",
                workDir: RuntimeDir,
                checkExitCode: false,
                suppressStartingLog: true);
        }
    }

    private static async Task TryPatchMarvinAsync(JobBase job, HashSet<string> prChangedFiles)
    {
        if (prChangedFiles.Contains(MarvinFile))
        {
            await job.LogAsync($"[Patches] Skipping Marvin patch - PR modifies {MarvinFile}");
            return;
        }

        string path = $"{RuntimeDir}/{MarvinFile}";
        if (!File.Exists(path))
        {
            await job.LogAsync($"[Patches] Skipping Marvin patch - file not found at {path}");
            return;
        }

        string content = await File.ReadAllTextAsync(path);

        if (!MarvinGenerateSeedRegex().IsMatch(content))
        {
            await job.LogAsync("[Patches] Skipping Marvin patch - GenerateSeed pattern not found");
            return;
        }

        string patched = MarvinGenerateSeedRegex().Replace(content, MarvinGenerateSeedReplacement, count: 1);

        await File.WriteAllTextAsync(path, patched);
        await job.LogAsync("[Patches] Replaced Marvin.GenerateSeed with constant 0xD1FFAB11E");
    }
}
