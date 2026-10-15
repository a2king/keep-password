using KeepPassword.App.Services;

namespace KeepPassword.App.Tests;

public class MotionPreferencesTests
{
    [Theory]
    [InlineData("Full", false, true)]
    [InlineData("Full", true, true)]
    [InlineData("System", false, false)]
    [InlineData("System", true, true)]
    [InlineData("Reduced", false, false)]
    [InlineData("Reduced", true, false)]
    public void AnimationPreference_ResolvesSystemAnimationsExplicitly(string mode, bool systemEnabled, bool expected)
    {
        Assert.Equal(expected, MotionPreferences.ResolveEnabled(mode, systemEnabled));
    }
}
