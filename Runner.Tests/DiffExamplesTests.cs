using Runner.Helpers;
using Xunit;

namespace Runner.Tests;

public sealed class DiffExamplesTests : IDisposable
{
    private const string Method = "Example:Callback(System.Object) (FullOpts)";
    private const string OtherMethod = "Example:Identify(System.Object) (FullOpts)";
    private const string HealthyMethod = "Example:Healthy() (FullOpts)";
    private const string Separator = "; ============================================================\n";
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("Runner-DiffExamples-");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterleavedTimerCallbackIsExcludedOnEitherSide(bool swap)
    {
        string interleaved =
            Header(Method) +
            "G_M16090_IG01:\n       mov      rax, 0xD1FFAB1E\n" +
            "G_M16090_IG03:\n       tail.jmp \n" +
            "G_M17922_IG01:\n[rax]      Example:TimerCallback() push     rbp\n" +
            Footer(Method, 13) +
            "       push     " + Separator +
            "Unwind Info:\nr14\nG_M17922_IG02:\n       ret\n" +
            Footer(OtherMethod, 451) + Separator;
        string clean = Listing(Method, 13) + Listing(OtherMethod, 451);

        DiffExamples report = await CompareAsync(
            (swap ? clean : interleaved) + Listing(HealthyMethod, 10),
            (swap ? interleaved : clean) + Listing(HealthyMethod, 11));

        DiffExamples.Entry entry = Assert.Single(report.Entries);
        Assert.Equal(HealthyMethod, entry.Method);
        Assert.Equal(10, entry.BaseBytes);
        Assert.Equal(11, entry.DiffBytes);
        Assert.Contains(report.Notes, note => note.StartsWith("2 incomplete or interleaved"));
    }

    [Theory]
    [InlineData("foreign-block")]
    [InlineData("foreign-footer")]
    [InlineData("duplicate-footer")]
    [InlineData("invalid-size")]
    [InlineData("overflow-size")]
    [InlineData("embedded-header")]
    [InlineData("embedded-footer")]
    [InlineData("embedded-separator")]
    [InlineData("missing-footer")]
    [InlineData("overlapping-header")]
    [InlineData("invalid-block")]
    public async Task CorruptListingPoisonsAllOccurrencesButNotFollowingMethods(string corruption)
    {
        string header = Header(Method);
        const string Body = "G_M16090_IG01:\n       ret\n";
        string footer = Footer(Method, 13);
        string corrupt = corruption switch
        {
            "foreign-block" => header + Body + "G_M17922_IG02:\n       ret\n" + footer + Separator,
            "foreign-footer" => header + Body + Footer(OtherMethod, 451) + Separator,
            "duplicate-footer" => header + Body + footer + footer + Separator,
            "invalid-size" => header + Body + footer.Replace("code 13", "code invalid") + Separator,
            "overflow-size" => header + Body + footer.Replace("code 13", "code 99999999999999999999999") + Separator,
            "embedded-header" => header + Body + "       push " + Header(OtherMethod) + footer + Separator,
            "embedded-footer" => header + Body + "       push " + footer + Separator,
            "embedded-separator" => header + Body + footer + "       push " + Separator,
            "missing-footer" => header + Body + Separator,
            "overlapping-header" => header + Body + Header(OtherMethod) + footer + Footer(OtherMethod, 451) + Separator,
            "invalid-block" => header + "G_Moops_IG01:\n       ret\n" + footer + Separator,
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };

        DiffExamples report = await CompareAsync(
            Listing(Method, 13) + corrupt + Listing(Method, 13) + Listing(HealthyMethod, 10),
            Listing(Method, 14) + Listing(OtherMethod, 451) + Listing(HealthyMethod, 11));

        Assert.DoesNotContain(report.Entries, entry => entry.Method == Method);
        Assert.Contains(report.Entries, entry => entry.Method == HealthyMethod);
        Assert.Contains(report.Notes, note => note.Contains("incomplete or interleaved"));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task ValidRepeatedListingsKeepTheirSizesAndContent(string newline)
    {
        string baseline = Listing(Method, 13) + Listing(OtherMethod, 451) + Listing(Method, 14);
        string changed = Listing(Method, 15) + Listing(OtherMethod, 451) + Listing(Method, 16);
        DiffExamples report = await CompareAsync(
            baseline.Replace("\n", newline).TrimEnd('\r', '\n'),
            changed.Replace("\n", newline).TrimEnd('\r', '\n'));

        DiffExamples.Entry entry = Assert.Single(report.Entries);
        Assert.Equal(Method, entry.Method);
        Assert.Equal(27, entry.BaseBytes);
        Assert.Equal(31, entry.DiffBytes);
        Assert.Contains("2 -> 2 listings", entry.Description);
        Assert.DoesNotContain(OtherMethod, entry.Diff);
        Assert.DoesNotContain(report.Notes, note => note.Contains("incomplete or interleaved"));
    }

    [Fact]
    public async Task FooterMetadataAloneDoesNotCreateDiffs()
    {
        string baseline = Listing(Method, 13);
        DiffExamples report = await CompareAsync(baseline, baseline.Replace("PerfScore 2.25", "PerfScore 9.00"));

        Assert.Empty(report.Entries);
        Assert.DoesNotContain(report.Notes, note => note.Contains("incomplete or interleaved"));
    }

    [Fact]
    public async Task LegacyFootersAndMissingSeparatorsAreSupported()
    {
        string baseline = Listing(Method, 13)
            .Replace(Footer(Method, 13), "; Total bytes of code 13, prolog size 0\n").Replace(Separator, "") +
            Listing(OtherMethod, 451);
        string changed = baseline.Replace("code 13,", "code 14,");
        DiffExamples report = await CompareAsync(baseline, changed);

        DiffExamples.Entry entry = Assert.Single(report.Entries);
        Assert.Equal(Method, entry.Method);
        Assert.Equal(13, entry.BaseBytes);
        Assert.Equal(14, entry.DiffBytes);
        Assert.DoesNotContain(OtherMethod, entry.Diff);
    }

    private async Task<DiffExamples> CompareAsync(string baseline, string changed)
    {
        string mainDirectory = Path.Combine(_directory.FullName, "main");
        string prDirectory = Path.Combine(_directory.FullName, "pr");
        Directory.CreateDirectory(mainDirectory);
        Directory.CreateDirectory(prDirectory);
        await File.WriteAllTextAsync(Path.Combine(mainDirectory, "Example.dasm"), baseline);
        await File.WriteAllTextAsync(Path.Combine(prDirectory, "Example.dasm"), changed);

        using var client = new HttpClient();
        var job = new TestJob(client);
        job.Metadata[nameof(JobBase.CustomArguments)] = "-includeNewMethodRegressions -includeRemovedMethodImprovements";
        return await DiffExamples.CreateJitAsync(job, mainDirectory, prDirectory);
    }

    private static string Header(string name) => $"; Assembly listing for method {name}\n";

    private static string Footer(string name, int bytes) =>
        $"; Total bytes of code {bytes}, prolog size 0, PerfScore 2.25 for method {name}\n";

    private static string Listing(string name, int bytes) =>
        Header(name) + "G_M16090_IG01:\n       ret\n" + Footer(name, bytes) + Separator;

    public void Dispose() => _directory.Delete(recursive: true);

    private sealed class TestJob(HttpClient client) : JobBase(client)
    {
        protected override Task RunJobCoreAsync() => throw new NotSupportedException();
    }
}
