using System.Collections.Concurrent;
using System.Text;
using GPTino.BridgeContract;
using Grasshopper.Kernel;

namespace GPTino.Grasshopper;

/// <summary>
/// Best-effort pre-execute checkpoint. A script solve runs on Rhino's single UI thread and can freeze
/// (unabortable) or crash the process; if the user then force-kills Rhino, all unsaved work is lost.
/// Before each execute this writes BACKUP COPIES of the Grasshopper definition and the paired Rhino model
/// into a GPTino-owned folder (never the user's own files), so a force-kill afterwards loses nothing —
/// the user (or a future restore flow) recovers from the copies.
///
/// <para>
/// Design rules: (1) never touch the user's files — only GPTino-owned copies; (2) never throw — a backup
/// failure logs and is swallowed so it can never block authoring; (3) atomic writes (temp + rename) so a
/// crash mid-backup can never corrupt a good copy; (4) the small GH definition is backed up every execute
/// (it is the primary IP), the potentially large Rhino model is throttled.
/// </para>
/// </summary>
internal static class GptinoDocumentBackup
{
    private static readonly ConcurrentDictionary<Guid, DateTime> LastRhinoBackupUtc = new();
    private static readonly TimeSpan RhinoBackupThrottle = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Root of all GPTino backups. Shared with GPTino.Rhino through
    /// <see cref="GptinoBackupPaths"/>, which uses it to refuse adopting a backup copy as the
    /// live document's identity.
    /// </summary>
    internal static string BackupRoot => GptinoBackupPaths.Root;

    public static void BeforeExecute(GH_Document? ghDocument, global::Rhino.RhinoDoc? rhinoDocument)
    {
        if (ghDocument is null)
        {
            return;
        }
        try
        {
            var directory = Path.Combine(BackupRoot, ghDocument.DocumentID.ToString("N"));
            Directory.CreateDirectory(directory);
            var savedGh = BackupGrasshopper(ghDocument, directory);
            var savedRhino = BackupRhino(rhinoDocument, ghDocument.DocumentID, directory);
            WriteManifest(ghDocument, rhinoDocument, directory, savedGh, savedRhino);
        }
        catch (Exception exception)
        {
            // Insurance must never block the work it protects.
            global::Rhino.RhinoApp.WriteLine($"[GPTino] pre-execute backup skipped: {exception.Message}");
        }
    }

    private static bool BackupGrasshopper(GH_Document ghDocument, string directory)
    {
        // The temp name must still END in .gh: GH_DocumentIO.SaveQuiet dispatches its archive
        // writer on the file EXTENSION, so the old ".definition.gh.tmp" was refused every single
        // time (verified live — same document, .gh and .ghx succeed, .tmp returns false). The
        // "primary IP" checkpoint this class advertises had therefore never once been written.
        var temporary = Path.Combine(directory, ".definition.tmp.gh");
        var final = Path.Combine(directory, "definition.gh");
        var io = new GH_DocumentIO(ghDocument);
        if (!io.SaveQuiet(temporary))
        {
            return false;
        }
        File.Move(temporary, final, overwrite: true);
        return true;
    }

    private static bool BackupRhino(global::Rhino.RhinoDoc? rhinoDocument, Guid documentId, string directory)
    {
        if (rhinoDocument is null)
        {
            return false;
        }
        var now = DateTime.UtcNow;
        // Throttle the (potentially large) model write; the GH definition above still checkpoints every time.
        if (LastRhinoBackupUtc.TryGetValue(documentId, out var last) && now - last < RhinoBackupThrottle)
        {
            return false;
        }
        // Keep a real .3dm extension: Rhino's writer is chosen by extension the same way
        // Grasshopper's is, and a ".3dm.tmp" name is one silent-refusal away from another
        // checkpoint that never happens.
        var temporary = Path.Combine(directory, ".model.tmp.3dm");
        var final = Path.Combine(directory, "model.3dm");
        var options = new global::Rhino.FileIO.FileWriteOptions
        {
            SuppressAllInput = true,
            // SuppressAllInput does NOT stop Rhino's "Failed to save … the temporary file could
            // not be renamed" message box (verified live). That box appears on the UI thread, in
            // the middle of an authoring turn, and blocks it — insurance must never do that.
            SuppressDialogBoxes = true,
            IncludeHistory = true,
        };
        try
        {
            // Write3dmFile, NOT WriteFile. Both write the same bytes, but WriteFile raises
            // RhinoDoc.EndSaveDocument carrying THIS temp path, and the plugin's save observer
            // then adopts it as the live document's identity — which forks the project data root
            // (it is keyed on the Rhino path) and orphans the real file's sessions. Verified
            // live: WriteFile fires the event with the temp path, Write3dmFile fires it with an
            // empty FileName, which ObserveRhinoDocument already ignores. The BackupRoot guard in
            // GptinoPlugIn.OnEndSaveDocument is the second layer, because this one leans on SDK
            // behaviour that is not contractual.
            if (!rhinoDocument.Write3dmFile(temporary, options))
            {
                return false;
            }
        }
        finally
        {
            options.Dispose();
        }
        File.Move(temporary, final, overwrite: true);
        LastRhinoBackupUtc[documentId] = now;
        return true;
    }

    private static void WriteManifest(
        GH_Document ghDocument,
        global::Rhino.RhinoDoc? rhinoDocument,
        string directory,
        bool savedGh,
        bool savedRhino)
    {
        // Hand-built JSON keeps the backup dependency-free and never itself throws on odd characters.
        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append("\"timestampUtc\":\"").Append(DateTime.UtcNow.ToString("O")).Append("\",");
        builder.Append("\"grasshopperDocumentId\":\"").Append(ghDocument.DocumentID.ToString("D")).Append("\",");
        builder.Append("\"grasshopperOriginalPath\":").Append(JsonString(ghDocument.FilePath)).Append(',');
        builder.Append("\"rhinoOriginalPath\":").Append(JsonString(rhinoDocument?.Path)).Append(',');
        builder.Append("\"grasshopperBackup\":").Append(savedGh ? "\"definition.gh\"" : "null").Append(',');
        builder.Append("\"rhinoBackup\":").Append(savedRhino ? "\"model.3dm\"" : "null");
        builder.Append('}');
        var temporary = Path.Combine(directory, ".manifest.json.tmp");
        var final = Path.Combine(directory, "manifest.json");
        File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(false));
        File.Move(temporary, final, overwrite: true);
    }

    private static string JsonString(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "null";
        }
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(character); break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }
}
