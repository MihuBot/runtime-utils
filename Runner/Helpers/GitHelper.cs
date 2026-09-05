using System.Globalization;

namespace Runner.Helpers;

internal static class GitHelper
{
    public static async Task<List<string>> DiffAsync(JobBase job, string leftFile, string rightFile, bool fullContext = false, bool preserveHunkHeaders = false)
    {
        List<string> lines = [];

        int exitCode = await job.RunProcessAsync("git",
            $"diff --no-index --histogram {(fullContext ? "-U1000000" : "")} \"{leftFile}\" \"{rightFile}\"",
            lines,
            checkExitCode: false,
            suppressOutputLogs: true,
            suppressStartingLog: true);

        if (exitCode is not (0 or 1))
        {
            throw new InvalidOperationException($"git diff failed with exit code {exitCode}.");
        }

        lines.RemoveAll(line => ShouldSkipLine(line) && !(preserveHunkHeaders && line.StartsWith("@@", StringComparison.Ordinal)));

        return lines;
    }

    private static bool ShouldSkipLine(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan().TrimStart();

        return
            span.StartsWith("diff --git", StringComparison.Ordinal) ||
            span.StartsWith("index ", StringComparison.Ordinal) ||
            span.StartsWith("+++", StringComparison.Ordinal) ||
            span.StartsWith("---", StringComparison.Ordinal) ||
            span.StartsWith("@@", StringComparison.Ordinal) ||
            span.StartsWith("\\ No newline at end of file", StringComparison.Ordinal);
    }

    public static async Task<List<string>> GetChangedFilesAsync(JobBase job, string baselineRef, string workDir)
    {
        List<string> lines = [];

        await job.RunProcessAsync("git",
            $"diff --name-only {baselineRef}",
            lines,
            workDir: workDir,
            suppressOutputLogs: true,
            suppressStartingLog: true);

        return lines;
    }

    /// <summary>
    /// Lists the commits from the last <paramref name="lastNDays"/> days, newest first, along with each
    /// commit's committer timestamp.
    /// </summary>
    public static async Task<List<(string Sha, DateTime CommitTime)>> ListCommitsAsync(JobBase job, int lastNDays, string workDir)
    {
        List<string> lines = [];

        await job.RunProcessAsync("git",
            $"log --pretty=format:%H;%cI --since={lastNDays}days",
            lines,
            workDir: workDir,
            checkExitCode: false,
            suppressOutputLogs: true,
            suppressStartingLog: true);

        List<(string Sha, DateTime CommitTime)> commits = new(lines.Count);

        foreach (string line in lines)
        {
            if (line.Split(';') is [string sha, string time] && ParseCommitTime(time) is { } commitTime)
            {
                commits.Add((sha, commitTime));
            }
        }

        return commits;
    }

    /// <summary>Parses a strict ISO 8601 git timestamp (<c>%cI</c>) as UTC.</summary>
    private static DateTime? ParseCommitTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset time)
            ? time.UtcDateTime
            : null;

    public static async Task<string> GetCurrentCommitAsync(JobBase job, string workDir, string? branch = null)
    {
        List<string> lines = [];

        await job.RunProcessAsync("git",
            $"log {branch} -1 --pretty=format:%H",
            lines,
            workDir: workDir,
            checkExitCode: false,
            suppressOutputLogs: true,
            suppressStartingLog: true);

        return lines.FirstOrDefault() ?? string.Empty;
    }
}
