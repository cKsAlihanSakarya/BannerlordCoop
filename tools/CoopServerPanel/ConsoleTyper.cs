using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CoopServerPanel;

public enum TypeResult
{
    Ok,
    /// <summary>The server runs as administrator and the panel does not (or the reverse).</summary>
    AccessDenied,
    NoConsole,
    Failed,
}

/// <summary>Types a command line into the server's console window, as if someone typed it there.</summary>
public interface IConsoleTyper
{
    TypeResult Type(int processId, string text);
}

/// <summary>
/// Windows: a short-lived copy of the panel (<c>--type-into</c>) leaves its own console, attaches
/// to the server's and writes key events into its input buffer. A separate process is needed
/// because a process can only be attached to one console and the panel keeps its own.
/// </summary>
public sealed class WindowsConsoleTyper : IConsoleTyper
{
    public const string HelperArgument = "--type-into";

    private const int ExitOk = 0;
    private const int ExitAccessDenied = 5;
    private const int ExitNoConsole = 6;

    public TypeResult Type(int processId, string text)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        string host = Environment.ProcessPath;
        startInfo.FileName = host;
        // Run from source (dotnet CoopServerPanel.dll) the host is dotnet itself.
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(typeof(WindowsConsoleTyper).Assembly.Location);
        startInfo.ArgumentList.Add(HelperArgument);
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));

        using var helper = Process.Start(startInfo);
        if (helper == null) return TypeResult.Failed;
        if (!helper.WaitForExit(10_000))
        {
            try { helper.Kill(); } catch (InvalidOperationException) { }
            return TypeResult.Failed;
        }

        return helper.ExitCode switch
        {
            ExitOk => TypeResult.Ok,
            ExitAccessDenied => TypeResult.AccessDenied,
            ExitNoConsole => TypeResult.NoConsole,
            _ => TypeResult.Failed,
        };
    }

    /// <summary>Entry point of the helper process: <c>--type-into &lt;pid&gt; &lt;base64 text&gt;</c>.</summary>
    public static int RunHelper(string[] args)
    {
        if (!OperatingSystem.IsWindows() || args.Length != 3 || !uint.TryParse(args[1], out uint processId))
            return 2;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromBase64String(args[2]));
        }
        catch (FormatException)
        {
            return 2;
        }

        FreeConsole();
        if (!AttachConsole(processId))
        {
            int error = Marshal.GetLastWin32Error();
            return error == 5 ? ExitAccessDenied : error == 6 ? ExitNoConsole : 1;
        }

        IntPtr input = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (input == new IntPtr(-1))
        {
            FreeConsole();
            return 1;
        }

        try
        {
            var records = new List<InputRecord>(text.Length * 2 + 2);
            foreach (char c in text)
            {
                short scan = VkKeyScanW(c);
                ushort vk = (ushort)(scan & 0xFF);
                uint shift = (scan & 0x100) != 0 ? 0x10u : 0u;
                records.Add(Key(c, vk, true, shift));
                records.Add(Key(c, vk, false, shift));
            }
            records.Add(Key('\r', 0x0D, true, 0));
            records.Add(Key('\r', 0x0D, false, 0));

            InputRecord[] array = records.ToArray();
            return WriteConsoleInputW(input, array, (uint)array.Length, out uint written) && written == array.Length
                ? ExitOk
                : 1;
        }
        finally
        {
            CloseHandle(input);
            FreeConsole();
        }
    }

    private static InputRecord Key(char c, ushort vk, bool down, uint controlState) => new()
    {
        EventType = 1,
        KeyEvent = new KeyEventRecord
        {
            KeyDown = down ? 1 : 0,
            RepeatCount = 1,
            VirtualKeyCode = vk,
            VirtualScanCode = (ushort)MapVirtualKeyW(vk, 0),
            UnicodeChar = c,
            ControlKeyState = controlState,
        },
    };

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct KeyEventRecord
    {
        [FieldOffset(0)] public int KeyDown;
        [FieldOffset(4)] public ushort RepeatCount;
        [FieldOffset(6)] public ushort VirtualKeyCode;
        [FieldOffset(8)] public ushort VirtualScanCode;
        [FieldOffset(10)] public char UnicodeChar;
        [FieldOffset(12)] public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KeyEventRecord KeyEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteConsoleInputW(IntPtr input, InputRecord[] records, uint count, out uint written);
    [DllImport("user32.dll")] private static extern short VkKeyScanW(char c);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint mapType);
}

/// <summary>
/// Development stand-in off Windows: appends each command to panel-input.txt in the server's
/// data folder, which a test server reads. There is no real dedicated server off Windows.
/// </summary>
public sealed class FileConsoleTyper : IConsoleTyper
{
    private readonly string path;

    public FileConsoleTyper(string dataDir)
    {
        path = Path.Combine(dataDir, "panel-input.txt");
    }

    public TypeResult Type(int processId, string text)
    {
        try
        {
            File.AppendAllText(path, text + "\n");
            return TypeResult.Ok;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return TypeResult.Failed;
        }
    }
}
