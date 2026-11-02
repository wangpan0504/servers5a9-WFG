namespace DfoServer.Game.Dungeons;

internal sealed class HostedDungeonEligibilityCoordinator(
    HostedDungeonEligibilityOptions options, Action<string, object>? writeLog = null)
{
    public HostedDungeonEligibilityOptions Options { get; } = options;

    public async Task<HostedDungeonEligibilityResult> RunAsync(
        HostedDungeonEligibilityCapture capture, CancellationToken cancellation)
    {
        if (!Options.Enabled)
            return HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonDisabled, capture.ProbeId);
        if (capture.RejectReason is { } rejected)
            return HostedDungeonEligibilityRules.Refuse(rejected, capture.ProbeId);
        if (string.IsNullOrWhiteSpace(Options.HostFileName))
            return HostedDungeonEligibilityRules.Refuse(HostedDungeonEligibilityRules.ReasonMissingCombatProfile, capture.ProbeId);
        return await HostedDungeonEligibilityProcess.RunAsync(Options, HostedDungeonEligibilityRules.Request(capture), cancellation);
    }

    public void Write(string kind, object detail) => writeLog?.Invoke(kind, detail);
}
