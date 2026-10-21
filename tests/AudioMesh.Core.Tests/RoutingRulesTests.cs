using AudioMesh.Core;
using Xunit;

namespace AudioMesh.Core.Tests;

public sealed class RoutingRulesTests
{
    [Fact]
    public void DefaultProfile_ContainsIndependentNamedChannels()
    {
        var profile = AudioProfile.Default();
        Assert.Equal(new[] { "Game", "Chat", "Media", "Aux", "Mic" },
            profile.Buses.Select(bus => bus.Name));
        Assert.Equal(5, profile.Buses.Select(bus => bus.Id).Distinct().Count());
    }

    [Fact]
    public void Match_UsesExecutableOrApplicationName()
    {
        var rules = new[] { new AppRoutingRule { Application = "firefox", SinkId = "audiomesh_Browser" } };
        Assert.Equal("audiomesh_Browser", RoutingRules.Match(rules, "firefox", "Firefox")?.SinkId);
        Assert.Equal("audiomesh_Browser", RoutingRules.Match(rules, "", "firefox")?.SinkId);
        Assert.Null(RoutingRules.Match(rules, "other", "Other"));
    }

    [Fact]
    public void Match_DoesNotUseDisabledOrEmptyRules()
    {
        var rules = new[]
        {
            new AppRoutingRule { Application = "firefox", SinkId = "audiomesh_Chat", Enabled = false },
            new AppRoutingRule { Application = "firefox", SinkId = "audiomesh_Browser" }
        };
        Assert.Equal("audiomesh_Browser", RoutingRules.Match(rules, "firefox", "Firefox")?.SinkId);
        Assert.Null(RoutingRules.Match(new[] { new AppRoutingRule { Application = "firefox" } }, "firefox", ""));
    }

    [Fact]
    public void ShouldMove_RespectsManualOverridesAndAlreadyCorrectSink()
    {
        var rule = new AppRoutingRule { Application = "firefox", SinkId = "audiomesh_Browser" };
        var sinks = new Dictionary<string, string> { ["audiomesh_Browser"] = "42" };
        Assert.True(RoutingRules.ShouldMove(rule, "5", sinks, false));
        Assert.False(RoutingRules.ShouldMove(rule, "42", sinks, false));
        Assert.False(RoutingRules.ShouldMove(rule, "5", sinks, true));
        Assert.False(RoutingRules.ShouldMove(rule, "5", new Dictionary<string, string>(), false));
        Assert.False(RoutingRules.ShouldMove(null, "5", sinks, false));
    }
}
