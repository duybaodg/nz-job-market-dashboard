namespace Domain.Entities;

public sealed class JobSkill
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Job? Job { get; set; }
    public string SkillName { get; set; } = string.Empty;
    public string? SkillType { get; set; }
    public decimal? Confidence { get; set; }
}
