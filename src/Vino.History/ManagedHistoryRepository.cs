using System.Text;
using LibGit2Sharp;

namespace Vino.History;

public sealed class ManagedHistoryRepository
{
    private static readonly Signature OrchestratorSignature =
        new("Vino Orchestrator", "vino@local", DateTimeOffset.UtcNow);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root;

    public ManagedHistoryRepository(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root => _root;

    public bool IsInitialized => Repository.IsValid(_root);

    public async Task<HistoryCommitResult> InitializeBaselineAsync(
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var resolvedFiles = ResolveAll(files);
            if (Repository.IsValid(_root))
            {
                throw new InvalidOperationException("Managed history already exists.");
            }

            Directory.CreateDirectory(_root);
            Repository.Init(_root);
            using var repository = new Repository(_root);
            WriteCanonicalTree(resolvedFiles);
            Commands.Stage(repository, "*");

            var signature = SignatureNow();
            var commit = repository.Commit(
                "baseline: initialize managed Rhino and Grasshopper state",
                signature,
                signature);
            repository.ApplyTag("baseline-v1", commit.Sha);
            return new HistoryCommitResult(commit.Sha, true, files.Count, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HistoryCommitResult> CommitAsync(
        HistoryCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var resolvedFiles = ResolveAll(request.Files);
            using var repository = new Repository(_root);
            var actualHead = repository.Head.Tip?.Sha;
            if (!string.Equals(actualHead, request.ExpectedHead, StringComparison.Ordinal))
            {
                throw new HistoryConcurrencyException(
                    $"History HEAD changed. Expected {request.ExpectedHead ?? "<none>"}, got {actualHead ?? "<none>"}.");
            }

            WriteCanonicalTree(resolvedFiles);
            Commands.Stage(repository, "*");
            var status = repository.RetrieveStatus(new StatusOptions());
            var changed = status.Count();
            if (changed == 0)
            {
                return new HistoryCommitResult(actualHead!, false, 0, DateTimeOffset.UtcNow);
            }

            var signature = SignatureNow();
            var commit = repository.Commit(BuildMessage(request.Metadata), signature, signature);
            return new HistoryCommitResult(commit.Sha, true, changed, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ReadHead()
    {
        if (!Repository.IsValid(_root))
        {
            return null;
        }

        using var repository = new Repository(_root);
        return repository.Head.Tip?.Sha;
    }

    /// <summary>
    /// The managed history, newest first, parsed from the commit messages
    /// <see cref="BuildMessage"/> writes. Read-only: it opens the repository, walks HEAD, and never
    /// touches the working tree — a checkout or reset here would fight <see cref="Verify"/>'s
    /// clean-tree requirement and, worse, move the user's files behind their back.
    /// </summary>
    public IReadOnlyList<HistoryRevision> ListRevisions(int limit = 100)
    {
        if (limit <= 0 || !Repository.IsValid(_root))
        {
            return Array.Empty<HistoryRevision>();
        }

        using var repository = new Repository(_root);
        var revisions = new List<HistoryRevision>();
        foreach (var commit in repository.Commits.Take(limit))
        {
            revisions.Add(ParseRevision(commit));
        }
        return revisions;
    }

    /// <summary>
    /// The bytes of one tracked file as of one commit, or null when the commit or the path is not
    /// there. This is how a past canvas state is read without disturbing the present one.
    /// </summary>
    public ReadOnlyMemory<byte>? ReadFileAt(string sha, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (!Repository.IsValid(_root))
        {
            return null;
        }

        using var repository = new Repository(_root);
        if (repository.Lookup<Commit>(sha) is not { } commit)
        {
            return null;
        }
        // Git paths are always forward-slashed regardless of the host platform.
        var path = relativePath.Replace('\\', '/').TrimStart('/');
        if (commit[path]?.Target is not Blob blob)
        {
            return null;
        }
        using var stream = blob.GetContentStream();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// The tracked paths directly under <paramref name="directory"/> as of one commit. Used to
    /// enumerate what a past revision holds without knowing the names in advance — the per-component
    /// source files a rewind restores from, for instance. Returns an empty list when the commit or
    /// the directory is not there; never recurses, and never touches the working tree.
    /// </summary>
    public IReadOnlyList<string> ListFilesAt(string sha, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Repository.IsValid(_root))
        {
            return [];
        }

        using var repository = new Repository(_root);
        if (repository.Lookup<Commit>(sha) is not { } commit)
        {
            return [];
        }
        var path = directory.Replace('\\', '/').Trim('/');
        if (commit[path]?.Target is not Tree tree)
        {
            return [];
        }
        return tree
            .Where(entry => entry.TargetType == TreeEntryTargetType.Blob)
            .Select(entry => $"{path}/{entry.Name}")
            .ToArray();
    }

    /// <summary>
    /// Whether a path is already tracked at the current head. The caller is a write-once capture:
    /// a component's pre-Vino source must be recorded the first time Vino edits it and never
    /// overwritten afterwards, or the oldest recoverable state would be lost on the second edit.
    /// </summary>
    public bool HasFileAtHead(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return Repository.IsValid(_root) && File.Exists(ResolveSafePath(relativePath));
    }

    /// <summary>
    /// The commit immediately BEFORE <paramref name="sha"/> on its first-parent chain — the state a
    /// job's own commit replaced, which is what "undo that job" restores. Null at the baseline.
    /// </summary>
    public HistoryRevision? FindParent(string sha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);
        if (!Repository.IsValid(_root))
        {
            return null;
        }

        using var repository = new Repository(_root);
        if (repository.Lookup<Commit>(sha) is not { } commit)
        {
            return null;
        }
        var parent = commit.Parents.FirstOrDefault();
        return parent is null ? null : ParseRevision(parent);
    }

    /// <summary>
    /// Reads back what <see cref="BuildMessage"/> wrote. A message that does not match the format
    /// still yields a revision — the sha and time are always real — so a hand-made commit in the
    /// history cannot make the whole listing unreadable.
    /// </summary>
    private static HistoryRevision ParseRevision(Commit commit)
    {
        var message = commit.Message ?? string.Empty;
        var firstLine = message.Split('\n', 2)[0].Trim();
        var revision = 0;
        var summary = firstLine;
        if (firstLine.StartsWith("rev ", StringComparison.Ordinal))
        {
            var colon = firstLine.IndexOf(':');
            if (colon > 4 && int.TryParse(firstLine[4..colon], out var parsed))
            {
                revision = parsed;
                summary = firstLine[(colon + 1)..].Trim();
            }
        }
        return new HistoryRevision(
            commit.Sha,
            revision,
            summary,
            commit.Author.When,
            ReadTrailerGuid(message, "Session-Id"),
            ReadTrailerGuid(message, "Task-Id"),
            ReadTrailer(message, "Snapshot"));
    }

    private static string? ReadTrailer(string message, string name)
    {
        var prefix = name + ": ";
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }
        return null;
    }

    private static Guid ReadTrailerGuid(string message, string name) =>
        Guid.TryParse(ReadTrailer(message, name), out var value) ? value : Guid.Empty;

    public HistoryVerificationResult Verify()
    {
        var problems = new List<string>();
        if (!Repository.IsValid(_root))
        {
            problems.Add("History directory is not a valid Git repository.");
            return new HistoryVerificationResult(false, null, problems);
        }

        using var repository = new Repository(_root);
        var head = repository.Head.Tip?.Sha;
        if (head is null)
        {
            problems.Add("History repository has no HEAD commit.");
        }

        var status = repository.RetrieveStatus(new StatusOptions());
        if (status.IsDirty)
        {
            problems.Add("History working tree contains uncommitted changes.");
        }

        if (repository.Tags["baseline-v1"] is null)
        {
            problems.Add("Missing baseline-v1 tag.");
        }

        return new HistoryVerificationResult(problems.Count == 0, head, problems);
    }

    private IReadOnlyList<ResolvedHistoryFile> ResolveAll(
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        EnsureNoReparsePoints(_root, "<repository-root>");
        var result = new List<ResolvedHistoryFile>(files.Count);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relativePath, content) in files)
        {
            var destination = ResolveSafePath(relativePath);
            EnsureNoReparsePoints(destination, relativePath);
            if (!destinations.Add(destination))
            {
                throw new HistoryPathException($"Multiple history paths resolve to the same destination: {relativePath}");
            }

            result.Add(new ResolvedHistoryFile(destination, content));
        }

        return result;
    }

    private static void WriteCanonicalTree(IReadOnlyList<ResolvedHistoryFile> files)
    {
        foreach (var file in files)
        {
            var destination = file.Destination;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + ".vino-tmp";
            File.WriteAllBytes(temporary, file.Content.ToArray());
            File.Move(temporary, destination, true);
        }
    }

    private string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new HistoryPathException($"History path must be relative: {relativePath}");
        }

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(IsReservedSegment))
        {
            throw new HistoryPathException($"History path contains a reserved segment: {relativePath}");
        }

        var normalized = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(_root, normalized));
        var rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new HistoryPathException($"History path escapes repository: {relativePath}");
        }
        return destination;
    }

    private void EnsureNoReparsePoints(string destination, string relativePath)
    {
        var current = destination;
        while (current.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetAttributes(current, out var attributes) &&
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new HistoryPathException(
                    $"History path traverses a reparse point: {relativePath}");
            }

            if (string.Equals(current, _root, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = parent;
        }
    }

    private static bool IsReservedSegment(string segment)
    {
        var windowsCanonical = segment.TrimEnd(' ', '.');
        return windowsCanonical.Length == 0 ||
               string.Equals(windowsCanonical, ".git", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(segment, ".", StringComparison.Ordinal) ||
               string.Equals(segment, "..", StringComparison.Ordinal);
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private void EnsureInitialized()
    {
        if (!Repository.IsValid(_root))
        {
            throw new InvalidOperationException("Managed history has not been initialized.");
        }
    }

    private static Signature SignatureNow() =>
        new(
            OrchestratorSignature.Name,
            OrchestratorSignature.Email,
            DateTimeOffset.UtcNow);

    private static string BuildMessage(HistoryCommitMetadata metadata)
    {
        var summary = string.IsNullOrWhiteSpace(metadata.Summary)
            ? "verified managed change"
            : metadata.Summary.Trim().Replace('\r', ' ').Replace('\n', ' ');
        var builder = new StringBuilder()
            .Append("rev ").Append(metadata.Revision).Append(": ").AppendLine(summary)
            .AppendLine()
            .Append("Project-Id: ").AppendLine(metadata.ProjectId.ToString("D"))
            .Append("Session-Id: ").AppendLine(metadata.SessionId.ToString("D"))
            .Append("Task-Id: ").AppendLine(metadata.TaskId.ToString("D"))
            .Append("Snapshot: ").AppendLine(metadata.SnapshotId)
            .Append("ChangeSet: ").AppendLine(metadata.ChangeSetHash)
            .Append("Model-Profile: ").Append(metadata.ModelProfile);
        return builder.ToString();
    }

    private sealed record ResolvedHistoryFile(
        string Destination,
        ReadOnlyMemory<byte> Content);
}
