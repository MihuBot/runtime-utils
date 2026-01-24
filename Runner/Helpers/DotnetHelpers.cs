namespace Runner.Helpers;

internal static class DotnetHelpers
{
    public static async Task KillRemainingDotnetProcessesAsync(JobBase job)
    {
        string[] processNames = ["dotnet", "MSBuild", "corerun", "superpmi", "test_fx_ver", ".NET Host"];

        if (OperatingSystem.IsWindows())
        {
            foreach (string processName in processNames)
            {
                await job.RunProcessAsync("taskkill", $"/IM \"{processName}.exe\" /F", logPrefix: "Cleanup .NET processes", checkExitCode: false);
            }
        }
        else
        {
            foreach (string processName in processNames)
            {
                await job.RunProcessAsync("pkill", $"-f \"{processName}\"", logPrefix: "Cleanup .NET processes", checkExitCode: false);
            }
        }
    }
}
