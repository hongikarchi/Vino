using System.Runtime.InteropServices;

namespace Vino.AgentHost.Hosting;

/// <summary>
/// Keeps Windows crash dialogs off the desktop for THIS process and everything it spawns.
/// The model's private verification compiles and runs scratch programs (codex → shell →
/// scratch exe); an unhandled exception there pops a modal "application error" box that
/// interrupts the user and hangs the model's shell command until someone clicks it
/// (BrepApiScratch.exe, observed live 08-21). The process error mode is inherited by child
/// processes, so one call here silences the whole tree — failures still exit nonzero and
/// reach the model as command output.
/// </summary>
public static class CrashDialogSuppression
{
    private const uint SemFailCriticalErrors = 0x0001;
    private const uint SemNoGpFaultErrorBox = 0x0002;

    public static void Apply()
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetErrorMode(SemFailCriticalErrors | SemNoGpFaultErrorBox);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);
}
