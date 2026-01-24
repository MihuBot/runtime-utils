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
}
