using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Orchestrator;

public enum OpenTtdConsoleOperation
{
    Pause,
    Unpause,
    Save,
    Quit,
}

public sealed record OpenTtdConsoleCommand(OpenTtdConsoleOperation Operation, string? SaveName = null)
{
    public static OpenTtdConsoleCommand Pause { get; } = new(OpenTtdConsoleOperation.Pause);
    public static OpenTtdConsoleCommand Unpause { get; } = new(OpenTtdConsoleOperation.Unpause);
    public static OpenTtdConsoleCommand Quit { get; } = new(OpenTtdConsoleOperation.Quit);

    public static OpenTtdConsoleCommand Save(string saveName)
    {
        if (string.IsNullOrWhiteSpace(saveName) ||
            !saveName.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            throw new ArgumentException("OpenTTD save names must use lowercase letters, digits, or hyphens.", nameof(saveName));
        }

        return new OpenTtdConsoleCommand(OpenTtdConsoleOperation.Save, saveName);
    }
}

public interface IOpenTtdConsoleBridge
{
    Task SendAsync(int processId, OpenTtdConsoleCommand command, CancellationToken cancellationToken);

    Task<bool> WaitForSignalsAsync(
        int processId,
        IReadOnlyCollection<string> expectedSignals,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// A redacted failure from the short-lived Windows console bridge. An OpenTTD
/// dedicated process may release the bridge's prior console attachment a few
/// milliseconds after a successful operation, so that one narrow condition is
/// explicitly retryable by the process supervisor.
/// </summary>
public sealed class OpenTtdConsoleControlException : InvalidOperationException
{
    public OpenTtdConsoleControlException(string safeDetail, bool isTransientAttachmentFailure)
        : base($"{ArenaErrorCodes.RunConsoleControlFailed}: the dedicated OpenTTD console rejected a controlled command ({safeDetail}).")
    {
        IsTransientAttachmentFailure = isTransientAttachmentFailure;
    }

    public bool IsTransientAttachmentFailure { get; }
}

public sealed record OpenTtdProcessStartRequest(
    string ComponentId,
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string StandardOutputLogPath,
    string StandardErrorLogPath,
    bool HasWindow);

public interface IManagedArenaProcess : IAsyncDisposable
{
    string ComponentId { get; }

    int ProcessId { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task<bool> RequestGracefulShutdownAsync(CancellationToken cancellationToken);

    Task ForceTerminateAsync(CancellationToken cancellationToken);

    Task<bool> SetStableWindowTitleAsync(string title, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IArenaProcessFactory
{
    Task<IManagedArenaProcess> StartAsync(OpenTtdProcessStartRequest request, CancellationToken cancellationToken);
}

public interface ILoopbackReadinessProbe
{
    Task<bool> WaitForPortAsync(
        string address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class TcpLoopbackReadinessProbe : ILoopbackReadinessProbe
{
    private readonly TimeProvider _timeProvider;

    public TcpLoopbackReadinessProbe(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> WaitForPortAsync(
        string address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out IPAddress? parsedAddress) || !IPAddress.IsLoopback(parsedAddress))
        {
            throw new ArgumentException("OpenTTD readiness can only probe a loopback endpoint.", nameof(address));
        }

        DateTimeOffset deadline = _timeProvider.GetUtcNow().Add(timeout);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using TcpClient client = new(parsedAddress.AddressFamily);
            try
            {
                await client.ConnectAsync(parsedAddress, port, cancellationToken);
                return true;
            }
            catch (SocketException)
            {
                TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(
                    remaining < TimeSpan.FromMilliseconds(200) ? remaining : TimeSpan.FromMilliseconds(200),
                    _timeProvider,
                    cancellationToken);
            }
        }

        return false;
    }
}

public sealed class SystemArenaProcessFactory : IArenaProcessFactory
{
    public Task<IManagedArenaProcess> StartAsync(OpenTtdProcessStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(request.ExecutablePath) || !Directory.Exists(request.WorkingDirectory))
        {
            throw new InvalidOperationException(
                $"{ArenaErrorCodes.RunPreparationFailed}: OpenTTD executable or its isolated working directory is missing.");
        }

        ProcessStartInfo startInfo = new(request.ExecutablePath)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = !request.HasWindow,
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"{ArenaErrorCodes.OpenTtdProcessExited}: OpenTTD did not start.");
        }

        IManagedArenaProcess managedProcess = new SystemManagedArenaProcess(process, request);
        return Task.FromResult(managedProcess);
    }
}

internal sealed class SystemManagedArenaProcess : IManagedArenaProcess
{
    private readonly Process _process;
    private readonly StreamWriter _standardOutput;
    private readonly StreamWriter _standardError;
    private readonly object _logLock = new();
    private bool _disposed;

    public SystemManagedArenaProcess(Process process, OpenTtdProcessStartRequest request)
    {
        _process = process;
        ComponentId = request.ComponentId;
        _standardOutput = CreateLogWriter(request.StandardOutputLogPath);
        _standardError = CreateLogWriter(request.StandardErrorLogPath);
        _process.OutputDataReceived += (_, eventArgs) => WriteLogLine(_standardOutput, eventArgs.Data);
        _process.ErrorDataReceived += (_, eventArgs) => WriteLogLine(_standardError, eventArgs.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        WriteLogLine(_standardOutput, $"process-started pid={_process.Id}");
    }

    public string ComponentId { get; }

    public int ProcessId => _process.Id;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode => HasExited ? TryGetExitCode() : null;

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (HasExited)
        {
            return true;
        }

        Task waitTask = _process.WaitForExitAsync(cancellationToken);
        Task timeoutTask = Task.Delay(timeout, cancellationToken);
        Task completed = await Task.WhenAny(waitTask, timeoutTask);
        if (completed != waitTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        await waitTask;
        WriteLogLine(_standardOutput, $"process-exited exit_code={TryGetExitCode()?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
        return true;
    }

    public Task<bool> RequestGracefulShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasExited)
        {
            return Task.FromResult(true);
        }

        try
        {
            bool requested = _process.CloseMainWindow();
            WriteLogLine(_standardOutput, requested ? "graceful-shutdown-requested" : "graceful-shutdown-unavailable");
            return Task.FromResult(requested);
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(false);
        }
    }

    public async Task ForceTerminateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HasExited)
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
            WriteLogLine(_standardError, "forced-termination-completed");
        }
        catch (InvalidOperationException)
        {
            // The process exited in the interval between the check and Kill.
        }
    }

    public async Task<bool> SetStableWindowTitleAsync(string title, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited)
            {
                return false;
            }

            _process.Refresh();
            IntPtr handle = _process.MainWindowHandle;
            if (handle != IntPtr.Zero && NativeWindowMethods.SetWindowText(handle, title))
            {
                WriteLogLine(_standardOutput, "stable-window-title-assigned");
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        return false;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        lock (_logLock)
        {
            _standardOutput.Dispose();
            _standardError.Dispose();
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private static StreamWriter CreateLogWriter(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("Process log path has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    private void WriteLogLine(StreamWriter writer, string? line)
    {
        if (line is null || _disposed)
        {
            return;
        }

        lock (_logLock)
        {
            if (!_disposed)
            {
                writer.WriteLine(ArtifactTextRedactor.Redact(line));
            }
        }
    }

    private int? TryGetExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

internal static class NativeWindowMethods
{
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowText(IntPtr windowHandle, string text);
}
