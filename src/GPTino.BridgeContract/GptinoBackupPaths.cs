namespace GPTino.BridgeContract;

/// <summary>
/// The one place that knows where GPTino writes its own pre-execute checkpoint copies.
///
/// <para>
/// Shared because two assemblies need the same answer for opposite reasons: GPTino.Grasshopper
/// WRITES there, and GPTino.Rhino must REFUSE to adopt anything under it as a document identity.
/// A backup save raises <c>RhinoDoc.EndSaveDocument</c> with the backup file's path, and the
/// document-observation path keys the project data root off the Rhino file path — so adopting a
/// backup copy forks the project, orphaning the real file's sessions. This is the same hazard
/// <c>RhinoAutoSavePaths</c> guards for Rhino's own autosave; GPTino's copies need it too.
/// </para>
/// </summary>
public static class GptinoBackupPaths
{
    /// <summary>Root of all GPTino backups: %LOCALAPPDATA%\GPTino\backups.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GPTino",
        "backups");

    /// <summary>
    /// True when <paramref name="path"/> is inside <see cref="Root"/>. Never throws: an
    /// unparseable path is simply "not ours", because the callers are guards on a hot event
    /// handler where an exception would be worse than a missed classification.
    /// </summary>
    public static bool IsBackupPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            var candidate = Path.GetFullPath(path);
            var root = Path.GetFullPath(Root);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
            {
                root += Path.DirectorySeparatorChar;
            }
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or PathTooLongException
                                              or NotSupportedException
                                              or System.Security.SecurityException)
        {
            return false;
        }
    }
}
