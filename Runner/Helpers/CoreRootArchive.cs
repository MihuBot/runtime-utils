using System.Formats.Tar;
using System.IO.Compression;

namespace Runner.Helpers;

/// <summary>
/// CoreRoot archives are plain tarballs compressed with ZStandard.
/// Consecutive dotnet/runtime commits produce nearly identical artifacts, so instead of compressing every
/// CoreRoot from scratch we hand the encoder a previously uploaded CoreRoot tarball as a prefix (the
/// equivalent of <c>zstd --patch-from</c>). The result is a delta that is orders of magnitude smaller than
/// a standalone archive and far cheaper to produce than 7z at max settings. The prefix used (if any) is
/// recorded in the CoreRoot metadata so that consumers know which archive they have to fetch first.
/// </summary>
internal static class CoreRootArchive
{
    public const string Extension = ".tar.zst";

    /// <summary>
    /// The prefix only helps if the compression window spans both the prefix and the data being compressed,
    /// and a CoreRoot tarball is a few hundred MB. Use the largest window ZStandard supports (2 GB on 64-bit).
    /// Long distance matching has to be enabled explicitly for windows this large to be useful.
    /// </summary>
    private static readonly int WindowLog2 = Math.Min(31, ZstandardCompressionOptions.MaxWindowLog2);

    public static string GetBlobName(string sha, string type) =>
        $"{sha}_{JobBase.Arch}_{JobBase.Os}_{type}{Extension}";

    /// <summary>
    /// Writes <paramref name="sourceDirectory"/> to an uncompressed tarball, preserving Unix file modes so
    /// that corerun and the native libraries stay executable.
    /// </summary>
    public static async Task CreateTarAsync(string sourceDirectory, string tarPath, CancellationToken cancellationToken = default)
    {
        await using FileStream output = OpenWrite(tarPath);
        await TarFile.CreateFromDirectoryAsync(sourceDirectory, output, includeBaseDirectory: false, cancellationToken);
    }

    public static async Task ExtractTarAsync(string tarPath, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);

        await using FileStream input = OpenRead(tarPath);
        await TarFile.ExtractToDirectoryAsync(input, destinationDirectory, overwriteFiles: true, cancellationToken);
    }

    /// <param name="prefix">The uncompressed contents of the reference tarball, or empty for a standalone archive.</param>
    public static async Task CompressAsync(string tarPath, string archivePath, ReadOnlyMemory<byte> prefix, CancellationToken cancellationToken = default)
    {
        var options = new ZstandardCompressionOptions
        {
            // Quality is left at the default - the prefix does the heavy lifting for deltas, and higher
            // levels cost a lot of CPU/memory for very little gain on artifacts this size.
            WindowLog2 = WindowLog2,
            EnableLongDistanceMatching = true,
        };

        await using var input = OpenRead(tarPath);
        await using var output = OpenWrite(archivePath);

        using var encoder = new ZstandardEncoder(options);

        if (!prefix.IsEmpty)
        {
            encoder.SetPrefix(prefix);
        }

        // Recording the source length in the frame header lets consumers size their buffers up front.
        encoder.SetSourceLength(input.Length);

        await using var compressionStream = new ZstandardStream(output, encoder, leaveOpen: true);
        await input.CopyToAsync(compressionStream, cancellationToken);
    }

    /// <param name="prefix">The uncompressed contents of the reference tarball, or empty if the archive is standalone.</param>
    public static async Task DecompressAsync(string archivePath, string tarPath, ReadOnlyMemory<byte> prefix, CancellationToken cancellationToken = default)
    {
        var options = new ZstandardDecompressionOptions
        {
            // Must be at least the window log used during compression, or decompression fails.
            MaxWindowLog2 = WindowLog2,
        };

        await using var input = OpenRead(archivePath);
        await using var output = OpenWrite(tarPath);

        using var decoder = new ZstandardDecoder(options);

        if (!prefix.IsEmpty)
        {
            decoder.SetPrefix(prefix);
        }

        await using var decompressionStream = new ZstandardStream(input, decoder, leaveOpen: true);
        await decompressionStream.CopyToAsync(output, cancellationToken);
    }

    /// <summary>
    /// Reads a reference tarball into memory so that it can be used as a compression/decompression prefix.
    /// The prefix has to be a single contiguous buffer.
    /// </summary>
    public static async Task<ReadOnlyMemory<byte>> ReadPrefixAsync(string tarPath, CancellationToken cancellationToken = default)
    {
        return await File.ReadAllBytesAsync(tarPath, cancellationToken);
    }

    private static FileStream OpenRead(string path) =>
        new(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, Options = FileOptions.Asynchronous | FileOptions.SequentialScan });

    private static FileStream OpenWrite(string path) =>
        new(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous });
}
