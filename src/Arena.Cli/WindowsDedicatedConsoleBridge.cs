using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenTtd.ModelArena.Contracts;
using OpenTtd.ModelArena.Orchestrator;
using OpenTtd.ModelArena.Storage;

namespace OpenTtd.ModelArena.Cli;

/// <summary>
/// Runs as a short-lived child process because a process can attach to only one
/// Windows console at a time. It controls the dedicated OpenTTD console, never
/// a gameplay window, and accepts only the four fixed Phase 02 operations.
/// </summary>
internal static class WindowsDedicatedConsoleBridge
{
    private const int Success = 0;
    private const int Failure = 2;
    private const int TimedOut = 3;
    private const int ConsoleAttachmentUnavailable = 4;
    private const int MaximumTimeoutMilliseconds = 300_000;

    public static int Run(IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The OpenTTD dedicated-console bridge is available only on the supported Windows host.");
            return Failure;
        }

        if (arguments.Count == 0)
        {
            return Failure;
        }

        try
        {
            return arguments[0] switch
            {
                "send" => Send(arguments.Skip(1).ToArray()),
                "wait" => WaitForSignals(arguments.Skip(1).ToArray()),
                _ => Failure,
            };
        }
        catch (DedicatedConsoleUnavailableException exception)
        {
            Console.Error.WriteLine(SecretRedactor.Redact($"{ArenaErrorCodes.RunConsoleControlFailed}: {exception.Message}"));
            return ConsoleAttachmentUnavailable;
        }
        catch (Win32Exception exception)
        {
            Console.Error.WriteLine(SecretRedactor.Redact($"{ArenaErrorCodes.RunConsoleControlFailed}: {exception.Message}"));
            return Failure;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(SecretRedactor.Redact($"{ArenaErrorCodes.RunConsoleControlFailed}: {exception.Message}"));
            return Failure;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(SecretRedactor.Redact($"{ArenaErrorCodes.RunConsoleControlFailed}: {exception.Message}"));
            return Failure;
        }
    }

    private static int Send(IReadOnlyList<string> arguments)
    {
        if (!TryReadOptions(arguments, out Dictionary<string, List<string>> options) ||
            !TryGetProcessId(options, out int processId) ||
            !TryGetSingle(options, "--operation", out string? operationText) ||
            operationText is null ||
            !TryCreateCommand(operationText, options, out OpenTtdConsoleCommand? command) ||
            command is null)
        {
            return Failure;
        }

        using DedicatedConsoleSession console = DedicatedConsoleSession.Attach(processId);
        console.Send(command);
        Console.WriteLine("Dedicated-console command delivered.");
        return Success;
    }

    private static int WaitForSignals(IReadOnlyList<string> arguments)
    {
        if (!TryReadOptions(arguments, out Dictionary<string, List<string>> options) ||
            !TryGetProcessId(options, out int processId) ||
            !TryGetSingle(options, "--timeout-ms", out string? timeoutText) ||
            !int.TryParse(timeoutText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int timeoutMilliseconds) ||
            timeoutMilliseconds is < 1 or > MaximumTimeoutMilliseconds ||
            !options.TryGetValue("--signal", out List<string>? signals) ||
            signals.Count is < 1 or > 8 ||
            signals.Any(signal => !IsValidReadinessSignal(signal)))
        {
            return Failure;
        }

        using DedicatedConsoleSession console = DedicatedConsoleSession.Attach(processId);
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            string transcript = console.ReadTranscript();
            if (signals.All(signal => transcript.Contains(signal, StringComparison.Ordinal)))
            {
                Console.WriteLine("Dedicated-console readiness signals observed.");
                return Success;
            }

            Thread.Sleep(100);
        }

        Console.Error.WriteLine("Dedicated-console readiness signals were not observed before the timeout.");
        return TimedOut;
    }

    private static bool TryReadOptions(IReadOnlyList<string> arguments, out Dictionary<string, List<string>> options)
    {
        options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if ((argument is not "--pid" and not "--operation" and not "--save-name" and not "--timeout-ms" and not "--signal") ||
                index + 1 >= arguments.Count ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return false;
            }

            string value = arguments[++index];
            if (!options.TryGetValue(argument, out List<string>? values))
            {
                values = [];
                options.Add(argument, values);
            }

            values.Add(value);
        }

        return true;
    }

    private static bool TryGetProcessId(Dictionary<string, List<string>> options, out int processId)
    {
        processId = 0;
        return TryGetSingle(options, "--pid", out string? processIdText) &&
            int.TryParse(processIdText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out processId) &&
            processId > 0;
    }

    private static bool TryGetSingle(
        Dictionary<string, List<string>> options,
        string key,
        out string? value)
    {
        value = null;
        if (!options.TryGetValue(key, out List<string>? values) || values.Count != 1)
        {
            return false;
        }

        value = values[0];
        return true;
    }

    private static bool TryCreateCommand(
        string operationText,
        Dictionary<string, List<string>> options,
        out OpenTtdConsoleCommand? command)
    {
        command = operationText switch
        {
            "pause" when !options.ContainsKey("--save-name") => OpenTtdConsoleCommand.Pause,
            "unpause" when !options.ContainsKey("--save-name") => OpenTtdConsoleCommand.Unpause,
            "quit" when !options.ContainsKey("--save-name") => OpenTtdConsoleCommand.Quit,
            "save" when TryGetSingle(options, "--save-name", out string? saveName) => TryCreateSave(saveName),
            _ => null,
        };
        return command is not null;
    }

    private static OpenTtdConsoleCommand? TryCreateSave(string? saveName)
    {
        try
        {
            return saveName is null ? null : OpenTtdConsoleCommand.Save(saveName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsValidReadinessSignal(string signal) =>
        signal.Length is >= 8 and <= 80 &&
        signal.All(character =>
            character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private sealed class DedicatedConsoleUnavailableException : Win32Exception
    {
        public DedicatedConsoleUnavailableException(int nativeErrorCode)
            : base(nativeErrorCode, "Could not attach to the dedicated OpenTTD console.")
        {
        }
    }

    private sealed class DedicatedConsoleSession : IDisposable
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint OpenExisting = 3;
        private const short KeyEvent = 0x0001;
        private readonly IntPtr _inputHandle;
        private readonly IntPtr _outputHandle;
        private bool _disposed;

        private DedicatedConsoleSession(IntPtr inputHandle, IntPtr outputHandle)
        {
            _inputHandle = inputHandle;
            _outputHandle = outputHandle;
        }

        public static DedicatedConsoleSession Attach(int processId)
        {
            NativeMethods.FreeConsole();
            if (!NativeMethods.AttachConsole((uint)processId))
            {
                throw new DedicatedConsoleUnavailableException(Marshal.GetLastWin32Error());
            }

            IntPtr input = NativeMethods.CreateFile(
                "CONIN$",
                GenericRead | GenericWrite,
                0,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            IntPtr output = NativeMethods.CreateFile(
                "CONOUT$",
                GenericRead,
                0,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (input == NativeMethods.InvalidHandleValue || output == NativeMethods.InvalidHandleValue)
            {
                if (input != NativeMethods.InvalidHandleValue)
                {
                    NativeMethods.CloseHandle(input);
                }

                if (output != NativeMethods.InvalidHandleValue)
                {
                    NativeMethods.CloseHandle(output);
                }

                NativeMethods.FreeConsole();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the dedicated OpenTTD console handles.");
            }

            return new DedicatedConsoleSession(input, output);
        }

        public void Send(OpenTtdConsoleCommand command)
        {
            string line = command.Operation switch
            {
                OpenTtdConsoleOperation.Pause => "pause",
                OpenTtdConsoleOperation.Unpause => "unpause",
                OpenTtdConsoleOperation.Quit => "quit",
                OpenTtdConsoleOperation.Save when command.SaveName is not null => "save " + command.SaveName,
                _ => throw new InvalidOperationException("The dedicated-console command is invalid."),
            };
            InputRecord[] records = new InputRecord[line.Length + 1];
            for (int index = 0; index < line.Length; index++)
            {
                records[index] = CreateKeyRecord(line[index]);
            }

            records[^1] = CreateKeyRecord('\r');
            if (!NativeMethods.WriteConsoleInput(
                    _inputHandle,
                    records,
                    (uint)records.Length,
                    out uint written) ||
                written != records.Length)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not write to the dedicated OpenTTD console.");
            }
        }

        public string ReadTranscript()
        {
            if (!NativeMethods.GetConsoleScreenBufferInfo(_outputHandle, out ConsoleScreenBufferInfo info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not inspect the dedicated OpenTTD console.");
            }

            int length = Math.Min(info.Size.X * info.Size.Y, 64 * 1024);
            if (length <= 0)
            {
                return string.Empty;
            }

            char[] buffer = new char[length];
            if (!NativeMethods.ReadConsoleOutputCharacter(
                    _outputHandle,
                    buffer,
                    (uint)length,
                    new Coordinate(0, 0),
                    out uint read))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the dedicated OpenTTD console.");
            }

            return new string(buffer, 0, (int)read);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            NativeMethods.CloseHandle(_inputHandle);
            NativeMethods.CloseHandle(_outputHandle);
            NativeMethods.FreeConsole();
        }

        private static InputRecord CreateKeyRecord(char character) =>
            new()
            {
                EventType = KeyEvent,
                KeyEvent = new KeyEventRecord
                {
                    KeyDown = true,
                    RepeatCount = 1,
                    UnicodeChar = character,
                },
            };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool KeyDown;

        public short RepeatCount;
        public short VirtualKeyCode;
        public short VirtualScanCode;
        public char UnicodeChar;
        public int ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public short EventType;

        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coordinate
    {
        public Coordinate(short x, short y)
        {
            X = x;
            Y = y;
        }

        public short X;

        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRectangle
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public Coordinate Size;
        public Coordinate CursorPosition;
        public short Attributes;
        public SmallRectangle Window;
        public Coordinate MaximumWindowSize;
    }

    private static class NativeMethods
    {
        internal static readonly IntPtr InvalidHandleValue = new(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateFile(
            string name,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleInputW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteConsoleInput(
            IntPtr inputHandle,
            [In] InputRecord[] buffer,
            uint length,
            out uint numberOfEventsWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleScreenBufferInfo(
            IntPtr outputHandle,
            out ConsoleScreenBufferInfo consoleScreenBufferInfo);

        [DllImport("kernel32.dll", EntryPoint = "ReadConsoleOutputCharacterW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadConsoleOutputCharacter(
            IntPtr outputHandle,
            [Out] char[] character,
            uint length,
            Coordinate readCoordinate,
            out uint numberOfCharactersRead);
    }
}

internal sealed class CliOpenTtdConsoleBridge : IOpenTtdConsoleBridge
{
    private const int ConsoleAttachmentUnavailable = 4;

    public async Task SendAsync(int processId, OpenTtdConsoleCommand command, CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "__console-bridge",
            "send",
            "--pid",
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--operation",
            command.Operation.ToString().ToLowerInvariant(),
        ];
        if (command.SaveName is not null)
        {
            arguments.Add("--save-name");
            arguments.Add(command.SaveName);
        }

        BridgeInvocationResult result = await InvokeAsync(arguments, TimeSpan.FromSeconds(15), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new OpenTtdConsoleControlException(
                result.SafeDetail,
                result.ExitCode == ConsoleAttachmentUnavailable);
        }
    }

    public async Task<bool> WaitForSignalsAsync(
        int processId,
        IReadOnlyCollection<string> expectedSignals,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        List<string> arguments =
        [
            "__console-bridge",
            "wait",
            "--pid",
            processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--timeout-ms",
            Math.Clamp((int)timeout.TotalMilliseconds, 1, 300_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
        foreach (string signal in expectedSignals)
        {
            arguments.Add("--signal");
            arguments.Add(signal);
        }

        BridgeInvocationResult result = await InvokeAsync(arguments, timeout + TimeSpan.FromSeconds(15), cancellationToken);
        if (result.ExitCode == ConsoleAttachmentUnavailable)
        {
            throw new OpenTtdConsoleControlException(result.SafeDetail, isTransientAttachmentFailure: true);
        }

        return result.ExitCode == 0;
    }

    private static async Task<BridgeInvocationResult> InvokeAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateSelfStartInfo(arguments);
        using Process process = new()
        {
            StartInfo = startInfo,
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("The dedicated-console bridge did not start.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        Task waitTask = process.WaitForExitAsync(cancellationToken);
        Task timeoutTask = Task.Delay(timeout, cancellationToken);
        if (await Task.WhenAny(waitTask, timeoutTask) != waitTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new InvalidOperationException("The dedicated-console bridge exceeded its bounded timeout.");
        }

        await waitTask;
        string output = await standardOutput;
        string error = await standardError;
        return new BridgeInvocationResult(process.ExitCode, SecretRedactor.Redact((error + " " + output).Trim()));
    }

    private static ProcessStartInfo CreateSelfStartInfo(IReadOnlyList<string> arguments)
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current CLI executable path is unavailable.");
        ProcessStartInfo startInfo = new(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        string[] commandLine = Environment.GetCommandLineArgs();
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (commandLine.Length == 0 || !commandLine[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The .NET CLI host did not provide the Arena CLI assembly path.");
            }

            startInfo.ArgumentList.Add(commandLine[0]);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private sealed record BridgeInvocationResult(int ExitCode, string SafeDetail);
}
