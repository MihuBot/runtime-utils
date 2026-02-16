using System.Text.Json.Nodes;

namespace Runner.Helpers;

internal static class DotnetHelpers
{
    public static string DefaultInstallPath => OperatingSystem.IsLinux()
        ? "/usr/lib/dotnet"
        : throw new NotImplementedException();

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

    public static int GetDotnetVersion(string repository = "runtime")
    {
        // "version": "10.0.100-preview.1.12345.6", => 10
        return int.Parse(File.ReadAllLines($"{repository}/global.json")
            .First(line => line.Contains("version", StringComparison.OrdinalIgnoreCase))
            .Split(':')[1] //  "10.0.100-preview.1.12345.6"
            .Split('.')[0] //  "10
            .TrimStart(' ', '"'));
    }

    public static async Task InstallDotnetSdkAsync(JobBase job, string globalJsonPath, string? installDir = null) =>
        await InstallDotnetSdkAsyncCore(job, $"--jsonfile {globalJsonPath}", installDir);

    public static async Task InstallDotnetDailySdkAsync(JobBase job, int version, string? installDir = null) =>
        await InstallDotnetSdkAsyncCore(job, $"--channel {version}.0 --quality daily", installDir);

    public static async Task<string> GetInstalledDotnetSdkVersionAsync(JobBase job, string? installDir = null)
    {
        installDir ??= DefaultInstallPath;

        List<string> output = [];
        await job.RunProcessAsync($"{installDir}/dotnet", "--version", output, logPrefix: "SDK version");
        return output.Last().Trim();
    }

    private static async Task InstallDotnetSdkAsyncCore(JobBase job, string versionArgs, string? installDir = null)
    {
        installDir ??= DefaultInstallPath;

        if (!File.Exists("dotnet-install.sh"))
        {
            await job.RunProcessAsync("wget", "https://dot.net/v1/dotnet-install.sh");
        }

        await job.RunProcessAsync("bash", $"dotnet-install.sh {versionArgs} --install-dir {installDir}");
    }

    public static async Task PatchVersionInGlobalJson(JobBase job, string globalJsonPath, string newVersion)
    {
        var json = JsonNode.Parse(File.ReadAllText(globalJsonPath))!;

        if (json["sdk"] is { } sdkNode)
        {
            sdkNode["version"] = newVersion;
        }

        if (json["tools"] is { } toolsNode)
        {
            toolsNode["dotnet"] = newVersion;
        }

        File.WriteAllText(globalJsonPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
