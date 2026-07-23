namespace OpenTtd.ModelArena.Foundation.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"arena-phase01-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateDirectory(params string[] segments)
    {
        string directory = segments.Aggregate(Path, System.IO.Path.Combine);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string WriteFile(string relativePath, string contents)
    {
        string filePath = System.IO.Path.Combine(Path, relativePath);
        string? parent = System.IO.Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("Temporary test file has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, true);
        }
    }
}
