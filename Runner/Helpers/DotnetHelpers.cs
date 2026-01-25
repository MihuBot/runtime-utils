namespace Runner.Helpers;

internal static class DotnetHelpers
{
    public static async Task KillRemainingDotnetProcessesAsync(JobBase job)
    {
        foreach (Process proc in Process.GetProcesses())
        {
            try
            {
                string name = proc.ProcessName;

                if (name.Contains("dotnet", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("MSBuild", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("corerun", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("superpmi", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("test_fx_ver", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(".NET Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (proc.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    await job.LogAsync($"Killing process {proc.Id} ({proc.ProcessName})");
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                await job.LogAsync($"Failed to kill process {proc.Id} ({proc.ProcessName}): {ex}");
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    public static async Task InstallDotnetSdkAsync(JobBase job, string globalJsonPath, string? installDir = null) =>
        await InstallDotnetSdkAsyncCore(job, $"--jsonfile {globalJsonPath}", installDir);

    public static async Task InstallDotnetDailySdkAsync(JobBase job, int version, string? installDir = null) =>
        await InstallDotnetSdkAsyncCore(job, $"--channel {version}.0 --quality daily", installDir);

    private static async Task InstallDotnetSdkAsyncCore(JobBase job, string versionArgs, string? installDir = null)
    {
        installDir ??= "/usr/lib/dotnet";

        if (!File.Exists("dotnet-install.sh"))
        {
            await job.RunProcessAsync("wget", "https://dot.net/v1/dotnet-install.sh");
        }

        await job.RunProcessAsync("bash", $"dotnet-install.sh {versionArgs} --install-dir {installDir}");
    }
}
