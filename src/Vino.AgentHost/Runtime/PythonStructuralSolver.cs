using System.Diagnostics;
using System.Text;
using Vino.AgentHost.Hosting;

namespace Vino.AgentHost.Runtime;

/// <summary>Runs the shipped structural solver on an input JSON and returns its report JSON.</summary>
public interface IStructuralSolver
{
    Task<string> SolveAsync(string inputJson, CancellationToken cancellationToken);

    /// <summary>Runs the shipped bay-layout script (layout.py) on an input JSON.</summary>
    Task<string> LayoutAsync(string inputJson, CancellationToken cancellationToken);
}

/// <summary>
/// Out-of-process PyNite runner for structural_solve. The solve is pure computation on the
/// extraction artifact — it needs neither the Rhino document nor the UI thread, so it runs in a
/// spawned Python process and cannot hit the bridge's 45-second budget no matter how large the
/// frame is. The solver script is SHIPPED CODE (assets/data/structural/solver.py): determinism is
/// the safety property, so the agent gets to call it, never to rewrite it.
/// </summary>
public sealed class PythonStructuralSolver : IStructuralSolver
{
    /// <summary>Rhino 8's scripting CPython, where PyNiteFEA is expected to be installed.</summary>
    private static readonly string RhinoCodePython = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".rhinocode", "py39-rh8", "python.exe");

    private readonly string _solverPath;
    private readonly string _layoutPath;
    private readonly string? _pythonOverride;
    private readonly TimeSpan _timeout;

    public PythonStructuralSolver(
        DataLibrary data,
        string? pythonPath = null,
        TimeSpan? timeout = null)
    {
        _solverPath = Path.Combine(data.Root, "structural", "solver.py");
        _layoutPath = Path.Combine(data.Root, "structural", "layout.py");
        _pythonOverride = pythonPath;
        _timeout = timeout ?? TimeSpan.FromSeconds(300);
    }

    /// <summary>
    /// VINO_PYTHON (explicit override) → Rhino's py39-rh8 environment. Failure names every
    /// candidate and the install command instead of a bare not-found: "install PyNiteFEA into
    /// Rhino's Python" is the actual remedy, and the tool error is where the user reads it.
    /// </summary>
    private string ResolvePython()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_pythonOverride))
        {
            candidates.Add(_pythonOverride);
        }
        var environmentOverride = Environment.GetEnvironmentVariable("VINO_PYTHON");
        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            candidates.Add(environmentOverride);
        }
        candidates.Add(RhinoCodePython);
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException(
            "No Python interpreter for the structural solver. Tried: " +
            string.Join("; ", candidates) +
            ". Install Rhino 8 (its ScriptEditor provisions ~/.rhinocode/py39-rh8) and run " +
            "'py39-rh8\\python.exe -m pip install PyNiteFEA', or set VINO_PYTHON to a Python " +
            "with PyNiteFEA installed.");
    }

    public Task<string> SolveAsync(string inputJson, CancellationToken cancellationToken) =>
        RunScriptAsync(_solverPath, inputJson, cancellationToken);

    /// <summary>
    /// The layout script is pure geometry (no PyNite), but it ships and runs exactly like the
    /// solver: same interpreter resolution, same determinism argument — a candidate beam is a
    /// geometric claim that must reproduce.
    /// </summary>
    public Task<string> LayoutAsync(string inputJson, CancellationToken cancellationToken) =>
        RunScriptAsync(_layoutPath, inputJson, cancellationToken);

    private async Task<string> RunScriptAsync(string scriptPath, string inputJson, CancellationToken cancellationToken)
    {
        if (!File.Exists(scriptPath))
        {
            throw new InvalidOperationException($"The shipped solver asset is missing: {scriptPath}");
        }
        var python = ResolvePython();
        var inputPath = Path.Combine(
            Path.GetTempPath(),
            $"vino-structural-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, inputJson, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The solver writes its report as UTF-8 explicitly (marks are layer names, and a
                // Korean layer name must survive a cp949 console); read it the same way.
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add(inputPath);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start {python}.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort; the process may have exited between the timeout and the kill.
                }
                throw new InvalidOperationException(
                    cancellationToken.IsCancellationRequested
                        ? "The structural solve was cancelled."
                        : $"The structural solve exceeded its {_timeout.TotalSeconds:F0}s budget and was stopped.");
            }
            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                var tail = errors.Length > 2000 ? errors[^2000..] : errors;
                throw new InvalidOperationException(
                    $"The structural solver exited with code {process.ExitCode}. " +
                    (string.IsNullOrWhiteSpace(tail) ? "No diagnostics were written." : $"Diagnostics: {tail}"));
            }
            return output;
        }
        finally
        {
            try
            {
                File.Delete(inputPath);
            }
            catch
            {
                // A leaked temp file is harmless; never mask the real outcome for one.
            }
        }
    }
}
