using System.Text;
using System.Text.RegularExpressions;

namespace GPTino.AgentHost.Hosting;

/// <summary>
/// Groups a document's layer names by what they actually share, so a scheme draft can be proposed
/// from the USER's file instead of from a vocabulary we shipped. Deterministic and Rhino-free:
/// this reports facts (which names share a parent, a mark family, a token, a Korean substring),
/// and naming those groups stays a judgement for the model and, finally, the user.
///
/// The goal is a draft worth correcting, not a classification worth trusting: coverage and
/// easy bulk editing beat precision, and a name this cannot place honestly stays ungrouped
/// rather than being forced into the nearest bucket.
///
/// Korean is why substrings exist here. Compounds carry no separator — 외벽 (exterior wall) does
/// not split into 외 + 벽 — so token splitting alone finds nothing shared between 외벽-콘크리트
/// and 콘크리트 벽, which any person reads as the same family.
/// </summary>
public static class LayerNameAnalyzer
{
    /// <summary>Group kinds, most specific first — this order also breaks scoring ties.</summary>
    public const string KindMarkFamily = "markFamily";
    public const string KindParent = "parent";
    public const string KindToken = "token";
    public const string KindSubstring = "substring";

    private static readonly string[] KindPriority = [KindMarkFamily, KindParent, KindToken, KindSubstring];

    // "SC1", "SG-3", "W 12" -> mark family SC / SG / W. Two or three letters is the convention in
    // the structural drawings this was measured against; a longer run is a word, not a mark.
    private static readonly Regex MarkFamily = new(
        @"^(?<mark>\p{L}{1,3})[-_ ]?\d+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static readonly Regex TokenSplit = new(
        @"[\s\-_./()\[\]]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>Longest Korean substring worth proposing as a group key (재료명 대부분이 2~4자).</summary>
    private const int MaximumSubstringLength = 4;

    public static LayerNameAnalysis Analyze(
        IReadOnlyList<string> layerFullPaths,
        LayerAliasMatcher? hints = null)
    {
        ArgumentNullException.ThrowIfNull(layerFullPaths);
        var layers = layerFullPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => new LayerEntry(path, LeafOf(path), ParentOf(path)))
            .ToArray();
        if (layers.Length == 0)
        {
            return new LayerNameAnalysis(0, [], []);
        }

        // key -> the layers it covers, per kind. A layer can appear under several keys; the
        // assignment pass below picks one so the draft reads as a partition, not a tag cloud.
        var candidates = new Dictionary<(string Kind, string Key), SortedSet<string>>();
        void Add(string kind, string key, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!candidates.TryGetValue((kind, key), out var members))
            {
                candidates[(kind, key)] = members = new SortedSet<string>(StringComparer.Ordinal);
            }
            members.Add(fullPath);
        }

        foreach (var layer in layers)
        {
            if (layer.Parent is { Length: > 0 })
            {
                Add(KindParent, layer.Parent, layer.FullPath);
            }
            var mark = MarkFamily.Match(layer.Leaf);
            if (mark.Success)
            {
                Add(KindMarkFamily, mark.Groups["mark"].Value.ToUpperInvariant(), layer.FullPath);
            }
            foreach (var token in TokenSplit.Split(layer.Leaf))
            {
                if (token.Length < 2 || ContainsHangul(token)) continue;
                // A bare number is not a family, and a mark's digits are already covered above.
                if (token.All(char.IsDigit)) continue;
                Add(KindToken, token.ToUpperInvariant(), layer.FullPath);
            }
            foreach (var substring in HangulSubstrings(layer.Leaf))
            {
                Add(KindSubstring, substring, layer.FullPath);
            }
        }

        // A key shared by a single layer describes nothing; it would just be the layer's own name.
        var shared = candidates
            .Where(entry => entry.Value.Count >= 2)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        // Drop a key that covers exactly the same layers as a longer key of the same kind:
        // "콘크리" and "콘크리트" over the same members are one group, and the longer one is the
        // one a person would recognise.
        var redundant = new HashSet<(string, string)>();
        foreach (var (candidate, members) in shared)
        {
            foreach (var (other, otherMembers) in shared)
            {
                if (candidate.Equals(other) || candidate.Kind != other.Kind) continue;
                if (other.Key.Length > candidate.Key.Length &&
                    other.Key.Contains(candidate.Key, StringComparison.Ordinal) &&
                    members.SetEquals(otherMembers))
                {
                    redundant.Add(candidate);
                    break;
                }
            }
        }

