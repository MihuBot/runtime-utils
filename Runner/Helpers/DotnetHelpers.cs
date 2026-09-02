using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

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

    /// <summary>
    /// Environment variables that point child processes at the SDK we installed into <paramref name="installDir"/>.
    /// </summary>
    /// <remarks>
    /// The machines we run on already have another .NET install (e.g. /usr/local/dotnet on Helix, which is also
    /// first on PATH), and it is generally older than the daily SDK the job installs. Running
    /// "{installDir}/dotnet run" only controls which SDK/muxer builds the app - the apphost it then launches
    /// resolves its shared framework through DOTNET_ROOT / PATH / the default install location, so it can pick
    /// the older install and fail to start with "You must install or update .NET to run this application".
    /// Pinning DOTNET_ROOT and prepending the install directory to PATH keeps the whole process tree on one install.
    /// </remarks>
    public static List<(string, string)> GetSdkEnvVars(string? installDir = null)
    {
        installDir ??= DefaultInstallPath;

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        char separator = OperatingSystem.IsWindows() ? ';' : ':';

        return
        [
            ("DOTNET_ROOT", installDir),
            // The arch-specific variable takes precedence over DOTNET_ROOT in the apphost, so it has to
            // be overridden too - otherwise a stale one in the environment would defeat the pin above.
            ($"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}", installDir),
            ("PATH", string.IsNullOrEmpty(path) ? installDir : $"{installDir}{separator}{path}"),
        ];
    }

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
