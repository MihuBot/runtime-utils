namespace Runner.Helpers;

internal static class RuntimeHelpers
{
    private static void AssertIsLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }
    }

    public static string LibrariesExtraBuildArgs => OperatingSystem.IsLinux()
        ? "-p:RunAnalyzers=false -p:ApiCompatValidateAssemblies=false"
        : "/p:RunAnalyzers=false /p:ApiCompatValidateAssemblies=false";

    private static Task InstallDependenciesAsync(JobBase job, string logPrefix) =>
        AptHelper.RunWithAptLockAsync(() =>
            job.RunProcessAsync("bash", "-x eng/common/native/install-dependencies.sh linux", logPrefix: logPrefix, workDir: "runtime"));

    public static async Task CloneRuntimeMainAsync(JobBase job)
    {
        const string LogPrefix = "Setup runtime";

        if (OperatingSystem.IsLinux())
        {
            string script =
                $$$"""
                set -e

                git clone --no-tags --branch main --single-branch --progress https://github.com/dotnet/runtime runtime
                cd runtime

                git log -1
                chmod 777 build.sh
                git config --global user.email build@build.foo
                git config --global user.name build
                """;

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("setup-runtime.sh", script);
            await job.RunProcessAsync("bash", "-x setup-runtime.sh", logPrefix: LogPrefix);

            await InstallDependenciesAsync(job, LogPrefix);
        }
        else
        {
            string script =
                $$$"""
                git config --system core.longpaths true
                git clone --no-tags --branch main --single-branch --progress https://github.com/dotnet/runtime runtime
                cd runtime

                git log -1
                git config --global user.email build@build.foo
                git config --global user.name build
                """;

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("clone-runtime.bat", script);
            await job.RunProcessAsync("clone-runtime.bat", string.Empty, logPrefix: LogPrefix);
        }
    }

    public static async Task CloneRuntimeAsync(JobBase job)
    {
        const string LogPrefix = "Setup runtime";

        bool runtimeAlreadyExists = Directory.Exists("runtime");
        string baselineBranch = job.BaseCommit is null ? job.BaseBranch : "baseline";

        if (OperatingSystem.IsLinux())
        {
            string initialClone = runtimeAlreadyExists ?
                $$$"""
                cd runtime
                git switch {{{job.BaseBranch}}}
                git pull origin
                """ :
                $$$"""
                git clone --no-tags --branch {{{job.BaseBranch}}} --single-branch --progress https://github.com/{{{job.BaseRepo}}} runtime
                cd runtime
                """;

            string createPrBranch = runtimeAlreadyExists ?
                """
                git branch -D pr
                git switch -c pr
                git remote remove combineWith1
                """ :
                """
                git switch -c pr
                """;

            string script = UpdateMergePlaceholders(
                $$$"""
                set -e

                {{{initialClone}}}

                git log -1
                chmod 777 build.sh
                git config --global user.email build@build.foo
                git config --global user.name build

                {{CHECKOUT_BASE_COMMIT}}

                {{MERGE_BASELINE_BRANCHES}}

                {{{createPrBranch}}}

                {{MERGE_PR_BRANCHES}}

                git switch {{{baselineBranch}}}
                """);

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("setup-runtime.sh", script);
            await job.RunProcessAsync("bash", "-x setup-runtime.sh", logPrefix: LogPrefix);

            if (!runtimeAlreadyExists)
            {
                await InstallDependenciesAsync(job, LogPrefix);
            }
        }
        else
        {
            if (runtimeAlreadyExists)
            {
                throw new UnreachableException();
            }

            string script = UpdateMergePlaceholders(
                $$$"""
                git config --system core.longpaths true
                git clone --no-tags --branch {{{job.BaseBranch}}} --single-branch --progress https://github.com/{{{job.BaseRepo}}} runtime
                cd runtime

                git log -1
                git config --global user.email build@build.foo
                git config --global user.name build

                {{CHECKOUT_BASE_COMMIT}}

                {{MERGE_BASELINE_BRANCHES}}

                git switch -c pr

                {{MERGE_PR_BRANCHES}}

                git switch {{{baselineBranch}}}
                """);

            await job.LogAsync($"Using runtime setup script:\n{script}");
            await File.WriteAllTextAsync("clone-runtime.bat", script);
            await job.RunProcessAsync("clone-runtime.bat", string.Empty, logPrefix: LogPrefix);
        }

        if (job.HasPatch)
        {
            await ApplyPatchAsync(job);
        }

        await job.LogAsync($"main commit: {await GitHelper.GetCurrentCommitAsync(job, "runtime", baselineBranch)}");
        await job.LogAsync($"pr commit: {await GitHelper.GetCurrentCommitAsync(job, "runtime", "pr")}");

        string UpdateMergePlaceholders(string template)
        {
            return template
                .ReplaceLineEndings()
                .Replace("{{CHECKOUT_BASE_COMMIT}}", GetBaseCommitScript(), StringComparison.Ordinal)
                .Replace("{{MERGE_BASELINE_BRANCHES}}", GetMergeScript("dependsOn"), StringComparison.Ordinal)
                .Replace("{{MERGE_PR_BRANCHES}}", GetMergeScript("combineWith"), StringComparison.Ordinal);
        }

        string GetBaseCommitScript() =>
            job.BaseCommit is null ? string.Empty :
            $"""
            git fetch origin {job.BaseCommit}
            git switch -C {baselineBranch} {job.BaseCommit}
            """;

        string GetMergeScript(string name)
        {
            int counter = 0;

            List<(string Repo, string Branch)> prList = new(GetPRList(job, name));

            if (name == "combineWith" && !job.HasPatch)
            {
                prList.Insert(0, (job.PrRepo, job.PrBranch));
            }

            return string.Join('\n', prList
                .Select(pr =>
                {
                    int index = ++counter;
                    string remoteName = $"{name}{index}";

                    return
                        $"git remote add {remoteName} https://github.com/{pr.Repo}\n" +
                        $"git fetch {remoteName} {pr.Branch}\n" +
                        $"git log {remoteName}/{pr.Branch} -1\n" +
                        $"git merge --no-edit {remoteName}/{pr.Branch}\n" +
                        $"git log -1\n";
                }));
        };

        static (string Repo, string Branch)[] GetPRList(JobBase job, string name)
        {
            if (job.Metadata.TryGetValue(name, out string? value))
            {
                return value.Split(',').Select(pr =>
                {
                    string[] parts = pr.Split(';');
                    return (parts[0], parts[1]);
                }).ToArray();
            }

            return [];
        }
    }

    private static async Task ApplyPatchAsync(JobBase job)
    {
        string patchPath = Path.GetFullPath("changes.patch");

        using (HttpResponseMessage response = await job.HttpClient.GetAsync($"Jobs/Patch?jobId={job.JobId}", job.JobTimeout))
        {
            response.EnsureSuccessStatusCode();
            await using Stream source = await response.Content.ReadAsStreamAsync(job.JobTimeout);
            await using FileStream destination = File.Create(patchPath);
            await source.CopyToAsync(destination, job.JobTimeout);
        }

        await job.LogAsync("Applying submitted patch");
        await job.RunProcessAsync("git", "switch pr", workDir: "runtime");
        await job.RunProcessAsync("git", $"apply --index --whitespace=nowarn \"{patchPath}\"", workDir: "runtime");
        await job.RunProcessAsync("git", "diff --cached --stat", logPrefix: "Patch summary", workDir: "runtime");
        await job.RunProcessAsync("git", "commit -m \"Apply submitted patch\"", workDir: "runtime");
        string baselineBranch = job.BaseCommit is null ? job.BaseBranch : "baseline";
        await job.RunProcessAsync("git", $"switch {baselineBranch}", workDir: "runtime");
    }

    public static async Task InstallRuntimeDotnetSdkAsync(JobBase job, string? installDir = null)
    {
        await DotnetHelpers.InstallDotnetSdkAsync(job, "runtime/global.json", installDir);
    }

    public static async Task CopyAspNetSharedFrameworkToCoreRootAsync(JobBase job, string coreRootFolder)
    {
        string sharedDir = Path.Combine(DotnetHelpers.DefaultInstallPath, "shared", "Microsoft.AspNetCore.App");

        if (!Directory.Exists(sharedDir))
        {
            await job.LogAsync("ASP.NET shared framework not found, skipping");
            return;
        }

        string? latestVersion = Directory.GetDirectories(sharedDir)
            .Select(Path.GetFileName)
            .OrderByDescending(v => Version.TryParse(v, out var ver) ? ver : new Version())
            .FirstOrDefault();

        if (latestVersion is null)
            return;

        string aspnetDir = Path.Combine(sharedDir, latestVersion);
        int copied = 0;

        foreach (string dll in Directory.GetFiles(aspnetDir, "*.dll"))
        {
            string dest = Path.Combine(coreRootFolder, Path.GetFileName(dll));
            if (!File.Exists(dest))
            {
                File.Copy(dll, dest);
                copied++;
            }
        }

        await job.LogAsync($"Copied {copied} ASP.NET shared framework DLLs from {latestVersion} to {coreRootFolder}");
    }

    public static async Task CopyReleaseArtifactsAsync(JobBase job, string logPrefix, string destination, string runtimeConfig = "Release")
    {
        AssertIsLinux();

        await job.RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{JobBase.Arch}.{runtimeConfig}/. {destination}", logPrefix: logPrefix);

        const string BaseDirectory = "runtime/artifacts/bin/runtime";

        string folder = Directory.GetDirectories(BaseDirectory)
            .Select(f => Path.GetRelativePath(BaseDirectory, f))
            .Where(f => f.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains("Release", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains("linux", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.Contains(JobBase.Arch, StringComparison.OrdinalIgnoreCase))
            .Single();

        await job.RunProcessAsync("cp", $"-r {BaseDirectory}/{folder}/. {destination}", logPrefix: logPrefix);
    }
}
