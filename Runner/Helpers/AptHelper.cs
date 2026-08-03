namespace Runner.Helpers;

/// <summary>
/// Serializes apt operations. Multiple concurrent apt processes fight over the apt locks
/// and one of them fails with "Could not get lock ... It is held by process N (apt-get)".
/// This affects both our own apt-get calls and the ones runtime's install-dependencies.sh performs.
/// </summary>
internal static class AptHelper
{
    private static readonly SemaphoreSlim s_lock = new(1, 1);

    public static Task RunAptGetAsync(JobBase job, string arguments, string? logPrefix = null) =>
        RunWithAptLockAsync(() => job.RunProcessAsync("apt-get", arguments, logPrefix: logPrefix));

    /// <summary>
    /// Runs an action that invokes apt internally while holding the apt lock.
    /// </summary>
    public static async Task RunWithAptLockAsync(Func<Task> action)
    {
        await s_lock.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            s_lock.Release();
        }
    }
}
