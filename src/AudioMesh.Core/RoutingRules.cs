namespace AudioMesh.Core;

/// <summary>Pure routing decision logic; audio-server operations belong in platform backends.</summary>
public static class RoutingRules
{
    public static AppRoutingRule? Match(IEnumerable<AppRoutingRule> rules, string executable, string application)
    {
        return rules.FirstOrDefault(rule =>
            rule.Enabled && !string.IsNullOrWhiteSpace(rule.SinkId) &&
            !string.IsNullOrWhiteSpace(rule.Application) &&
            (string.Equals(rule.Application, executable, StringComparison.Ordinal) ||
             string.Equals(rule.Application, application, StringComparison.Ordinal)));
    }

    /// <summary>False means the sink is already correct, missing, or manually overridden.</summary>
    public static bool ShouldMove(AppRoutingRule? rule, string? currentSinkIndex,
        IReadOnlyDictionary<string, string> sinkIndices, bool manuallyOverridden)
    {
        return !manuallyOverridden && rule is not null &&
               sinkIndices.TryGetValue(rule.SinkId, out var intendedIndex) &&
               !string.Equals(currentSinkIndex, intendedIndex, StringComparison.Ordinal);
    }
}
