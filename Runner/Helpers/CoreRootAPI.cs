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
            $"CoreRoot/List?range={range}arch={JobBase.Arch}&os={JobBase.Os}&type={type}",
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

    public static async Task SaveAsync(JobBase job, string sha, string type, string blobName)
    {
        await job.SendAsyncCore<object>(
            HttpMethod.Get,
            $"CoreRoot/Save?jobId={job.JobId}&sha={sha}&arch={JobBase.Arch}&os={JobBase.Os}&type={type}&blobName={blobName}");
    }

    public sealed class CoreRootEntry
    {
        public string? Sha { get; set; }
        public string? Arch { get; set; }
        public string? Os { get; set; }
        public string? Type { get; set; }
        public string? Url { get; set; }
        public string? Directory { get; set; }
    }
}
