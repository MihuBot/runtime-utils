namespace Runner.Jobs;

internal sealed class CoreRootGenerationJob : JobBase
{
    /// <summary>A safety rail only - <see cref="ShouldKeepDelta"/> normally decides when to stop.</summary>
    private const int MaxCommitsPerReference = 100;

    /// <summary>
    /// How far apart in <em>commit</em> time a reference and a delta may be before a fresh reference is
    /// created. Wall-clock time is deliberately not used: CoreRoots are not necessarily generated in commit
    /// order (a job may be started manually for older commits), and how well a delta compresses depends on
    /// how far apart the two commits are in history, not on when they happened to be built.
    /// </summary>
    private static readonly TimeSpan MaxReferenceCommitAge = TimeSpan.FromDays(3);

    private readonly HashSet<(string? Sha, string? Type)> _toSkip = [];
    private int _builtThisSession;

    /// <summary>
    /// Every delta is produced against <see cref="_referencePrefix"/>, which is swapped out once it is too
    /// far from the commit being built / used by too many commits. The prefix is the whole uncompressed
    /// reference tarball, so it is only ever held once - compression runs inline between builds rather than
    /// in the background, so that its allocations never overlap with a runtime build.
    /// </summary>
    private ReadOnlyMemory<byte> _referencePrefix;
    private string? _referenceBlobName;
    private DateTime _referenceCommitTime;
    private int _commitsUsingReference;

    /// <summary>The compressed size of the current reference archive - "R" in <see cref="ShouldKeepDelta"/>.</summary>
    private long _referenceSize;

    /// <summary>
    /// The total compressed size of the deltas produced against the current reference. Null if the reference
    /// was inherited from a previous session, which only records how many deltas point at it, not their sizes.
    /// </summary>
    private long? _deltaBytesUsingReference;

    /// <summary>
    /// A previously uploaded reference that this session may reuse. It is only downloaded once a commit
    /// that can actually use it comes up, so that a session building commits far away from it (or building
    /// nothing at all) doesn't pay for a large download it will throw away.
    /// </summary>
    private CoreRootAPI.CoreRootEntry? _candidateReference;
    private int _candidateReferenceCommits;

    public CoreRootGenerationJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamOrFastestDiskAsync();

        await CloneRuntimeAndSetupToolsAsync();

        string type = "release";

        CoreRootAPI.CoreRootEntry[] existingEntries = await CoreRootAPI.AllAsync(this, type);

        foreach (var entry in existingEntries)
        {
            _toSkip.Add((entry.Sha, entry.Type));
        }

        SelectCandidateReference(existingEntries);

