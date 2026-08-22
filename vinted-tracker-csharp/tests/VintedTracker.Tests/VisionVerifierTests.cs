using VintedTracker;
using Xunit;

namespace VintedTracker.Tests;

public class VisionVerifierTests
{
    [Fact]
    public void PromptContainsGameAndListingTitle()
    {
        var p = VisionVerifier.BuildPrompt("Metroid Dread", "Metroid keychain okazja");
        Assert.Contains("Metroid Dread", p);
        Assert.Contains("Metroid keychain okazja", p);
        Assert.Contains("is_match", p);
    }

    [Fact]
    public void ParsesCleanJson()
    {
        var v = VisionVerifier.ParseVerdict(
            """{"is_match": true, "complete": false, "note": "gra bez pudełka"}""");
        Assert.NotNull(v);
        Assert.True(v!.IsMatch);
        Assert.False(v.Complete);
        Assert.Equal("gra bez pudełka", v.Note);
    }

    [Fact]
    public void ParsesJsonWrappedInProse()
    {
        var v = VisionVerifier.ParseVerdict(
            "Oto ocena:\n```json\n{\"is_match\": false, \"complete\": false, \"note\": \"to brelok\"}\n```");
        Assert.NotNull(v);
        Assert.False(v!.IsMatch);
        Assert.Equal("to brelok", v.Note);
    }

    [Fact]
    public void GarbageReturnsNull()
    {
        Assert.Null(VisionVerifier.ParseVerdict("nie umiem ocenić"));
        Assert.Null(VisionVerifier.ParseVerdict("{złamany json"));
    }

    [Fact]
    public void DisabledWithoutApiKey()
    {
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Assert.False(new VisionVerifier("claude-opus-5").Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
        }
    }
}
