namespace Runner.Helpers;

internal static class CoreRootAPI
{
    public static async Task<CoreRootEntry?> GetAsync(JobBase job, string sha, string type)
    {
        return await job.SendAsyncCore(
            HttpMethod.Get,
            $"CoreRoot/Get?sha={sha}&arch={JobBase.Arch}&os={JobBase.Os}&type={type}",
            responseFunc: async response => response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<CoreRootEntry>() : null);
    }

    public static async Task<CoreRootEntry[]> ListAsync(JobBase job, string range, string type)
    {
        return await job.SendAsyncCore(
            HttpMethod.Get,
            $"CoreRoot/List?range={range}&arch={JobBase.Arch}&os={JobBase.Os}&type={type}",
            responseFunc: async response => await response.Content.ReadFromJsonAsync<CoreRootEntry[]>())
            ?? [];
    }

    public static async Task<CoreRootEntry[]> AllAsync(JobBase job, string type)
    {
        return await job.SendAsyncCore(
            HttpMethod.Get,
            $"CoreRoot/All?arch={JobBase.Arch}&os={JobBase.Os}&type={type}",
            responseFunc: async response => await response.Content.ReadFromJsonAsync<CoreRootEntry[]>())
            ?? [];
    }

    public static async Task SaveAsync(JobBase job, string sha, string type, string blobName, string? prefixBlobName, DateTime commitTime)
    {
        // Sent as Unix seconds so that the timestamp survives the query string untouched (an ISO 8601
        // offset like "+02:00" would otherwise have to be escaped).
        long commitTimeUnixSeconds = new DateTimeOffset(DateTime.SpecifyKind(commitTime, DateTimeKind.Utc)).ToUnixTimeSeconds();

        await job.SendAsyncCore<object>(
            HttpMethod.Get,
            $"CoreRoot/Save?jobId={job.JobId}&sha={sha}&arch={JobBase.Arch}&os={JobBase.Os}&type={type}&blobName={blobName}&prefixBlobName={prefixBlobName}&commitTime={commitTimeUnixSeconds}");
    }

    public sealed class CoreRootEntry
    {
        public string? Sha { get; set; }
        public string? Arch { get; set; }
        public string? Os { get; set; }
        public string? Type { get; set; }
        public string? Url { get; set; }
        public string? BlobName { get; set; }

        /// <summary>The blob that was used as the ZStandard prefix when compressing this archive, if any.</summary>
        public string? PrefixBlobName { get; set; }

        /// <summary>A download link for <see cref="PrefixBlobName"/>. Required to decompress this archive.</summary>
        public string? PrefixUrl { get; set; }

        /// <summary>
        /// When the underlying commit was authored. CoreRoots are not necessarily generated in commit
        /// order, so this - not <see cref="CreatedOn"/> - is what orders entries against each other.
        /// </summary>
        public DateTime CommitTime { get; set; }

        /// <summary>When this CoreRoot was generated and uploaded.</summary>
        public DateTime CreatedOn { get; set; }

        public string? Directory { get; set; }
    }
}