        while (true)
        {
            int built = _builtThisSession;
            await BuildCoreRootsAsync(type);

            if (built == _builtThisSession)
            {
                break;
            }

            await RunProcessAsync("git", "checkout main", workDir: "runtime");
            await RunProcessAsync("git", "pull origin", workDir: "runtime");
        }
    }

    private async Task CloneRuntimeAndSetupToolsAsync()
    {
        Task cloneRuntimeTask = RuntimeHelpers.CloneRuntimeMainAsync(this);

        Task aptGetTask = RunProcessAsync("apt-get", "install -y zip wget ninja-build", logPrefix: "Install tools");

        await aptGetTask;
        await cloneRuntimeTask;
    }

    /// <summary>
    /// Picks the reference this session should reuse: the one closest to the tip of history that still has
    /// capacity left. Selection is by commit time rather than by when the CoreRoot was generated, because
    /// entries are not necessarily created in commit order.
    /// </summary>
    private void SelectCandidateReference(CoreRootAPI.CoreRootEntry[] entries)
    {
        // How many deltas each reference is already responsible for. Counting the entries that actually
        // point at it is exact and order-independent, unlike "everything created after it".
        Dictionary<string, int> deltasPerReference = entries
            .Where(e => !string.IsNullOrEmpty(e.PrefixBlobName))
            .GroupBy(e => e.PrefixBlobName!)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        (CoreRootAPI.CoreRootEntry Entry, int Commits)? best = entries
            .Where(e => string.IsNullOrEmpty(e.PrefixBlobName) && !string.IsNullOrEmpty(e.Url) && !string.IsNullOrEmpty(e.BlobName))
            .Select(e => (Entry: e, Commits: deltasPerReference.GetValueOrDefault(e.BlobName!)))
            .Where(e => e.Commits < MaxCommitsPerReference)
            .OrderByDescending(e => e.Entry.CommitTime)
            .Select(e => ((CoreRootAPI.CoreRootEntry, int)?)e)
            .FirstOrDefault();

        if (best is null)
        {
            return;
        }

        _candidateReference = best.Value.Entry;
        _candidateReferenceCommits = best.Value.Commits;
    }

    /// <summary>
    /// A reference may be used for a commit when the two commits are close enough together in history and
    /// the reference isn't already used by too many commits.
    /// </summary>
    private static bool CanUseReference(DateTime referenceCommitTime, int commitsUsingReference, DateTime commitTime) =>
        commitsUsingReference < MaxCommitsPerReference &&
        (commitTime - referenceCommitTime).Duration() < MaxReferenceCommitAge;

    /// <summary>Whether the reference currently held in memory can serve <paramref name="commitTime"/>.</summary>
    private bool CanUseLoadedReference(DateTime commitTime) =>
        !_referencePrefix.IsEmpty &&
        CanUseReference(_referenceCommitTime, _commitsUsingReference, commitTime);

    /// <summary>
    /// Downloads and decompresses <see cref="_candidateReference"/> so that it can be used as a prefix.
    /// </summary>
    private async Task LoadCandidateReferenceAsync(string logPrefix)
    {
        CoreRootAPI.CoreRootEntry candidate = _candidateReference!;

        // Only ever attempted once - on failure we fall back to creating a fresh reference.
        _candidateReference = null;

        try
        {
            await LogAsync($"[{logPrefix}] Downloading compression reference '{candidate.BlobName}' (commit time {candidate.CommitTime:u}, used by {_candidateReferenceCommits} commits) ...");

            using var archive = new TempFile("tar.zst");
            using var tar = new TempFile("tar");

            await DownloadToFileAsync(candidate.Url!, archive.Path);
            await CoreRootArchive.DecompressAsync(archive.Path, tar.Path, prefix: default, JobTimeout);

            _referencePrefix = await CoreRootArchive.ReadPrefixAsync(tar.Path, JobTimeout);
            _referenceBlobName = candidate.BlobName;
            _referenceCommitTime = candidate.CommitTime;
            _commitsUsingReference = _candidateReferenceCommits;
            _referenceSize = new FileInfo(archive.Path).Length;

            // Only the delta count survives across sessions, so ShouldKeepDelta has to estimate their sizes.
            _deltaBytesUsingReference = null;

            await LogAsync($"[{logPrefix}] Using '{candidate.BlobName}' as the compression reference.");
        }
        catch (Exception ex)
        {
            _referencePrefix = default;
            _referenceBlobName = null;
            await LogAsync($"[{logPrefix}] Failed to restore the compression reference: {ex}");
        }
    }

    private async Task DownloadToFileAsync(string url, string filePath)
    {
        await SendAsyncCore<object?>(HttpMethod.Get, url, content: null, async response =>
        {
            await using Stream source = await response.Content.ReadAsStreamAsync();
            await using var destination = File.Create(filePath);
            await source.CopyToAsync(destination);
            return null;
        });
    }

    private async Task BuildCoreRootsAsync(string type)
    {
        int lastNDays = int.Parse(GetArgument(nameof(lastNDays), "2"));

        List<(string Sha, DateTime CommitTime)> commits = await GitHelper.ListCommitsAsync(this, lastNDays, "runtime");
        commits.Reverse();

        await LogAsync($"Found {commits.Count} commits in the last {lastNDays} days");

        for (int i = 0; i < commits.Count; i++)
        {
            if (MaxRemainingTime.TotalHours < 1)
            {
                await LogAsync("Approaching job duration limit. Stopping ...");
                break;
            }

            string progressMessage = $"Processing commit {i + 1}/{commits.Count}. Built {_builtThisSession} in this session.";
            LastProgressSummary = progressMessage;
            await LogAsync(progressMessage);

            (string commit, DateTime commitTime) = commits[i];

            if (!_toSkip.Add((commit, type)))
            {
                await LogAsync($"[{commit}] Skipping build");
                continue;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();

            await RunProcessAsync("git", $"checkout {commit}", workDir: "runtime");

            List<string> changedFiles = await GitHelper.GetChangedFilesAsync(this, "HEAD~1", "runtime");

            if (CanSkipBuilding(changedFiles))
            {
                await LogAsync($"[{commit}] Skipping build (docs-only changes)");
                continue;
            }

            if (await CoreRootAPI.GetAsync(this, commit, type) is not null)
            {
                await LogAsync($"[{commit}] Skipping build");
                continue;
            }

            string logPrefix = $"{commit[..20]} {type}";

            if (!await TryBuildAsync(logPrefix, type))
            {
                await LogAsync($"[{logPrefix}] Build failed. Retrying ...");

                await Task.Delay(1_000);

                if (await RunProcessAsync("git", "clean -fdx", logPrefix: logPrefix, workDir: "runtime", checkExitCode: false) != 0)
                {
                    await Task.Delay(10_000);
                    await RunProcessAsync("git", "clean -fdx", logPrefix: logPrefix, workDir: "runtime", checkExitCode: false);
                }

                if (!await TryBuildAsync(logPrefix, type))
                {
                    await LogAsync($"[{logPrefix}] Build failed again. Skipping ...");
                    continue;
                }
            }

            string artifactsDir = await CopyArtifactsAsync(logPrefix, commit, type);
            _builtThisSession++;

            // Compression is done inline rather than in the background. It briefly needs a multiple of the
            // CoreRoot size (the reference prefix, plus ZStandard's window), and overlapping that with the
            // next runtime build is what pushes small runners into the OOM killer.
            try
            {
                await CompressUploadAndSaveAsync(logPrefix, commit, commitTime, type, artifactsDir);

                await LogAsync($"[{logPrefix}] Done in {FormatElapsedTime(stopwatch.Elapsed)}");
            }
            catch (Exception ex)
            {
                await LogAsync($"[{logPrefix}] Error: {ex}");
            }
        }

        static bool CanSkipBuilding(List<string> changedFiles)
        {
            foreach (string file in changedFiles)
            {
                if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (file is "LICENSE.TXT" or "PATENTS.TXT" or "THIRD-PARTY-NOTICES.TXT")
                {
                    continue;
                }    

                return false;
            }

            return true;
        }
    }

    private async Task UploadCoreRootAsync(string blobName, string filePath)
    {
        string containerSasUrl = Metadata["CoreRootSasUri"];
        int queryOffset = containerSasUrl.IndexOf('?');
        string url = $"{containerSasUrl.AsSpan(0, queryOffset)}/{blobName}{containerSasUrl.AsSpan(queryOffset)}";

        await using var fs = File.OpenRead(filePath);
        using var content = new StreamContent(fs);

        using var response = await HttpClient.PostAsync(url, content, JobTimeout);
        response.EnsureSuccessStatusCode();
    }

    private async Task<bool> TryBuildAsync(string logPrefix, string type)
    {
        await LogAsync($"[{logPrefix}] Building ...");

        try
        {
            string targets = $"clr+libs -rc {type} -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}";

            if (OperatingSystem.IsWindows())
            {
                await RunProcessAsync("build.cmd", targets, logPrefix: logPrefix, workDir: "runtime");
            }
            else
            {
                await RunProcessAsync("bash", $"build.sh {targets}", logPrefix: logPrefix, workDir: "runtime");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> CopyArtifactsAsync(string logPrefix, string commit, string type)
    {
        await LogAsync($"[{logPrefix}] Copying {type} artifacts ...");

        string artifactsDir = $"artifacts-{commit}-{type}";
        Directory.CreateDirectory(artifactsDir);

        await RuntimeHelpers.CopyReleaseArtifactsAsync(this, logPrefix, artifactsDir, runtimeConfig: type == "release" ? "Release" : "Checked");

        foreach (string file in Directory.EnumerateFiles(artifactsDir, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".mibc", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("SuperFileCheck/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("R2RTest/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("PDB/", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("PdbChecker/", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }

        return artifactsDir;
    }

    /// <summary>
    /// Tars the artifacts, compresses them with ZStandard, and publishes the result.
    /// <para>
    /// A CoreRoot is either a <em>reference</em> (compressed standalone) or a <em>delta</em> (compressed
    /// against a reference). References are never themselves compressed against another reference: prefix
    /// chains are forbidden, because a consumer only downloads a single prefix before decompressing.
    /// </para>
    /// </summary>
    private async Task CompressUploadAndSaveAsync(string logPrefix, string commit, DateTime commitTime, string type, string artifactsDir)
    {
        string tarPath = Path.GetFullPath($"{artifactsDir}.tar");
        string archivePath = Path.GetFullPath($"{artifactsDir}{CoreRootArchive.Extension}");
        string blobName = CoreRootArchive.GetBlobName(commit, type);

        await LogAsync($"[{logPrefix}] Creating {type} tarball ...");
        await CoreRootArchive.CreateTarAsync(artifactsDir, tarPath, JobTimeout);
        Directory.Delete(artifactsDir, recursive: true);

        try
        {
            // Prefer the reference already held in memory; otherwise fall back to the one left over from a
            // previous session, downloading it only now that a commit can actually use it.
            if (!CanUseLoadedReference(commitTime) &&
                _candidateReference is not null &&
                CanUseReference(_candidateReference.CommitTime, _candidateReferenceCommits, commitTime))
            {
                await LoadCandidateReferenceAsync(logPrefix);
            }

            if (CanUseLoadedReference(commitTime))
            {
                await LogAsync($"[{logPrefix}] Compressing {type} artifacts using '{_referenceBlobName}' as the prefix ...");

                await CoreRootArchive.CompressAsync(tarPath, archivePath, _referencePrefix, JobTimeout);
                await LogArchiveSizeAsync(logPrefix, archivePath);

                long deltaSize = new FileInfo(archivePath).Length;

                if (ShouldKeepDelta(deltaSize, out long deltaBytesUsingReference, out double averageBytesPerCoreRoot))
                {
                    File.Delete(tarPath);

                    await UploadAndSaveAsync(logPrefix, commit, commitTime, type, blobName, archivePath, _referenceBlobName);

                    _commitsUsingReference++;
                    _deltaBytesUsingReference = deltaBytesUsingReference + deltaSize;
                    return;
                }

                await LogAsync($"[{logPrefix}] The delta is larger than the {ToMB(averageBytesPerCoreRoot)} MB/CoreRoot " +
                    $"that '{_referenceBlobName}' currently averages over {_commitsUsingReference + 1} CoreRoots - " +
                    $"recompressing as a new standalone reference instead.");
            }

            await LogAsync($"[{logPrefix}] Compressing {type} artifacts as a new standalone reference ...");

            await CoreRootArchive.CompressAsync(tarPath, archivePath, prefix: default, JobTimeout);
            await LogArchiveSizeAsync(logPrefix, archivePath);

            long referenceSize = new FileInfo(archivePath).Length;

            // The reference is only adopted once it is durably stored - no delta may point at a blob that
            // failed to upload. If this throws, the next commit simply becomes the reference instead.
            await UploadAndSaveAsync(logPrefix, commit, commitTime, type, blobName, archivePath, prefixBlobName: null);

            // Drop the old prefix before reading the new one so the two are never held at the same time.
            _referencePrefix = default;
            _referencePrefix = await CoreRootArchive.ReadPrefixAsync(tarPath, JobTimeout);
            _referenceBlobName = blobName;
            _referenceCommitTime = commitTime;
            _commitsUsingReference = 0;
            _referenceSize = referenceSize;
            _deltaBytesUsingReference = 0;
        }
        finally
        {
            try { File.Delete(tarPath); }
            catch { }
        }
    }

    private async Task UploadAndSaveAsync(string logPrefix, string commit, DateTime commitTime, string type, string blobName, string archivePath, string? prefixBlobName)
    {
        await LogAsync($"[{logPrefix}] Uploading CoreRoot ...");

        await UploadCoreRootAsync(blobName, archivePath);
        await CoreRootAPI.SaveAsync(this, commit, type, blobName, prefixBlobName, commitTime);

        File.Delete(archivePath);
    }

    private async Task LogArchiveSizeAsync(string logPrefix, string archivePath)
    {
        await LogAsync($"[{logPrefix}] Archive size: {ToMB(new FileInfo(archivePath).Length)} MB");
    }

    /// <summary>
    /// Decides whether a freshly compressed delta is worth keeping, or whether this CoreRoot should be
    /// recompressed as a new standalone reference instead.
    /// <para>
    /// A reference plus its <c>n</c> deltas costs <c>R + ΣDᵢ</c> for <c>n + 1</c> CoreRoots, so one more
    /// delta lowers the storage bill iff <c>D₍ₙ₊₁₎ &lt; (R + ΣDᵢ) / (n + 1)</c>. Deltas grow as the commit
    /// drifts away from the reference, so the first delta to fail that test is exactly where a fresh
    /// reference (assumed to also cost <c>R</c>) becomes cheaper - the greedy test is optimal, not a
    /// heuristic. A fixed commit count can't be: the optimum is <c>sqrt(2R / a)</c> for deltas growing at
    /// <c>a</c> bytes per commit, so it moves with how much churn the tree sees.
    /// </para>
    /// </summary>
    /// <param name="deltaBytesUsingReference">
    /// The resolved <c>ΣDᵢ</c>, which the caller should carry forward once the delta is kept.
    /// </param>
    private bool ShouldKeepDelta(long deltaSize, out long deltaBytesUsingReference, out double averageBytesPerCoreRoot)
    {
        int commits = _commitsUsingReference;

        // Deltas grow roughly linearly with the distance from the reference, so this one being D₍ₙ₊₁₎ implies
        // the n before it summed to about n * D₍ₙ₊₁₎ / 2. From here on the sizes are tracked exactly.
        deltaBytesUsingReference = _deltaBytesUsingReference ?? (deltaSize * commits / 2);

        averageBytesPerCoreRoot = (double)(_referenceSize + deltaBytesUsingReference) / (commits + 1);

        // Falling back to keeping the delta if the reference size is somehow unknown.
        return _referenceSize <= 0 || deltaSize < averageBytesPerCoreRoot;
    }

    private static string ToMB(double bytes) => $"{bytes / (1024 * 1024):F1}";
}
