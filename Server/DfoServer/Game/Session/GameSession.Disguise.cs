using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record ConsumableDisguiseChange(Character Character, long Epoch, SessionPhase Phase);

internal sealed partial class GameSession
{
    sealed record DisguiseAuthorization(ConsumableDisguiseDefinition Definition, int ItemId, long Epoch, long UntilMilliseconds);
    DisguiseAuthorization? pendingDisguise;

    public ConsumableDisguiseChange? ApplyConsumableDisguise(byte type, int seconds, int itemId, long now, bool blocked = false) => owner.Execute(() =>
    {
        if (blocked || !CanUseConsumable() || Phase != SessionPhase.Town || Selected is not { } character ||
            pendingDisguise is not { } pending || pending.Epoch != SelectionEpoch || pending.UntilMilliseconds <= now ||
            pending.ItemId != itemId || pending.Definition.Type != type || pending.Definition.Seconds != seconds ||
            character.Disguise?.Active(now) == true)
            return null;
        // 211仅兑现已经通过44原子扣药的一次资格，不信任请求中的时长和源模板，也不延长原截止。
        pendingDisguise = null;
        Selected = character with { Disguise = new(Guid.NewGuid(), pending.Definition.Type,
            pending.Definition.FormId, pending.ItemId, pending.UntilMilliseconds) };
        return new ConsumableDisguiseChange(Selected, SelectionEpoch, Phase);
    });

    public ConsumableDisguiseChange? CancelConsumableDisguise() => owner.Execute(() =>
    {
        // 取消同时撤销未兑现资格，迟到的211不能重新激活已取消的形态。
        pendingDisguise = null;
        if (!Ready || Selected is not { Disguise: not null } character) return null;
        Selected = character with { Disguise = null };
        return new ConsumableDisguiseChange(Selected, SelectionEpoch, Phase);
    });

    public ConsumableDisguiseChange? RefreshConsumableDisguise(long now) => owner.Execute(() =>
    {
        if (!Ready || Phase != SessionPhase.Town || pendingDisguise?.UntilMilliseconds <= now)
            pendingDisguise = null;
        if (Selected is not { Disguise: { } form } character || Ready && Phase == SessionPhase.Town && form.Active(now))
            return null;
        Selected = character with { Disguise = null };
        return new ConsumableDisguiseChange(Selected, SelectionEpoch, Phase);
    });

    public (long Epoch, ConsumableDisguiseAppearance Appearance)? CurrentConsumableDisguise() => owner.Execute(() =>
        Ready && Phase == SessionPhase.Town && Selected?.Disguise is { } form
            ? ((long Epoch, ConsumableDisguiseAppearance Appearance)?)(SelectionEpoch, form) : null);

    public bool CanPublishConsumableDisguise(ConsumableDisguiseChange change) => owner.Execute(() => Ready &&
        SelectionEpoch == change.Epoch && Phase == change.Phase && ReferenceEquals(Selected, change.Character));
}
