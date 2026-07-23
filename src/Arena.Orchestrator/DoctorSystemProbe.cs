using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace OpenTtd.ModelArena.Orchestrator;

public interface IDoctorSystemProbe
{
    Task<HostProbeResult> GetHostAsync(CancellationToken cancellationToken);

    Task<ExecutableProbeResult> ProbeExecutableAsync(
        string executable,
        string versionArgument,
        CancellationToken cancellationToken);

    Task<ExecutableProbeResult> ProbeFileVersionAsync(
        string executable,
        CancellationToken cancellationToken);

    Task<PathProbeResult> CheckFileReadableAsync(string path, CancellationToken cancellationToken);

    Task<PathProbeResult> CheckDirectoryWritableAsync(string path, CancellationToken cancellationToken);

    Task<PortProbeResult> CheckLoopbackPortAvailableAsync(
        string address,
        int port,
        CancellationToken cancellationToken);

    Task<DiskProbeResult> GetDiskSpaceAsync(string path, CancellationToken cancellationToken);
}

public sealed record HostProbeResult(bool IsWindows, bool Is64Bit, int WindowsBuild);

public sealed record ExecutableProbeResult(bool IsAvailable, Version? Version, string? FailureKind);

public sealed record PathProbeResult(bool IsAvailable, string? FailureKind);

public sealed record PortProbeResult(bool IsAvailable, string? FailureKind);

public sealed record DiskProbeResult(bool IsAvailable, long AvailableBytes, string? FailureKind);

public sealed partial class DoctorSystemProbe : IDoctorSystemProbe
{
    private static readonly TimeSpan ExecutableProbeTimeout = TimeSpan.FromSeconds(10);

    public Task<HostProbeResult> GetHostAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int windowsBuild = OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : 0;
        return Task.FromResult(new HostProbeResult(
            OperatingSystem.IsWindows(),
            Environment.Is64BitOperatingSystem,
            windowsBuild));
    }

    public async Task<ExecutableProbeResult> ProbeExecutableAsync(
        string executable,
        string versionArgument,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(versionArgument))
        {
            return new ExecutableProbeResult(false, null, "invalid-command");
        }

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add(versionArgument);

        try
        {
            if (!process.Start())
            {
                return new ExecutableProbeResult(false, null, "process-did-not-start");
            }
        }
        catch (Win32Exception)
        {
            return new ExecutableProbeResult(false, null, "not-found");
        }
        catch (InvalidOperationException)
        {
            return new ExecutableProbeResult(false, null, "invalid-command");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExecutableProbeTimeout);
        try
        {
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), standardOutput, standardError);
            if (process.ExitCode != 0)
            {
                return new ExecutableProbeResult(false, null, "nonzero-exit");
            }

            Version? version = ExtractVersion(standardOutput.Result) ?? ExtractVersion(standardError.Result);
            return version is null
                ? new ExecutableProbeResult(false, null, "unparseable-version")
                : new ExecutableProbeResult(true, version, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            bool stopped = TryStopProcess(process);
            return new ExecutableProbeResult(false, null, stopped ? "timed-out" : "timed-out-after-exit");
        }
    }

    public Task<ExecutableProbeResult> ProbeFileVersionAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return Task.FromResult(new ExecutableProbeResult(false, null, "missing"));
        }

        try
        {
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executable);
            Version? version = ExtractVersion(versionInfo.ProductVersion ?? string.Empty) ??
                ExtractVersion(versionInfo.FileVersion ?? string.Empty);
            return Task.FromResult(version is null
                ? new ExecutableProbeResult(false, null, "unparseable-file-version")
                : new ExecutableProbeResult(true, version, null));
        }
        catch (Win32Exception)
        {
            return Task.FromResult(new ExecutableProbeResult(false, null, "unreadable-file-version"));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(new ExecutableProbeResult(false, null, "missing"));
        }
    }

    public Task<PathProbeResult> CheckFileReadableAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!File.Exists(path))
            {
                return Task.FromResult(new PathProbeResult(false, "missing"));
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(new PathProbeResult(true, null));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new PathProbeResult(false, "access-denied"));
        }
        catch (IOException)
        {
            return Task.FromResult(new PathProbeResult(false, "io-error"));
        }
    }

    public Task<PathProbeResult> CheckDirectoryWritableAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string probePath = Path.Combine(path, $".arena-doctor-write-{Guid.NewGuid():N}.tmp");
        try
        {
            if (!Directory.Exists(path))
            {
                return Task.FromResult(new PathProbeResult(false, "missing"));
            }

            using (FileStream stream = new(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
                stream.Flush(true);
            }

            return Task.FromResult(new PathProbeResult(true, null));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new PathProbeResult(false, "access-denied"));
        }
        catch (IOException)
        {
            return Task.FromResult(new PathProbeResult(false, "io-error"));
        }
    }

    public Task<PortProbeResult> CheckLoopbackPortAvailableAsync(
        string address,
        int port,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IPAddress.TryParse(address, out IPAddress? ipAddress) || !IPAddress.IsLoopback(ipAddress))
        {
            return Task.FromResult(new PortProbeResult(false, "not-loopback"));
        }

        try
        {
            using TcpListener listener = new(ipAddress, port);
            listener.Start();
            listener.Stop();
            return Task.FromResult(new PortProbeResult(true, null));
        }
        catch (SocketException)
        {
            return Task.FromResult(new PortProbeResult(false, "in-use-or-denied"));
        }
    }

    public Task<DiskProbeResult> GetDiskSpaceAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return Task.FromResult(new DiskProbeResult(false, 0, "missing-root"));
            }

            DriveInfo drive = new(root);
            return Task.FromResult(new DiskProbeResult(true, drive.AvailableFreeSpace, null));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new DiskProbeResult(false, 0, "invalid-path"));
        }
        catch (IOException)
        {
            return Task.FromResult(new DiskProbeResult(false, 0, "io-error"));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new DiskProbeResult(false, 0, "access-denied"));
        }
    }

    private static Version? ExtractVersion(string output)
    {
        Match match = VersionPattern().Match(output);
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, out int major) ||
            !int.TryParse(match.Groups["minor"].Value, out int minor))
        {
            return null;
        }

        int build = match.Groups["build"].Success && int.TryParse(match.Groups["build"].Value, out int parsedBuild)
            ? parsedBuild
            : 0;
        return new Version(major, minor, build);
    }

    private static bool TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                return true;
            }

            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"(?<!\d)(?<major>\d+)\.(?<minor>\d+)(?:\.(?<build>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
