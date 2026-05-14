using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Runner.Jobs;

internal sealed class NuGetExtraAssembliesJob : JobBase
{
    private const string OutputDir = "nuget-extra-assemblies";

    private NuGetClient _nuget = null!;

    public NuGetExtraAssembliesJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamOrFastestDiskAsync();

        Metadata[nameof(BaseRepo)] = Metadata[nameof(PrRepo)] = "dotnet/runtime";
        Metadata[nameof(BaseBranch)] = Metadata[nameof(PrBranch)] = "main";

        var services = new ServiceCollection();
        services.AddHybridCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<HybridCache>();

        const string DepCacheDir = "dep-cache";
        Directory.CreateDirectory(DepCacheDir);
        _nuget = new NuGetClient(HttpClient, cache, DepCacheDir, LogAsync);

        Directory.CreateDirectory(OutputDir);

        if (!TryGetArgument("count", out int packageCount) || packageCount <= 0)
        {
            packageCount = 1000;
        }

        var runtimeTask = BuildRuntimeAsync();
        var approvedPackages = await GatherNuGetInfoAsync(packageCount);
        await runtimeTask;

        await ProcessApprovedPackagesAsync(approvedPackages);

        await ZipAndUploadArtifactAsync(OutputDir, OutputDir);
    }

    private async Task BuildRuntimeAsync()
    {
        await JitDiffJob.CloneRuntimeAndSetupToolsAsync(this);
        await JitDiffJob.BuildAndCopyRuntimeBranchBitsAsync(this, "main", uploadArtifacts: false);
        await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);
    }

    private async Task<List<(string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps)>> GatherNuGetInfoAsync(int count)
    {
        await LogAsync($"Searching for top {count} NuGet packages ...");
        var packages = await _nuget.SearchTopPackagesAsync(count);
        await LogAsync($"Found {packages.Count} packages to evaluate");

        int skippedLicense = 0, skippedNoDlls = 0;

        string packagesDir = "nuget-packages-temp";
        Directory.CreateDirectory(packagesDir);

        var approvedPackages = new List<(string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps)>();

        for (int i = 0; i < packages.Count; i++)
        {
            var (id, version) = packages[i];
            string prefix = $"[NuGet {i + 1}/{packages.Count}] {id} {version}";
            LastProgressSummary = $"Checking NuGet package {i + 1}/{packages.Count}. Approved {approvedPackages.Count} so far.";

            string? license = await _nuget.GetLicenseExpressionAsync(id, version);
            if (!NuGetClient.IsPermissiveLicense(license))
            {
                await LogAsync($"{prefix} - skipped (license: {license ?? "none"})");
                skippedLicense++;
                continue;
            }

            string pkgDir = Path.Combine(packagesDir, id);
            Directory.CreateDirectory(pkgDir);

            var (dll, selectedTfm) = await _nuget.DownloadAndExtractBestDllAsync(id, version, pkgDir);
            if (dll is null || selectedTfm is null)
            {
                await LogAsync($"{prefix} - skipped (no suitable DLL)");
                skippedNoDlls++;
                DeleteDirectory(pkgDir);
                continue;
            }

            var deps = await _nuget.ResolveAllDependenciesAsync(id, version, selectedTfm);
            if (deps is null)
            {
                await LogAsync($"{prefix} - skipped (dependency with non-permissive license)");
                skippedLicense++;
                DeleteDirectory(pkgDir);
                continue;
            }

            approvedPackages.Add((id, version, pkgDir, dll, deps));
            await LogAsync($"{prefix} - approved ({deps.Count} deps)");
        }

        await LogAsync($"Phase 1 complete: {approvedPackages.Count} approved, {skippedLicense} license, {skippedNoDlls} no DLLs");

        if (approvedPackages.Count == 0)
        {
            throw new Exception("No packages passed license and DLL checks.");
        }

        return approvedPackages;
    }

    private async Task ProcessApprovedPackagesAsync(List<(string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps)> approvedPackages)
    {
        int included = 0, skippedJitDiff = 0;
        string diffOutputDir = "nuget-diff-temp";
        Directory.CreateDirectory(diffOutputDir);

        int parallelism = Math.Min(Math.Min(Environment.ProcessorCount / 2, approvedPackages.Count), 8);
        parallelism = Math.Max(parallelism, 1);
        var packageQueue = new Queue<(string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps)>(approvedPackages);

        try
        {
            await Parallel.ForAsync(0, parallelism, async (index, _) =>
            {
                string coreRoot = $"artifacts-main_{index}";
                string checkedClr = $"clr-checked-main_{index}";
                CopyDirectory("artifacts-main", coreRoot);
                CopyDirectory("clr-checked-main", checkedClr);

                try
                {
                    while (true)
                    {
                        (string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps) pkg;
                        lock (packageQueue)
                        {
                            if (!packageQueue.TryDequeue(out pkg))
                                break;
                        }

                        LastProgressSummary = $"Processing NuGet packages. ~{packageQueue.Count} remaining, {included} included.";

                        string pkgDiffDir = Path.Combine(diffOutputDir, pkg.Id);
                        Directory.CreateDirectory(pkgDiffDir);

                        try
                        {
                            // Download dependency DLLs into the package directory
                            if (pkg.Deps.Count > 0)
                            {
                                foreach (var (depId, depVersion) in pkg.Deps)
                                {
                                    await _nuget.DownloadDependencyDllsAsync(depId, depVersion, pkg.PkgDir);
                                }
                            }

                            // Validate main DLL with jit-diff
                            string dllPath = Path.GetFullPath(Path.Combine(pkg.PkgDir, pkg.Dll));

                            try
                            {
                                await JitDiffUtils.RunJitDiffOnAssembliesAsync(this, coreRoot, checkedClr, pkgDiffDir, [dllPath]);
                            }
                            catch
                            {
                                await LogAsync($"[NuGet] {pkg.Id} - skipped (jit-diff failed)");
                                Interlocked.Increment(ref skippedJitDiff);
                                continue;
                            }

                            string outputPkgDir = Path.Combine(OutputDir, pkg.Id);
                            Directory.CreateDirectory(outputPkgDir);

                            foreach (string file in Directory.GetFiles(pkg.PkgDir, "*.dll"))
                            {
                                File.Copy(file, Path.Combine(outputPkgDir, Path.GetFileName(file)), overwrite: true);
                            }

                            // Write DiffAssemblies.txt so JitDiffJob only diffs the main DLL, not dependencies
                            File.WriteAllText(Path.Combine(outputPkgDir, "DiffAssemblies.txt"), pkg.Dll);

                            Interlocked.Increment(ref included);
                            await LogAsync($"[NuGet] {pkg.Id} - included ({pkg.Deps.Count} deps)");
                        }
                        finally
                        {
                            DeleteDirectory(pkg.PkgDir);
                            DeleteDirectory(pkgDiffDir);
                        }
                    }
                }
                finally
                {
                    DeleteDirectory(coreRoot);
                    DeleteDirectory(checkedClr);
                }
            });
        }
        finally
        {
            DeleteDirectory(diffOutputDir);
        }

        await LogAsync($"Summary: {included} included, {skippedJitDiff} jit-diff failed");

        if (included == 0)
        {
            throw new Exception("No packages were included in the archive.");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, true);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
