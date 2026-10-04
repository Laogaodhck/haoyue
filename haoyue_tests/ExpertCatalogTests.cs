using Haoyue.Runtime.Experts;

namespace Haoyue.Tests;

public sealed class ExpertCatalogTests
{
    [Fact]
    public void Entries_NotEmpty_WithUniqueIds()
    {
        Assert.NotEmpty(ExpertCatalog.Entries);
        Assert.Equal(
            ExpertCatalog.Entries.Count,
            ExpertCatalog.Entries.Select(expert => expert.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Entries_AllFieldsPopulated()
    {
        foreach (var expert in ExpertCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(expert.Name));
            Assert.False(string.IsNullOrWhiteSpace(expert.Title));
            Assert.False(string.IsNullOrWhiteSpace(expert.Avatar));
            Assert.False(string.IsNullOrWhiteSpace(expert.Bio));
            Assert.NotEmpty(expert.Domains);
            Assert.All(expert.Domains, domain => Assert.False(string.IsNullOrWhiteSpace(domain)));
            Assert.NotEmpty(expert.Skills);
            Assert.All(expert.Skills, skill => Assert.False(string.IsNullOrWhiteSpace(skill)));
            Assert.False(string.IsNullOrWhiteSpace(expert.Prompt));
        }
    }

    [Fact]
    public void Entries_IdsUseKebabCase_ForStableUrlAndKeys()
    {
        foreach (var expert in ExpertCatalog.Entries)
        {
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", expert.Id);
        }
    }
}