        // Specificity is a hard precedence, not a numeric tug-of-war: a mark family claims its
        // members before the parent layer that contains them, so a broad parent can never swallow
        // the SC/SG distinction just by being bigger. Within a kind, bigger and longer keys win.
        var ranked = shared
            .Where(entry => !redundant.Contains(entry.Key))
            .OrderBy(entry => Array.IndexOf(KindPriority, entry.Key.Kind))
            .ThenByDescending(entry => Score(entry.Key.Key, entry.Value.Count))
            .ThenBy(entry => entry.Key.Key, StringComparer.Ordinal)
            .ToArray();

        // Each layer joins its highest-scoring group, so the draft partitions the table. The keys
        // it ALSO matched ride along: that is where a second axis shows up (a name carrying both
        // an element and a material), which the model and the user may want to split later.
        var assignment = new Dictionary<string, (string Kind, string Key)>(StringComparer.Ordinal);
        var alsoMatched = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, members) in ranked)
        {
            foreach (var member in members)
            {
                if (assignment.TryAdd(member, key)) continue;
                if (!alsoMatched.TryGetValue(member, out var others))
                {
                    alsoMatched[member] = others = [];
                }
                others.Add(key.Key);
            }
        }

        var groups = new List<LayerNameGroup>();
        foreach (var (key, _) in ranked)
        {
            var assigned = assignment
                .Where(entry => entry.Value.Equals(key))
                .Select(entry => entry.Key)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            // A group whose members all went somewhere better is not a group any more.
            if (assigned.Length < 2) continue;
            var hint = hints?.Match(key.Key);
            groups.Add(new LayerNameGroup(
                key.Key,
                key.Kind,
                assigned,
                hint?.Canonical,
                hint?.Material));
        }

        var grouped = groups.SelectMany(group => group.Members).ToHashSet(StringComparer.Ordinal);
        var ungrouped = layers
            .Select(layer => layer.FullPath)
            .Where(path => !grouped.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        return new LayerNameAnalysis(
            layers.Length,
            groups,
            ungrouped,
            alsoMatched.ToDictionary(
                entry => entry.Key,
                entry => (IReadOnlyList<string>)entry.Value
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal));
    }

    // Within one kind: bigger groups first, then longer (more specific) keys. Ordering ACROSS
    // kinds is the KindPriority precedence above, chosen because for a draft the user will
    // correct, splitting too finely is the cheaper mistake — merging two groups is one
    // instruction, while re-splitting a group that swallowed a distinction means going back layer
    // by layer, which is the manual work this feature exists to remove.
    private static int Score(string key, int memberCount) =>
        memberCount * 10 + Math.Min(key.Length, 8);

    private static string LeafOf(string fullPath)
    {
        var segments = fullPath.Split("::", StringSplitOptions.RemoveEmptyEntries);
        var leaf = segments.Length > 0 ? segments[^1] : fullPath;
        return leaf.Trim().Normalize(NormalizationForm.FormKC);
    }

    private static string? ParentOf(string fullPath)
    {
        var index = fullPath.LastIndexOf("::", StringComparison.Ordinal);
        return index > 0 ? fullPath[..index].Trim() : null;
    }

    private static bool ContainsHangul(string value) => value.Any(IsHangul);

    // Precomposed syllables plus the Jamo block: enough for layer names, which are syllables.
    private static bool IsHangul(char value) =>
        (value >= '가' && value <= '힣') || (value >= 'ᄀ' && value <= 'ᇿ');

    /// <summary>
    /// Every Hangul substring (1..4 syllables) of the name's Korean runs. Single syllables are
    /// included on purpose — 벽, 보, 문, 창 are whole domain terms — and the "shared by at least
    /// two layers" filter plus longest-key-wins is what keeps the noise out.
    /// </summary>
    private static IEnumerable<string> HangulSubstrings(string leaf)
    {
        var run = new StringBuilder();
        foreach (var character in leaf + "\0")
        {
            if (IsHangul(character))
            {
                run.Append(character);
                continue;
            }
            if (run.Length > 0)
            {
                var text = run.ToString();
                for (var length = 1; length <= Math.Min(MaximumSubstringLength, text.Length); length++)
                {
                    for (var start = 0; start + length <= text.Length; start++)
                    {
                        yield return text.Substring(start, length);
                    }
                }
                run.Clear();
            }
        }
    }

    private sealed record LayerEntry(string FullPath, string Leaf, string? Parent);
}

/// <summary>One proposed grouping of layers, with the evidence that produced it.</summary>
public sealed record LayerNameGroup(
    string Key,
    string Kind,
    IReadOnlyList<string> Members,
    string? HintCanonical,
    string? HintMaterial);

/// <summary>
/// The draft: groups, the layers no rule could place, and the extra keys a layer also matched
/// (a second axis the user may want to separate).
/// </summary>
public sealed record LayerNameAnalysis(
    int LayerCount,
    IReadOnlyList<LayerNameGroup> Groups,
    IReadOnlyList<string> Ungrouped,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? AlsoMatched = null);
