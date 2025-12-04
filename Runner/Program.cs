using Microsoft.CodeAnalysis;

try
{
    await RunAsync(args);
}
catch (Exception ex)
{
    Console.WriteLine(ex);
    File.WriteAllText("crash.txt", ex.ToString());
}

static async Task RunAsync(string[] args)
{
    Console.WriteLine("Starting ...");
    string? jobId = Environment.GetEnvironmentVariable("JOB_ID");
    string? jobType = Environment.GetEnvironmentVariable("JOB_TYPE");
    string? runnerId = Environment.GetEnvironmentVariable("RUNNER_ID");
    string? runnerToken = Environment.GetEnvironmentVariable("RUNNER_TOKEN");

    if (string.IsNullOrEmpty(jobId) && string.IsNullOrEmpty(runnerId))
    {
        if (args.Length == 1 &&
            args[0] is string eventPath &&
            File.Exists(eventPath))
        {
            JsonDocument document = JsonDocument.Parse(File.ReadAllText(eventPath));
            string? body = document.RootElement.GetProperty("issue").GetProperty("body").GetString();

            if (body is not null)
            {
                // <!-- RUN_AS_GITHUB_ACTION_{ExternalId} -->
                const string Prefix = "RUN_AS_GITHUB_ACTION_";

                int offset = body.IndexOf(Prefix, StringComparison.Ordinal) + Prefix.Length;
                int endOfId = body.IndexOf(' ', offset);

                jobId = body.Substring(offset, endOfId - offset);
            }
        }

        if (string.IsNullOrEmpty(jobId))
        {
            return;
        }
    }

    var client = new HttpClient(new SocketsHttpHandler
    {
        KeepAlivePingDelay = TimeSpan.FromSeconds(5),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
    })
    {
        DefaultRequestVersion = HttpVersion.Version20,
        BaseAddress = new Uri("https://mihubot.xyz/api/RuntimeUtils/"),
        Timeout = TimeSpan.FromMinutes(1),
    };

    if (!string.IsNullOrEmpty(jobId))
    {
        var metadata = await JobBase.GetMetadataAsync(client, jobId);
        jobType = metadata["JobType"];

        Console.WriteLine($"Obtained the job metadata. Job type: {jobType}");

        JobBase job = jobType switch
        {
            nameof(JitDiffJob) => new JitDiffJob(client, metadata),
            nameof(FuzzLibrariesJob) => new FuzzLibrariesJob(client, metadata),
            nameof(RebaseJob) => new RebaseJob(client, metadata),
            nameof(BenchmarkLibrariesJob) => new BenchmarkLibrariesJob(client, metadata),
            nameof(RegexDiffJob) => new RegexDiffJob(client, metadata),
            nameof(BackportJob) => new BackportJob(client, metadata),
            nameof(CoreRootGenerationJob) => new CoreRootGenerationJob(client, metadata),
            var type => throw new NotSupportedException(type),
        };

        await job.RunJobAsync();
    }
    else
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobType);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerToken);

        Console.WriteLine($"Starting prepared runner. Job type: {jobType}");

        JobBase job = jobType switch
        {
            nameof(JitDiffJob) => new JitDiffJob(client, runnerId, runnerToken),
            var type => throw new NotSupportedException(type),
        };

        await job.RunPreparedRunnerAsync();
    }
}