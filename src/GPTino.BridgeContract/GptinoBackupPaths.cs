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

    /// <summary>
    /// Deletes all but the <paramref name="keep"/> most-recently-written backup folders under
    /// <paramref name="root"/>, so old sessions' copies do not accumulate without bound (two 447 MB
    /// copies were observed). Best-effort and never throws: a folder that will not delete is left in
    /// place. Returns the number of folders removed.
    /// </summary>
    public static int PruneToMostRecent(string root, int keep)
    {
        if (keep < 0)
        {
            keep = 0;
        }
        try
        {
            if (!Directory.Exists(root))
            {
                return 0;
            }
            var folders = new DirectoryInfo(root)
                .GetDirectories()
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .Skip(keep)
                .ToArray();
            var removed = 0;
            foreach (var folder in folders)
            {
                try
                {
                    folder.Delete(recursive: true);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException
                                                      or System.Security.SecurityException)
                {
                    // A folder in use or locked stays: pruning is insurance, never a guarantee.
                }
            }
            return removed;
        }
        catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or System.Security.SecurityException)
        {
            return 0;
        }
    }

    /// <summary>Prunes <see cref="Root"/> to the <paramref name="keep"/> most-recent folders.</summary>
    public static int PruneToMostRecent(int keep) => PruneToMostRecent(Root, keep);
}
