using Eisenberg.Data;
using Xunit;

namespace Eisenberg.Tests;

public class ContentUnderstandingRulesTests
{
    [Fact]
    public void Import_value_wins_over_user_and_global()
    {
        Assert.False(ContentUnderstandingRules.IsEnabled(true, true, false));
        Assert.True(ContentUnderstandingRules.IsEnabled(false, false, true));
    }

    [Fact]
    public void User_value_wins_when_the_import_has_none()
    {
        Assert.False(ContentUnderstandingRules.IsEnabled(true, false, null));
        Assert.True(ContentUnderstandingRules.IsEnabled(false, true, null));
    }

    [Fact]
    public void Global_value_is_used_when_both_overrides_are_empty()
    {
        Assert.True(ContentUnderstandingRules.IsEnabled(true, null, null));
        Assert.False(ContentUnderstandingRules.IsEnabled(false, null, null));
    }
}
