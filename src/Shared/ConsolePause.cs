using System.Runtime.InteropServices;

// The executables require elevation, and Windows cannot start such a program in place from a non-elevated console: it opens
// a new elevated console window and closes it the moment the process exits, taking the output with it. When this process is
// the only one attached to its console the window is ours and would vanish, so wait for Enter before exiting. In a terminal
// that is shared with a shell (an Administrator prompt, Windows Terminal) or when there is no console at all (a pipe), this
// does nothing.
static class ConsolePause
{
    public static void Register() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Pause();

    internal static bool OwnsConsole() => GetConsoleProcessList(new uint[1], 1) == 1;

    static void Pause()
    {
        if (!OwnsConsole()) return;
        Console.Error.WriteLine();
        Console.Error.Write("Press Enter to close this window...");
        try { Console.In.ReadLine(); }
        catch (IOException) { }
    }

    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
