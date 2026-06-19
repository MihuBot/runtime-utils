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
            "dnlib", "AsmResolver", "BepuPhysics", "Avalonia", "Uno.UI",
            "MessagePack", "NodaTime",
            "Markdig", "Yarp.ReverseProxy",

            "Silk.NET.Core", "Silk.NET.Maths", "Silk.NET.OpenGL", "Silk.NET.Vulkan", "Silk.NET.OpenGLES", "Silk.NET.Vulkan.Extensions.KHR",
            "Silk.NET.GLFW", "Silk.NET.Windowing.Common", "Silk.NET.Vulkan.Extensions.EXT", "Silk.NET.Input.Common", "Silk.NET.Windowing.Glfw",
            "Silk.NET.SDL", "Silk.NET.OpenAL", "Silk.NET.Input.Glfw", "Silk.NET.Windowing.Sdl", "Silk.NET.OpenGL.Legacy", "Silk.NET.Input.Sdl",
            "Silk.NET.OpenXR", "Silk.NET.Assimp", "Silk.NET.DXGI", "Silk.NET.OpenGLES.Extensions.EXT", "Silk.NET.Input.Extensions", "Silk.NET.OpenCL",
            "Silk.NET.Direct3D11", "Silk.NET.OpenGL.Extensions.ImGui", "Silk.NET.OpenXR.Extensions.FB", "Silk.NET.Direct3D.Compilers",
            "Silk.NET.OpenAL.Extensions.EXT", "Silk.NET.OpenAL.Extensions.Enumeration", "Silk.NET.OpenAL.Extensions.Creative", "Silk.NET.Direct3D12",
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
        var approvedPackages = await GatherNuGetInfoAsync(packageCount * 3); // 3x to compensate for packages we'll rule out later
        await runtimeTask;

        await ProcessApprovedPackagesAsync(approvedPackages, packageCount);

        await ZipAndUploadArtifactAsync(OutputDir, OutputDir, maxCompression: true);

        await CreateTopNArchiveAsync(approvedPackages, topN: 50);
    }

    private async Task BuildRuntimeAsync()
    {
        await JitDiffJob.CloneRuntimeAndSetupToolsAsync(this);
        await JitDiffJob.BuildAndCopyRuntimeBranchBitsAsync(this, "main", uploadArtifacts: false);
        await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);
        await RuntimeHelpers.CopyAspNetSharedFrameworkToCoreRootAsync(this, "artifacts-main");
    }

    private async Task<List<PackageInfo>> GatherNuGetInfoAsync(int count)
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

        var approvedPackages = new List<PackageInfo>();

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

            approvedPackages.Add(new PackageInfo(id, version, pkgDir, dll, deps));
            await LogAsync($"{prefix} - approved ({deps.Count} deps)");
        }

        await LogAsync($"Phase 1 complete: {approvedPackages.Count} approved, {skippedLicense} license, {skippedNoDlls} no DLLs");

        if (approvedPackages.Count == 0)
        {
            throw new Exception("No packages passed license and DLL checks.");
        }

        return approvedPackages;
    }

    private async Task ProcessApprovedPackagesAsync(List<PackageInfo> approvedPackages, int maxPackagesToInclude)
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

        // Deduplicate packages that share the same DLL name (e.g. MathNet.Numerics vs MathNet.Numerics.Signed).
        // The list is ordered by popularity, so the first occurrence wins.
        var seenDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedDuplicateDll = 0;
        approvedPackages.RemoveAll(p =>
        {
            if (!seenDlls.Add(p.Dll))
            {
                skippedDuplicateDll++;
                return true;
            }
            return false;
        });

        if (skippedDuplicateDll > 0)
        {
            await LogAsync($"Skipped {skippedDuplicateDll} packages with duplicate DLL names");
        }

        int skippedJitDiff = 0;
        string diffOutputDir = "nuget-diff-temp";
        Directory.CreateDirectory(diffOutputDir);

        int memoryParallelism = OnRamDisk ? GetRemainingSystemMemoryGB() / 3 : GetRemainingSystemMemoryGB() * 2;
        int parallelism = Math.Min(Math.Min(Environment.ProcessorCount, memoryParallelism), approvedPackages.Count);
        parallelism = Math.Max(parallelism, 1);
        var packageQueue = new Queue<PackageInfo>(
            approvedPackages.OrderByDescending(p => new FileInfo(Path.Combine(p.PkgDir, p.Dll)).Length));

        List<PackageInfo> includedPackages = [];

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
                        PackageInfo? pkg;
                        lock (packageQueue)
                        {
                            if (!packageQueue.TryDequeue(out pkg))
                                break;
                        }

                        LastProgressSummary = $"Processing NuGet packages. ~{packageQueue.Count} remaining, {includedPackages.Count} included.";

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

                            lock (includedPackages)
                            {
                                includedPackages.Add(pkg);
                            }

                            await LogAsync($"[NuGet] {pkg.Id} - included ({pkg.Deps.Count} deps)");
                        }
                        finally
                        {
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

        if (includedPackages.Count > maxPackagesToInclude)
        {
            List<PackageInfo> original = includedPackages;

            includedPackages = approvedPackages.Where(p => ExtraPackages.Contains(p.Id))
                .Concat(approvedPackages)
                .Select(p => includedPackages.FirstOrDefault(ip => ip.Id == p.Id))
                .Where(p => p is not null)
                .DistinctBy(p => p!.Id)
                .Take(maxPackagesToInclude)
                .ToList()!;

            await LogAsync($"[NuGet] Trimmed included packages from {original.Count} to {includedPackages.Count}. Removed: {string.Join(", ", original.Except(includedPackages).Select(p => p.Id))}");
        }

        await Parallel.ForEachAsync(includedPackages, async (pkg, _) =>
        {
            string outputPkgDir = Path.Combine(OutputDir, pkg.Id);
            Directory.CreateDirectory(outputPkgDir);

            foreach (string file in Directory.GetFiles(pkg.PkgDir, "*.dll"))
            {
                File.Copy(file, Path.Combine(outputPkgDir, Path.GetFileName(file)), overwrite: true);
            }

            // Write DiffAssemblies.txt so JitDiffJob only diffs the main DLL, not dependencies
            File.WriteAllText(Path.Combine(outputPkgDir, "DiffAssemblies.txt"), pkg.Dll);

            // Write version.txt with package and dependency versions
            File.WriteAllText(Path.Combine(outputPkgDir, "PackageInfo.json"), JsonSerializer.Serialize(pkg));
        });

        await WritePackageListAsync(OutputDir, includedPackages);

        await LogAsync($"Summary: {includedPackages.Count} included, {skippedJitDiff} jit-diff failed, {skippedDuplicateDll} duplicate DLL names");

        if (includedPackages.Count == 0)
        {
            throw new Exception("No packages were included in the archive.");
        }
    }

    private async Task CreateTopNArchiveAsync(List<PackageInfo> approvedPackages, int topN)
    {
        var includedIds = new HashSet<string>(
            Directory.GetDirectories(OutputDir).Select(Path.GetFileName)!,
            StringComparer.OrdinalIgnoreCase);

        string subsetDir = $"{OutputDir}-subset";
        Directory.CreateDirectory(subsetDir);

        List<PackageInfo> topNPackages = [];

        int nonExtraCount = 0;
        foreach (var pkg in approvedPackages)
        {
            if (!includedIds.Contains(pkg.Id))
                continue;

            bool isExtra = ExtraPackages.Contains(pkg.Id);
            if (!isExtra)
            {
                if (nonExtraCount >= topN)
                    continue;
                nonExtraCount++;
            }

            topNPackages.Add(pkg);
        }

        foreach (var pkg in topNPackages)
        {
            CopyDirectory(Path.Combine(OutputDir, pkg.Id), Path.Combine(subsetDir, pkg.Id));
        }

        await WritePackageListAsync(subsetDir, topNPackages);

        int totalCount = Directory.GetDirectories(subsetDir).Length;
        await LogAsync($"Subset archive: {totalCount} packages ({nonExtraCount} top + {totalCount - nonExtraCount} extra)");
        await ZipAndUploadArtifactAsync(subsetDir, subsetDir, maxCompression: true);
        DeleteDirectory(subsetDir);
    }

    private static async Task WritePackageListAsync(string directory, List<PackageInfo> packages)
    {
        await File.WriteAllTextAsync(
            Path.Combine(directory, "PackageList.json"),
            JsonSerializer.Serialize(packages));
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

    private record PackageInfo(string Id, string Version, string PkgDir, string Dll, Dictionary<string, string> Deps);
}
