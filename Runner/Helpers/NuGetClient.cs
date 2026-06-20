using System.Reflection.PortableExecutable;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Hybrid;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Licenses;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace Runner.Helpers;

internal sealed class NuGetClient
{
    private const string NuGetFeedUrl = "https://api.nuget.org/v3/index.json";
    private const int MaxNupkgSizeBytes = 50 * 1024 * 1024;

    private static readonly HashSet<string> s_permissiveLicenses = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIT", "Apache-1.1", "Apache-2.0", "BSD-1-Clause", "BSD-2-Clause", "BSD-3-Clause", "BSD-3-Clause-Clear",
        "ISC", "MS-PL", "Unlicense", "0BSD", "CC0-1.0", "Zlib", "NCSA",
        "BSL-1.0", "PostgreSQL", "X11", "MIT-0", "WTFPL", "MulanPSL-2.0",
        "mit-license", "bsd-license", "bsd", "apache2.0", "apache-license-2.0",
    };

    private static readonly string[] s_nonPermissiveMarkers =
    [
        "general public license",        // GPL / LGPL / AGPL
        "mozilla public license",        // MPL
        "eclipse public license",        // EPL
        "common public license",         // CPL
        "common development and distribution license", // CDDL
        "microsoft reciprocal license",  // MS-RL
        "server side public license",    // SSPL
        "business source license",       // BUSL
        "polyform",                      // PolyForm Noncommercial/etc.
        "commons clause",
        "all advertising materials",     // BSD-4-Clause / OpenSSL advertising clause
        "noncommercial",                 // CC *-NC, PolyForm Noncommercial (one word; permissive
                                         // licenses use the hyphenated "non-commercial" to mean "any use")
        "shall be used for good, not evil", // JSON license
    ];

    private readonly HttpClient _httpClient;
    private readonly HybridCache _cache;
    private readonly string _depCacheDir;
    private readonly Func<string, Task> _log;
    private readonly Func<string, bool> _isFrameworkProvidedPackage;

    // NuGet client resolution is delegated to the official NuGet.* libraries so that dependency and
    // version resolution behaves exactly like a real restore (range-aware, lowest-applicable,
    // conflict-resolved, framework-aware) instead of being approximated by hand.
    private readonly NuGetFramework _targetFramework;
    private readonly SourceRepository _repository;
    private readonly SourceCacheContext _sourceCache;
    private readonly ILogger _nugetLogger;

    public NuGetClient(HttpClient httpClient, HybridCache cache, string depCacheDir, Func<string, Task> log, string targetFramework,
        Func<string, bool> isFrameworkProvidedPackage)
    {
        _httpClient = httpClient;
        _cache = cache;
        _depCacheDir = depCacheDir;
        _log = log;
        _isFrameworkProvidedPackage = isFrameworkProvidedPackage;
        _targetFramework = NuGetFramework.Parse(targetFramework);
        _repository = Repository.Factory.GetCoreV3(NuGetFeedUrl);
        _sourceCache = new SourceCacheContext();
        _nugetLogger = new NuGetLoggerAdapter(log);
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

    /// <summary>
    /// Determines whether a package version is permissively licensed. Recognizes SPDX
    /// <c>&lt;license&gt;</c> expressions, embedded license files, and (legacy) <c>licenseUrl</c>s, and
    /// falls back to fetching and matching the text of unrecognized license files/URLs against the
    /// wording of common permissive licenses.
    /// </summary>
    public async Task<bool> IsPermissivelyLicensedAsync(string id, string version)
    {
        // HybridCache has a built-in serializer for string but not bool, so cache a "1"/"0" sentinel.
        string result = await _cache.GetOrCreateAsync(
            $"permissive:{id.ToLowerInvariant()}:{version.ToLowerInvariant()}",
            async ct =>
            {
                bool? permissive = await EvaluateLicenseAsync(id, version, ct);

                // Old versions often predate the SPDX <license> element or declare no license at all.
                // Newer versions usually declare it properly, so fall back to the latest version.
                if (permissive is null)
                {
                    string? latest = await ResolveLatestVersionAsync(id);
                    if (latest is not null && !latest.Equals(version, StringComparison.OrdinalIgnoreCase))
                    {
                        permissive = await EvaluateLicenseAsync(id, latest, ct);
                    }
                }

                return (permissive ?? false) ? "1" : "0";
            });
        return result == "1";

        async Task<bool?> EvaluateLicenseAsync(string id, string version, CancellationToken ct)
        {
            LicenseMetadata? license;
            string? licenseUrl;
            try
            {
                var metadataResource = await _repository.GetResourceAsync<PackageMetadataResource>(ct);
                var metadata = await metadataResource.GetMetadataAsync(
                    new PackageIdentity(id, NuGetVersion.Parse(version)), _sourceCache, _nugetLogger, ct);
                license = metadata?.LicenseMetadata;
                licenseUrl = metadata?.LicenseUrl?.ToString();
            }
            catch (Exception ex)
            {
                await _log($"[NuGetClient] Failed to fetch license metadata for {id} {version}: {ex.Message}");
                return null;
            }

            // SPDX expression.
            if (license is { Type: LicenseType.Expression })
                return IsPermissiveExpression(license.License);

            // License file embedded in the package - match its text against known license formats.
            if (license is { Type: LicenseType.File })
                return await IsPermissiveLicenseFileAsync(id, version, license.License, ct);

            // licenseUrl: map well-known URLs, otherwise fetch the content and match it.
            if (!string.IsNullOrEmpty(licenseUrl))
            {
                string? mapped = MapLicenseUrlToExpression(licenseUrl);
                if (mapped is not null)
                    return IsPermissiveExpression(mapped);

                string? text = await TryFetchTextAsync(licenseUrl, ct);
                return text is null ? null : IsPermissiveExpression(DetectLicenseId(text));
            }

            return null;
        }

        async Task<bool?> IsPermissiveLicenseFileAsync(string id, string version, string licenseFilePath, CancellationToken ct)
        {
            try
            {
                byte[]? nupkg = await DownloadNupkgAsync(id, version);
                if (nupkg is null)
                    return null;

                using var reader = new PackageArchiveReader(new MemoryStream(nupkg));
                string normalized = licenseFilePath.Replace('\\', '/');
                string? entry = reader.GetFiles().FirstOrDefault(f => f.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                    return null;

                using var stream = reader.GetStream(entry);
                using var streamReader = new StreamReader(stream);
                string text = await streamReader.ReadToEndAsync(ct);
                return IsPermissiveExpression(DetectLicenseId(text));
            }
            catch (Exception ex)
            {
                await _log($"[NuGetClient] Failed to read license file for {id} {version}: {ex.Message}");
                return null;
            }
        }

        async Task<string?> TryFetchTextAsync(string url, CancellationToken ct)
        {
            return await _cache.GetOrCreateAsync(
                $"license-text:{url.ToLowerInvariant()}",
                async ct =>
                {
                    using var client = new HttpClient();
                    client.DefaultRequestVersion = HttpVersion.Version20;
                    client.Timeout = TimeSpan.FromSeconds(20);
                    client.MaxResponseContentBufferSize = 1024 * 1024; // 1 MB

                    try
                    {
                        return await client.GetStringAsync(url, ct);
                    }
                    catch (Exception ex)
                    {
                        await _log($"[NuGetClient] Failed to fetch license URL {url}: {ex.Message}");
                        return null;
                    }
                },
                cancellationToken: ct);
        }

        static string? MapLicenseUrlToExpression(string? licenseUrl)
        {
            if (string.IsNullOrEmpty(licenseUrl))
                return null;

            const string NuGetLicensePrefix = "https://licenses.nuget.org/";
            if (licenseUrl.StartsWith(NuGetLicensePrefix, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(licenseUrl[NuGetLicensePrefix.Length..]);

            // dotnet org repositories (corefx/coreclr/runtime/...) are MIT-licensed.
            if (licenseUrl.Contains("github.com/dotnet/", StringComparison.OrdinalIgnoreCase) ||
                licenseUrl.Contains("raw.githubusercontent.com/dotnet/", StringComparison.OrdinalIgnoreCase))
                return "MIT";

            if (licenseUrl.Contains("apache.org/licenses/LICENSE-2.0", StringComparison.OrdinalIgnoreCase))
                return "Apache-2.0";

            // opensource.org/licenses/<slug>, where slug may carry a file extension (e.g. mit-license.php).
            const string OpenSourcePrefix = "opensource.org/licenses/";
            int index = licenseUrl.IndexOf(OpenSourcePrefix, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                string slug = licenseUrl[(index + OpenSourcePrefix.Length)..];
                int end = slug.IndexOfAny(['/', '?', '#']);
                if (end >= 0)
                    slug = slug[..end];

                int dot = slug.LastIndexOf('.');
                if (dot > 0 && slug[(dot + 1)..].ToLowerInvariant() is "php" or "html" or "htm" or "txt")
                    slug = slug[..dot];

                return NormalizeLegacyLicenseSlug(slug);
            }

            return null;

            static string? NormalizeLegacyLicenseSlug(string slug)
            {
                if (slug.Length == 0)
                    return null;

                return slug.ToLowerInvariant() switch
                {
                    "mit-license" or "mit" => "MIT",
                    "ms-pl" => "MS-PL",
                    "bsd-license" or "bsd" or "bsd-3-clause" => "BSD-3-Clause",
                    "bsd-2-clause" => "BSD-2-Clause",
                    "apache-2.0" or "apache2.0" or "apache-license-2.0" => "Apache-2.0",
                    _ => slug, // assume already an SPDX identifier
                };
            }
        }

        static string? DetectLicenseId(string text)
        {
            text = Regex.Replace(text, @"\s+", " ").Trim().Replace('\u201C', '"').Replace('\u201D', '"');

            foreach (string marker in s_nonPermissiveMarkers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            // MIT / X11 / Expat: permission grant + canonical rights + the "as is" warranty disclaimer.
            if (text.Contains("permission is hereby granted, free of charge", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("to deal in the software without restriction", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("the software is provided \"as is\"", StringComparison.OrdinalIgnoreCase))
                return "MIT";

            // BSD 2-/3-clause: redistribution grant + warranty disclaimer (advertising clause excluded above).
            if (text.Contains("redistribution and use in source and binary forms", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("this software is provided", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("as is", StringComparison.OrdinalIgnoreCase))
                return text.Contains("neither the name", StringComparison.OrdinalIgnoreCase) ? "BSD-3-Clause" : "BSD-2-Clause";

            // Apache 2.0: the actual license body, not merely a mention of it.
            if (text.Contains("apache license", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("version 2.0", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("terms and conditions for use, reproduction, and distribution", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("you may not use this file except in compliance with the license", StringComparison.OrdinalIgnoreCase)))
                return "Apache-2.0";

            // ISC: distinctive permission grant + warranty disclaimer.
            if (text.Contains("permission to use, copy, modify, and/or distribute this software for any purpose", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("the software is provided \"as is\"", StringComparison.OrdinalIgnoreCase))
                return "ISC";

            if (text.Contains("boost software license", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("permission is hereby granted", StringComparison.OrdinalIgnoreCase))
                return "BSL-1.0";

            if (text.Contains("microsoft public license", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("(ms-pl)", StringComparison.OrdinalIgnoreCase))
                return "MS-PL";

            // The opening sentence is globally unique to the Unlicense; no other license uses it.
            if (text.Contains("this is free and unencumbered software released into the public domain", StringComparison.OrdinalIgnoreCase))
                return "Unlicense";

            if (text.Contains("this software is provided 'as-is'", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("altered source versions must be plainly marked", StringComparison.OrdinalIgnoreCase))
                return "Zlib";

            if (text.Contains("mulan permissive software license", StringComparison.OrdinalIgnoreCase))
                return "MulanPSL-2.0";

            if (text.Contains("do what the f", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("you want to public license", StringComparison.OrdinalIgnoreCase))
                return "WTFPL";

            return null;
        }
    }

    private static bool IsPermissiveExpression(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        try
        {
            return IsPermissive(NuGetLicenseExpression.Parse(expression));
        }
        catch (NuGetLicenseExpressionParsingException)
        {
            return false;
        }

        static bool IsPermissive(NuGetLicenseExpression expression)
        {
            if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
                return false;

            return expression switch
            {
                // A leaf license is permissive if its SPDX identifier is in the allow-list.
                NuGetLicense license => s_permissiveLicenses.Contains(license.Identifier),
                // "license WITH exception" only grants additional rights, so judge by the base license.
                WithOperator withOperator => IsPermissive(withOperator.License),
                // "A AND B" requires complying with both; "A OR B" lets the consumer pick either.
                LogicalOperator { LogicalOperatorType: LogicalOperatorType.And } and => IsPermissive(and.Left) && IsPermissive(and.Right),
                LogicalOperator { LogicalOperatorType: LogicalOperatorType.Or } or => IsPermissive(or.Left) || IsPermissive(or.Right),
                _ => false,
            };
        }
    }

    // === Version resolution ===

    public async Task<string?> ResolveLatestVersionAsync(string id)
    {
        string result = await _cache.GetOrCreateAsync(
            $"latest:{id.ToLowerInvariant()}",
            async ct => await FetchAsync(ct) ?? "");
        return result.Length == 0 ? null : result;

        async Task<string?> FetchAsync(CancellationToken ct)
        {
            try
            {
                var byIdResource = await _repository.GetResourceAsync<FindPackageByIdResource>(ct);
                var versions = await byIdResource.GetAllVersionsAsync(id, _sourceCache, _nugetLogger, ct);
                NuGetVersion? latest = versions.Where(v => v is not null).Max();
                return latest?.ToNormalizedString();
            }
            catch (Exception ex)
            {
                await _log($"[NuGetClient] Failed to resolve latest version for {id}: {ex.Message}");
                return null;
            }
        }
    }

    // === Dependency resolution ===

    /// <summary>
    /// Resolves all transitive dependencies of a package using NuGet's own resolver (the same logic a
    /// restore uses): each dependency's declared version range is honoured and the lowest applicable
    /// version is selected, with conflict resolution across the whole graph.
    /// Returns <c>null</c> if any resolved dependency has a non-permissive license.
    /// </summary>
    public async Task<Dictionary<string, string>?> ResolveAllDependenciesAsync(string rootId, string rootVersion, bool skipLicenseCheck = false)
    {
        List<SourcePackageDependencyInfo> resolvedPackages;

        try
        {
            var depInfoResource = await _repository.GetResourceAsync<DependencyInfoResource>();
            var rootIdentity = new PackageIdentity(rootId, NuGetVersion.Parse(rootVersion));

            var availablePackages = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };

            // Pin the root to the requested version, then gather every candidate version of each
            // transitive dependency so the resolver can satisfy all constraints.
            var rootInfo = await depInfoResource.ResolvePackage(rootIdentity, _targetFramework, _sourceCache, _nugetLogger, CancellationToken.None);
            if (rootInfo is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            availablePackages.Add(rootInfo);

            var queue = new Queue<string>(rootInfo.Dependencies.Select(d => d.Id));
            while (queue.Count > 0)
            {
                string depId = queue.Dequeue();
                if (IsExcludedDependency(depId) || !visited.Add(depId))
                    continue;

                var infos = await depInfoResource.ResolvePackages(depId, _targetFramework, _sourceCache, _nugetLogger, CancellationToken.None);
                foreach (var info in infos)
                {
                    availablePackages.Add(info);
                    foreach (var dep in info.Dependencies)
                        queue.Enqueue(dep.Id);
                }
            }

            var resolverContext = new PackageResolverContext(
                DependencyBehavior.Lowest,
                targetIds: [rootId],
                requiredPackageIds: [],
                packagesConfig: [],
                preferredVersions: [rootIdentity],
                availablePackages: availablePackages,
                packageSources: [_repository.PackageSource],
                log: _nugetLogger);

            resolvedPackages = new PackageResolver()
                .Resolve(resolverContext, CancellationToken.None)
                .Select(p => availablePackages.First(a => PackageIdentityComparer.Default.Equals(a, p)))
                .ToList();
        }
        catch (Exception ex)
        {
            await _log($"[NuGetClient] Failed to resolve dependencies for {rootId} {rootVersion}: {ex.Message}");
            // Proceed with just the main DLL; the diff will fail to load if a reference is missing.
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in resolvedPackages)
        {
            if (package.Id.Equals(rootId, StringComparison.OrdinalIgnoreCase) || IsExcludedDependency(package.Id))
                continue;

            string version = package.Version.ToNormalizedString();

            // Dependencies whose assembly is provided by the shared framework (in core_root) are dropped
            // before diffing, so they are never redistributed and their license is irrelevant. Skipping
            // them also avoids false negatives from old framework packages with legacy license metadata.
            bool frameworkProvided = _isFrameworkProvidedPackage(package.Id);

            if (!skipLicenseCheck && !frameworkProvided)
            {
                if (!await IsPermissivelyLicensedAsync(package.Id, version))
                    return null;
            }

            resolved[package.Id] = version;
        }

        return resolved;
    }

    // === Package download & DLL extraction ===

    public async Task<(string? Dll, string? Tfm)> DownloadAndExtractBestDllAsync(string id, string version, string extractDir)
    {
        try
        {
            byte[]? nupkg = await DownloadNupkgAsync(id, version);
            if (nupkg is null)
                return (null, null);

            using var reader = new PackageArchiveReader(new MemoryStream(nupkg));

            var group = SelectNearestLibGroup(reader);
            if (group is null)
                return (null, null);

            string expectedDll = $"{id}.dll";
            var dlls = group.Items
                .Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Where(i => !IsSatelliteOrLegacyAssembly(i))
                .ToList();

            // Prefer the DLL matching the package name, fall back if there's exactly one DLL in the group.
            string? entry = dlls.FirstOrDefault(i => Path.GetFileName(i).Equals(expectedDll, StringComparison.OrdinalIgnoreCase))
                ?? (dlls.Count == 1 ? dlls[0] : null);

            if (entry is null)
                return (null, group.TargetFramework.GetShortFolderName());

            string fileName = Path.GetFileName(entry);
            string destPath = Path.Combine(extractDir, fileName);
            ExtractEntry(reader, entry, destPath);

            if (!IsManagedAssembly(destPath))
            {
                File.Delete(destPath);
                return (null, group.TargetFramework.GetShortFolderName());
            }

            return (fileName, group.TargetFramework.GetShortFolderName());
        }
        catch (Exception ex)
        {
            await _log($"[NuGetClient] Failed to download/extract DLL for {id} {version}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Downloads a dependency package's assemblies to a shared cache directory (once), then copies its
    /// DLLs to the target directory. HybridCache prevents stampeding when multiple parallel workers
    /// need the same dependency.
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
                byte[]? nupkg = await DownloadNupkgAsync(id, version);
                if (nupkg is null)
                    return [];

                using var reader = new PackageArchiveReader(new MemoryStream(nupkg));

                var group = SelectNearestLibGroup(reader);
                if (group is null)
                    return [];

                var dlls = new List<string>();
                foreach (string entry in group.Items.Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    string fileName = Path.GetFileName(entry);
                    string destPath = Path.Combine(depCacheSubDir, fileName);
                    ExtractEntry(reader, entry, destPath);

                    if (IsManagedAssembly(destPath))
                    {
                        dlls.Add(fileName);
                    }
                    else
                    {
                        File.Delete(destPath);
                    }
                }

                return dlls;
            }
            catch (Exception ex)
            {
                await _log($"[NuGetClient] Failed to download dependency DLLs for {id} {version}: {ex.Message}");
                return [];
            }
        }
    }

    // === Helpers ===

    private async Task<byte[]?> DownloadNupkgAsync(string id, string version)
    {
        string lowerId = id.ToLowerInvariant();
        string lowerVersion = version.ToLowerInvariant();
        string url = $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg";

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxNupkgSizeBytes)
            return null;

        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        return bytes.Length > MaxNupkgSizeBytes ? null : bytes;
    }

    private FrameworkSpecificGroup? SelectNearestLibGroup(PackageArchiveReader reader)
    {
        var group = NuGetFrameworkUtility.GetNearest(reader.GetLibItems(), _targetFramework, g => g.TargetFramework);
        return group is null || group.TargetFramework.IsUnsupported ? null : group;
    }

    private static void ExtractEntry(PackageArchiveReader reader, string entry, string destPath)
    {
        using var entryStream = reader.GetStream(entry);
        using var fs = File.Create(destPath);
        entryStream.CopyTo(fs);
    }

    private static bool IsSatelliteOrLegacyAssembly(string path)
    {
        ReadOnlySpan<char> fileName = Path.GetFileName(path.AsSpan());
        return fileName.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".legacy.dll", StringComparison.OrdinalIgnoreCase);
    }

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

    private static bool IsExcludedDependency(string id)
    {
        return id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("Microsoft.NETCore.Platforms", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("Microsoft.NETCore.Targets", StringComparison.OrdinalIgnoreCase) ||
               id.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class NuGetLoggerAdapter(Func<string, Task> log) : LoggerBase
    {
        public override void Log(ILogMessage message)
        {
            if (ShouldForward(message.Level))
            {
                log(Format(message)).GetAwaiter().GetResult();
            }
        }

        public override Task LogAsync(ILogMessage message) =>
            ShouldForward(message.Level) ? log(Format(message)) : Task.CompletedTask;

        private static bool ShouldForward(LogLevel level) => level >= LogLevel.Warning;

        private static string Format(ILogMessage message) => $"[NuGet] {message.Level}: {message.Message}";
    }

}
