using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Hybrid;
using NuGet.Versioning;

namespace Runner.Helpers;

internal sealed class NuGetClient
{
    private const int MaxNupkgSizeBytes = 50 * 1024 * 1024;

    private static readonly HashSet<string> s_permissiveLicenses = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC",
        "MS-PL", "Unlicense", "0BSD", "CC0-1.0", "Zlib",
        "BSL-1.0", "PostgreSQL", "X11", "MIT-0", "WTFPL", "MulanPSL-2.0",
    };

    private readonly HttpClient _httpClient;
    private readonly HybridCache _cache;
    private readonly string _depCacheDir;
    private readonly Func<string, Task> _log;

    public NuGetClient(HttpClient httpClient, HybridCache cache, string depCacheDir, Func<string, Task> log)
    {
        _httpClient = httpClient;
        _cache = cache;
        _depCacheDir = depCacheDir;
        _log = log;
    }

    // === DTOs ===

    private sealed class NuGetSearchResponse
    {
        [JsonPropertyName("data")]
        public NuGetSearchResult[] Data { get; set; } = [];
    }

    private sealed class NuGetSearchResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }

    private sealed class NuGetVersionIndex
    {
        [JsonPropertyName("versions")]
        public string[] Versions { get; set; } = [];
    }

    public sealed record PackageDependency(string Id, string VersionRange);

    // === Search ===

    public async Task<List<(string Id, string Version)>> SearchTopPackagesAsync(int count)
    {
        var results = new List<(string Id, string Version)>();
        int skip = 0;
        const int pageSize = 100;

        while (results.Count < count)
        {
            int take = Math.Min(pageSize, count - results.Count);
            string url = $"https://azuresearch-usnc.nuget.org/query?q=&skip={skip}&take={take}&prerelease=true&semVerLevel=2.0.0";

            using var stream = await _httpClient.GetStreamAsync(url);
            var response = await JsonSerializer.DeserializeAsync<NuGetSearchResponse>(stream);

            if (response?.Data is not { Length: > 0 })
                break;

            foreach (var item in response.Data)
            {
                if (!string.IsNullOrEmpty(item.Id) && !string.IsNullOrEmpty(item.Version))
                {
                    results.Add((item.Id, item.Version));
                }
            }

            if (response.Data.Length < take)
                break;

            skip += take;
        }

        return results;
    }

    // === License ===

    public async Task<string?> GetLicenseExpressionAsync(string id, string version)
    {
        string result = await _cache.GetOrCreateAsync(
            $"license:{id.ToLowerInvariant()}:{version.ToLowerInvariant()}",
            async ct => await FetchAsync() ?? "");
        return result.Length == 0 ? null : result;

        async Task<string?> FetchAsync()
        {
            try
            {
                string url = $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/{version.ToLowerInvariant()}/{id.ToLowerInvariant()}.nuspec";
                string nuspecXml = await _httpClient.GetStringAsync(url);

                var doc = XDocument.Parse(nuspecXml);
                XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var metadata = doc.Root?.Element(ns + "metadata");

                var licenseElement = metadata?.Element(ns + "license");
                if (licenseElement?.Attribute("type")?.Value?.Equals("expression", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return licenseElement.Value;
                }

                // Fall back to licenseUrl for licenses.nuget.org URLs
                string? licenseUrl = metadata?.Element(ns + "licenseUrl")?.Value;
                if (licenseUrl is not null && licenseUrl.StartsWith("https://licenses.nuget.org/", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(licenseUrl["https://licenses.nuget.org/".Length..]);
                }
            }
            catch (Exception ex)
            {
                await _log($"[NuGetClient] Failed to fetch license for {id} {version}: {ex.Message}");
            }

            return null;
        }
    }

    public static bool IsPermissiveLicense(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        // Strip exception clauses: "Apache-2.0 WITH LLVM-exception" -> "Apache-2.0"
        expression = Regex.Replace(expression, @"\bWITH\s+\S+", "", RegexOptions.IgnoreCase);

        // Extract license identifiers, skipping operators and parens
        var licenses = Regex.Split(expression, @"[\s()]+")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => !t.Equals("OR", StringComparison.OrdinalIgnoreCase))
            .Where(t => !t.Equals("AND", StringComparison.OrdinalIgnoreCase))
            .Select(t => t.TrimEnd('+'))
            .ToList();

        return licenses.Count > 0 && licenses.All(l => s_permissiveLicenses.Contains(l));
    }

    // === Package Download & DLL Extraction ===

    public async Task<(string? Dll, string? Tfm)> DownloadAndExtractBestDllAsync(string id, string version, string extractDir)
    {
        try
        {
            string lowerId = id.ToLowerInvariant();
            string lowerVersion = version.ToLowerInvariant();
            string url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxNupkgSizeBytes)
                return (null, null);

            string nupkgPath = Path.Combine(extractDir, $"{id}.nupkg");
            using (var fs = File.Create(nupkgPath))
            {
                await response.Content.CopyToAsync(fs);
            }

            if (new FileInfo(nupkgPath).Length > MaxNupkgSizeBytes)
            {
                File.Delete(nupkgPath);
                return (null, null);
            }

            string? dll = null;
            string? selectedTfm = null;

            using (var archive = ZipFile.OpenRead(nupkgPath))
            {
                string expectedDll = $"{id}.dll";

                var allDllEntries = archive.Entries
                    .Select(e => (Entry: e, Parts: e.FullName.Split('/')))
                    .Where(x => x.Parts.Length == 3 &&
                                x.Parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase) &&
                                x.Parts[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var tfmGroups = allDllEntries
                    .GroupBy(x => x.Parts[1])
                    .Select(g =>
                    {
                        // Prefer the DLL matching the package name, fall back if there's exactly one DLL in the TFM
                        var match = g.FirstOrDefault(x => x.Parts[2].Equals(expectedDll, StringComparison.OrdinalIgnoreCase));
                        if (match.Entry is null && g.Count() == 1)
                            match = g.First();
                        return (Tfm: g.Key, Entry: match);
                    })
                    .Where(x => x.Entry.Entry is not null)
                    .OrderByDescending(x => GetTfmPriority(x.Tfm))
                    .ToList();

                if (tfmGroups.Count == 0 || GetTfmPriority(tfmGroups[0].Tfm) < 0)
                {
                    File.Delete(nupkgPath);
                    return (null, null);
                }

                selectedTfm = tfmGroups[0].Tfm;
                var entry = tfmGroups[0].Entry;

                string destPath = Path.Combine(extractDir, entry.Parts[2]);
                entry.Entry.ExtractToFile(destPath, overwrite: true);

                if (IsManagedAssembly(destPath))
                {
                    dll = entry.Parts[2];
                }
                else
                {
                    File.Delete(destPath);
                }
            }

            File.Delete(nupkgPath);
            return (dll, selectedTfm);
        }
        catch (Exception ex)
        {
            await _log($"[NuGetClient] Failed to download/extract DLL for {id} {version}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Downloads a dependency package to a shared cache directory (once), then copies its DLLs to the target directory.
    /// HybridCache prevents stampeding when multiple parallel workers need the same dependency.
    /// </summary>
    public async Task DownloadDependencyDllsAsync(string id, string version, string targetDir)
    {
        string lowerId = id.ToLowerInvariant();
        string lowerVersion = version.ToLowerInvariant();
        string depCacheSubDir = Path.Combine(_depCacheDir, $"{lowerId}-{lowerVersion}");

        var dlls = await _cache.GetOrCreateAsync(
            $"dep-dlls:{lowerId}:{lowerVersion}",
            async ct =>
            {
                Directory.CreateDirectory(depCacheSubDir);
                return await FetchAsync();
            });

        foreach (string dll in dlls)
        {
            string src = Path.Combine(depCacheSubDir, dll);
            string dest = Path.Combine(targetDir, dll);
            if (File.Exists(src) && !File.Exists(dest))
            {
                File.Copy(src, dest);
            }
        }

        async Task<List<string>> FetchAsync()
        {
            try
            {
                string url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength > MaxNupkgSizeBytes)
                    return [];

                string nupkgPath = Path.Combine(depCacheSubDir, $"{id}.nupkg");
                using (var fs = File.Create(nupkgPath))
                {
                    await response.Content.CopyToAsync(fs);
                }

                if (new FileInfo(nupkgPath).Length > MaxNupkgSizeBytes)
                {
                    File.Delete(nupkgPath);
                    return [];
                }

                var dlls = new List<string>();

                using (var archive = ZipFile.OpenRead(nupkgPath))
                {
                    var tfmGroups = archive.Entries
                        .Select(e => (Entry: e, Parts: e.FullName.Split('/')))
                        .Where(x => x.Parts.Length == 3 &&
                                    x.Parts[0].Equals("lib", StringComparison.OrdinalIgnoreCase) &&
                                    x.Parts[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                                    !string.IsNullOrEmpty(x.Parts[2]))
                        .GroupBy(x => x.Parts[1])
                        .OrderByDescending(g => GetTfmPriority(g.Key))
                        .ToList();

                    if (tfmGroups.Count > 0 && GetTfmPriority(tfmGroups[0].Key) >= 0)
                    {
                        foreach (var entry in tfmGroups[0])
                        {
                            string destPath = Path.Combine(depCacheSubDir, entry.Parts[2]);
                            entry.Entry.ExtractToFile(destPath, overwrite: true);

                            if (IsManagedAssembly(destPath))
                            {
                                dlls.Add(entry.Parts[2]);
                            }
                            else
                            {
                                File.Delete(destPath);
                            }
                        }
                    }
                }

                File.Delete(nupkgPath);
                return dlls;
            }
            catch (Exception ex)
            {
                await _log($"[NuGetClient] Failed to download dependency DLLs for {id} {version}: {ex.Message}");
                return [];
            }
        }
    }

    // === Dependency Resolution ===

    public async Task<List<PackageDependency>> GetPackageDependenciesAsync(string id, string version, string targetTfm)
    {
        return await _cache.GetOrCreateAsync(
            $"deps:{id.ToLowerInvariant()}:{version.ToLowerInvariant()}:{targetTfm}",
            async ct => await FetchAsync());

        async Task<List<PackageDependency>> FetchAsync()
        {
            try
            {
                string lowerId = id.ToLowerInvariant();
                string lowerVersion = version.ToLowerInvariant();
                string url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.nuspec";
                string nuspecXml = await _httpClient.GetStringAsync(url);

                var doc = XDocument.Parse(nuspecXml);
                XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var metadata = doc.Root?.Element(ns + "metadata");
                var dependencies = metadata?.Element(ns + "dependencies");

                if (dependencies is null) return [];

                var groups = dependencies.Elements(ns + "group").ToList();
                XElement? selectedGroup;

                if (groups.Count > 0)
                {
                    int targetPriority = GetTfmPriority(targetTfm);

                    // Find best matching group: highest priority that doesn't exceed target
                    selectedGroup = groups
                        .Select(g => (Element: g, Priority: GetTfmPriority(NormalizeTfm(g.Attribute("targetFramework")?.Value))))
                        .Where(g => g.Priority >= 0 && g.Priority <= targetPriority)
                        .OrderByDescending(g => g.Priority)
                        .Select(g => g.Element)
                        .FirstOrDefault();

                    // Fall back to group with no targetFramework
                    selectedGroup ??= groups.FirstOrDefault(g => g.Attribute("targetFramework") is null);

                    if (selectedGroup is null) return [];
                }
                else
                {
                    // Old format: flat list of dependencies (no groups)
                    selectedGroup = dependencies;
                }

                return selectedGroup.Elements(ns + "dependency")
                    .Select(d => new PackageDependency(
                        d.Attribute("id")?.Value ?? "",
                        d.Attribute("version")?.Value ?? ""
                    ))
                    .Where(d => !string.IsNullOrEmpty(d.Id))
                    .ToList();
            }
            catch (Exception ex)
            {
                await _log($"[NuGetClient] Failed to fetch dependencies for {id} {version}: {ex.Message}");
                return [];
            }
        }
    }

    public async Task<string?> ResolveLatestVersionAsync(string id)
    {
        string[] versions = await GetAllVersionsAsync(id);
        return versions.Length > 0 ? versions[^1] : null;
    }

    /// <summary>
    /// Resolves the highest available version of <paramref name="id"/> that satisfies the given NuGet
    /// version range. Falls back to the latest version when the range is empty/unparseable or when no
    /// available version satisfies it, preserving the previous "latest wins" behavior in those cases.
    /// </summary>
    public async Task<string?> ResolveBestVersionInRangeAsync(string id, string? versionRange)
    {
        string[] versions = await GetAllVersionsAsync(id);
        if (versions.Length == 0)
            return null;

        if (string.IsNullOrWhiteSpace(versionRange) || !VersionRange.TryParse(versionRange, out VersionRange? range))
            return versions[^1];

        string? bestVersion = null;
        NuGetVersion? best = null;

        foreach (string v in versions)
        {
            if (NuGetVersion.TryParse(v, out NuGetVersion? parsed) &&
                range.Satisfies(parsed) &&
                (best is null || parsed > best))
            {
                best = parsed;
                bestVersion = v;
            }
        }

        return bestVersion ?? versions[^1];
    }

    private async Task<string[]> GetAllVersionsAsync(string id)
    {
        return await _cache.GetOrCreateAsync(
            $"versions:{id.ToLowerInvariant()}",
            async ct => await FetchAsync());

        async Task<string[]> FetchAsync()
        {
            try
            {
                string url = $"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json";
                using var stream = await _httpClient.GetStreamAsync(url);
                var index = await JsonSerializer.DeserializeAsync<NuGetVersionIndex>(stream);

                return index?.Versions ?? [];
            }
            catch (Exception ex)
            {
                await _log($"[NuGetClient] Failed to list versions for {id}: {ex.Message}");
                return [];
            }
        }
    }

    /// <summary>
    /// Resolves all transitive dependencies of a package for the given TFM.
    /// Returns null if any dependency has a non-permissive license.
    /// </summary>
    public async Task<Dictionary<string, string>?> ResolveAllDependenciesAsync(string rootId, string rootVersion, string targetTfm, bool skipLicenseCheck = false)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };
        var queue = new Queue<PackageDependency>();

        foreach (var dep in await GetPackageDependenciesAsync(rootId, rootVersion, targetTfm))
            queue.Enqueue(dep);

        while (queue.Count > 0)
        {
            var (depId, depRange) = queue.Dequeue();
            if (string.IsNullOrEmpty(depId) || !visited.Add(depId) || IsExcludedDependency(depId))
                continue;

            string? version = await ResolveBestVersionInRangeAsync(depId, depRange);
            if (version is null) continue;

            if (!skipLicenseCheck)
            {
                string? license = await GetLicenseExpressionAsync(depId, version);
                if (!IsPermissiveLicense(license))
                    return null;
            }

            resolved[depId] = version;

            foreach (var td in await GetPackageDependenciesAsync(depId, version, targetTfm))
                queue.Enqueue(td);
        }

        return resolved;
    }

    // === Helpers ===

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            return pe.HasMetadata;
        }
        catch
        {
            return false;
        }
    }

    private static int GetTfmPriority(string tfm)
    {
        // net9.0, net8.0, etc. -> 1000+X
        var netMatch = Regex.Match(tfm, @"^net(\d+)\.0$", RegexOptions.IgnoreCase);
        if (netMatch.Success)
            return 1000 + int.Parse(netMatch.Groups[1].Value);

        // netcoreapp3.1, etc. -> 500+X*10+Y
        var coreMatch = Regex.Match(tfm, @"^netcoreapp(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (coreMatch.Success)
            return 500 + int.Parse(coreMatch.Groups[1].Value) * 10 + int.Parse(coreMatch.Groups[2].Value);

        // netstandard2.1, etc. -> 100+X*10+Y
        var stdMatch = Regex.Match(tfm, @"^netstandard(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (stdMatch.Success)
            return 100 + int.Parse(stdMatch.Groups[1].Value) * 10 + int.Parse(stdMatch.Groups[2].Value);

        return -1; // .NET Framework, other unknown TFMs
    }

    private static string NormalizeTfm(string? tfm)
    {
        if (string.IsNullOrEmpty(tfm)) return "";

        string lower = tfm.ToLowerInvariant();
        if (GetTfmPriority(lower) >= 0) return lower;

        // .NETStandard,Version=v2.0 or .NETStandard2.0
        var match = Regex.Match(tfm, @"NETStandard[,.]?\s*(?:Version\s*=\s*v)?(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        if (match.Success) return $"netstandard{match.Groups[1].Value}.{match.Groups[2].Value}";

        // .NETCoreApp,Version=v3.1 or .NETCoreApp3.1
        match = Regex.Match(tfm, @"NETCoreApp[,.]?\s*(?:Version\s*=\s*v)?(\d+)\.(\d+)", RegexOptions.IgnoreCase);
        if (match.Success) return $"netcoreapp{match.Groups[1].Value}.{match.Groups[2].Value}";

        return lower;
    }

    private static bool IsExcludedDependency(string id)
    {
        return id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("Microsoft.NETCore.Platforms", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("Microsoft.NETCore.Targets", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase);
    }
}
