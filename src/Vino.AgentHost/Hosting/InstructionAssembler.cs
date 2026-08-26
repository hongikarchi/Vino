using System.Text;

namespace Vino.AgentHost.Hosting;

/// <summary>
/// Assembles the layered thread instructions every Codex thread receives:
/// static base contract → plugin house rules (identical for every project, machine,
/// and user) → built-in skills index → per-project rules and memory. Layered so the
/// plugin's conventions travel with the install while projects stay customizable.
/// </summary>
public sealed class InstructionAssembler : IThreadInstructionComposer
{
    private readonly ProjectContextStore _projectContext;
    private readonly SkillLibrary _skills;

    public InstructionAssembler(ProjectContextStore projectContext, SkillLibrary skills)
    {
        _projectContext = projectContext;
        _skills = skills;
    }

    public string Compose(string baseInstructions)
    {
        var builder = new StringBuilder(baseInstructions);
        builder.Append("\n\n## Vino house rules\n").Append(HouseRules.Text);
        var index = _skills.IndexText();
        if (index.Length > 0)
        {
            builder.Append("\n\n## Built-in skills (fetch with skill_read; bodies are not in context)\n");
            builder.Append(index);
        }
        return _projectContext.Compose(builder.ToString());
    }
}

/// <summary>
/// Plugin-level modeling conventions — the performance and quality baseline of Vino rather
/// than per-project preference (those belong in rules.md). The authoritative text ships as
/// assets/instructions/house-rules.md so prompt tuning is a file edit, not a rebuild; the
/// build embeds the same file into this assembly as the fallback for a broken install.
/// </summary>
public static class HouseRules
{
    public static string Text { get; } = InstructionAssets.LoadOrFallback("house-rules.md");
}
