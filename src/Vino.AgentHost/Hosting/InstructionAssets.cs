using System.Collections.Concurrent;

namespace Vino.AgentHost.Hosting;

/// <summary>
/// Loads model-facing instruction text from markdown assets shipped beside the executable
/// (assets/instructions → instructions/), falling back to the copy of the same file the build
/// embeds into this assembly when the loose file is missing or unreadable. The loose file keeps
/// prompt experiments a file edit instead of a rebuild; the embedded copy keeps a broken install
/// functional and, being produced by the build from the single source file, cannot drift from it.
/// </summary>
public static class InstructionAssets
{
    private const string ResourcePrefix = "Vino.AgentHost.Instructions.";

    private static readonly ConcurrentQueue<string> BufferedDiagnostics = new();
    private static volatile Action<string>? _diagnosticSink;

    /// <summary>
    /// Receives one message per fallback event. Serving the embedded copy is invisible to the model
    /// (the text is identical), so this is the only signal that an install has lost its loose
    /// instruction files. Loading happens in static initializers, before any logger exists — events
    /// raised while the sink is unset are buffered and flushed on assignment.
    /// </summary>
    public static Action<string>? DiagnosticSink
    {
        get => _diagnosticSink;
        set
        {
            _diagnosticSink = value;
            if (value is not null)
            {
                Flush(value);
            }
        }
    }

    public static string LoadOrFallback(string fileName) =>
        LoadOrFallback(fileName, Path.Combine(AppContext.BaseDirectory, "instructions"));

    // The directory is a parameter so a test can exercise the fallback branch against a directory
    // that does not exist, instead of deleting the running assembly's own instruction files.
    internal static string LoadOrFallback(string fileName, string instructionsDirectory)
    {
        var path = Path.Combine(instructionsDirectory, fileName);
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
                Report($"Instruction asset '{path}' exists but is empty; serving the embedded copy.");
            }
            else
            {
                Report($"Instruction asset '{path}' is missing; serving the embedded copy.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through to the embedded copy; instruction loading must never fail startup.
            Report($"Instruction asset '{path}' is unreadable ({exception.GetType().Name}); serving the embedded copy.");
        }
        return ReadEmbedded(fileName);
    }

    internal static string ReadEmbedded(string fileName)
    {
        using var stream = typeof(InstructionAssets).Assembly.GetManifestResourceStream(ResourcePrefix + fileName)
            ?? throw new InvalidOperationException(
                $"Embedded instruction resource '{ResourcePrefix}{fileName}' is missing from the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Report(string message)
    {
        BufferedDiagnostics.Enqueue(message);
        // Re-read after enqueue so a sink assigned concurrently cannot strand the message.
        if (_diagnosticSink is { } sink)
        {
            Flush(sink);
        }
    }

    private static void Flush(Action<string> sink)
    {
        while (BufferedDiagnostics.TryDequeue(out var message))
        {
            sink(message);
        }
    }
}
