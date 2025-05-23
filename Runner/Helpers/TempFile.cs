﻿namespace Runner.Helpers;

internal sealed class TempFile : IDisposable
{
    private static long _counter = 1;

    public string Path { get; private set; }

    public TempFile(string extension)
    {
        Path = $"RunnerTemp_{Interlocked.Increment(ref _counter)}.{extension.TrimStart('.')}";
        Path = System.IO.Path.GetFullPath(Path);
    }

    ~TempFile()
    {
        Cleanup();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Cleanup();
    }

    private void Cleanup()
    {
        try
        {
            File.Delete(Path);
        }
        catch { }
    }

    public override string ToString()
    {
        return Path;
    }
}
