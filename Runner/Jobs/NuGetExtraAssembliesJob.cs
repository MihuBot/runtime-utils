using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Runner.Jobs;

internal sealed class NuGetExtraAssembliesJob : JobBase
{
    private const string OutputDir = "nuget-extra-assemblies";

    private NuGetClient _nuget = null!;

    public NuGetExtraAssembliesJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    private static HashSet<string> ExtraPackages { get; } = new(
        [
            "CommunityToolkit.HighPerformance", "CommunityToolkit.Mvvm", "CommunityToolkit.Diagnostics",
            "SixLabors.ImageSharp", "SixLabors.Fonts", "SixLabors.ImageSharp.Drawing",
            "dnlib", "AsmResolver", "BepuPhysics", "Avalonia", "Uno.UI"
        ],
        StringComparer.OrdinalIgnoreCase);

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

        await ZipAndUploadArtifactAsync(OutputDir, OutputDir, maxCompression: true);
    }

    private async Task BuildRuntimeAsync()
    {
        await JitDiffJob.CloneRuntimeAndSetupToolsAsync(this);
        await JitDiffJob.BuildAndCopyRuntimeBranchBitsAsync(this, "main", uploadArtifacts: false);
        await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);
        await RuntimeHelpers.CopyAspNetSharedFrameworkToCoreRootAsync(this, "artifacts-main");
    }

    private async Task<List<(string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps)>> GatherNuGetInfoAsync(int count)
    {
        await LogAsync($"Searching for top {count} NuGet packages ...");
        var packages = await _nuget.SearchTopPackagesAsync(count);
        await LogAsync($"Found {packages.Count} packages to evaluate");

        // Merge in ExtraPackages, resolving their latest versions and deduplicating
        var existingIds = new HashSet<string>(packages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        foreach (string extraId in ExtraPackages)
        {
            if (!existingIds.Add(extraId))
                continue;

            string? version = await _nuget.ResolveLatestVersionAsync(extraId);
            if (version is not null)
            {
                packages.Add((extraId, version));
                await LogAsync($"Added extra package {extraId} {version}");
            }
        }

        int skippedLicense = 0, skippedNoDlls = 0;

        string packagesDir = "nuget-packages-temp";
        Directory.CreateDirectory(packagesDir);

        var approvedPackages = new List<(string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps)>();

        for (int i = 0; i < packages.Count; i++)
        {
            var (id, version) = packages[i];
            string prefix = $"[NuGet {i + 1}/{packages.Count}] {id} {version}";
            LastProgressSummary = $"Checking NuGet package {i + 1}/{packages.Count}. Approved {approvedPackages.Count} so far.";

            bool isExtra = ExtraPackages.Contains(id);
            if (!isExtra)
            {
                string? license = await _nuget.GetLicenseExpressionAsync(id, version);
                if (!NuGetClient.IsPermissiveLicense(license))
                {
                    await LogAsync($"{prefix} - skipped (license: {license ?? "none"})");
                    skippedLicense++;
                    continue;
                }
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

            var deps = await _nuget.ResolveAllDependenciesAsync(id, version, selectedTfm, skipLicenseCheck: isExtra);
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
        // Skip packages whose main DLL shares a name with a system library in core_root
        var systemDlls = new HashSet<string>(
            Directory.GetFiles("artifacts-main", "*.dll").Select(Path.GetFileName)!,
            StringComparer.OrdinalIgnoreCase);

        int skippedSystem = 0;
        approvedPackages.RemoveAll(p =>
        {
            if (systemDlls.Contains(p.Dll))
            {
                skippedSystem++;
                return true;
            }
            return false;
        });

        if (skippedSystem > 0)
        {
            await LogAsync($"Skipped {skippedSystem} packages with DLLs already in core_root");
        }

        int included = 0, skippedJitDiff = 0;
        string diffOutputDir = "nuget-diff-temp";
        Directory.CreateDirectory(diffOutputDir);

        int memoryParallelism = OnRamDisk ? GetRemainingSystemMemoryGB() / 3 : GetRemainingSystemMemoryGB() * 2;
        int parallelism = Math.Min(Math.Min(Environment.ProcessorCount, memoryParallelism), approvedPackages.Count);
        parallelism = Math.Max(parallelism, 1);
        var packageQueue = new Queue<(string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps)>(
            approvedPackages.OrderByDescending(p => new FileInfo(Path.Combine(p.PkgDir, p.Dll)).Length));

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
                                for (int i = 0; i < 3; i++)
                                {
                                    await JitDiffUtils.RunJitDiffOnAssembliesAsync(this, coreRoot, checkedClr, pkgDiffDir, [dllPath], logPrefix: pkg.Id);
                                }
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

                            // Write version.txt with package and dependency versions
                            var versionLines = new StringBuilder();
                            versionLines.AppendLine($"{pkg.Id} {pkg.Version}");
                            foreach (var (depId, depVersion) in pkg.Deps.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
                            {
                                versionLines.AppendLine($"{depId} {depVersion}");
                            }
                            File.WriteAllText(Path.Combine(outputPkgDir, "version.txt"), versionLines.ToString());

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
