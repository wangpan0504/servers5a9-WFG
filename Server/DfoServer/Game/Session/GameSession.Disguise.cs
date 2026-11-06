using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record ConsumableDisguiseChange(Character Character, long Epoch, SessionPhase Phase);

internal sealed partial class GameSession
{
    sealed record DisguiseAuthorization(ConsumableDisguiseDefinition Definition, int ItemId, long Epoch, long UntilMilliseconds);
    DisguiseAuthorization? pendingDisguise;

    public ConsumableDisguiseChange? ApplyConsumableDisguise(byte type, int formId, int itemId, long now, bool blocked = false) => owner.Execute(() =>
    {
        if (blocked || !CanUseConsumable() || Phase != SessionPhase.Town || Selected is not { } character ||
            pendingDisguise is not { } pending || pending.Epoch != SelectionEpoch || pending.UntilMilliseconds <= now ||
            pending.ItemId != itemId || pending.Definition.Type != 2 || type != 2 || !pending.Definition.Allows(formId) ||
            character.Disguise?.Active(now) == true)
            return null;
        // monster在211和Actor中的类型均为2；Actor类型1沿NPC资源链，不能用于怪物形态。
        pendingDisguise = null;
        Selected = character with { Disguise = new(Guid.NewGuid(), pending.Definition.Type,
            formId, pending.ItemId, pending.UntilMilliseconds) };
        return new ConsumableDisguiseChange(Selected, SelectionEpoch, Phase);
    });

    public ConsumableDisguiseChange? ApplyUnlimitedRandomDisguise(byte type, int formId, int itemId,
        InventoryConsumableUseService service, bool blocked = false)
    {
        // 顶层unlimited直接执行本地随机效果并发211，不经过44成功消费者。
        if (!service.TryDefinition(itemId, out var definition) || !definition.Unlimited ||
            definition.Disguise is not { Type: 2 } disguise || disguise.Forms.IsEmpty ||
            type != 2 || !disguise.Allows(formId)) return null;
        return owner.Execute(() =>
        {
            long now = service.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            // 普通44未兑现资格没有形态计时任务，不能让已过期字段误拒首个直接211。
            if (pendingDisguise?.UntilMilliseconds <= now) pendingDisguise = null;
            if (blocked || !CanUseConsumable() || pendingConsumableUse is not null || pendingDisguise is not null ||
                Phase != SessionPhase.Town || Selected is not { } character || character.Disguise?.Active(now) == true ||
                definition.RequiresDungeon || definition.SummonsApc)
                return null;
            var previous = usedConsumables.GetValueOrDefault(definition.CoolGroup);
            if (previous is { EnforceCooldown: true } &&
                previous.Clock.GetElapsedTime(previous.Timestamp) < TimeSpan.FromMilliseconds(definition.CoolTime) ||
                character.Inventory.ConsumableStates.Any(row => row.Kind == ConsumableStateKind.Cooldown &&
                    row.CoolGroup == definition.CoolGroup && row.Active(now)))
                return null;
            // 211没有源槽；在同一owner门内复用Plan的UID、主背包、锁、期限及等级校验。
            var source = character.Inventory.Items.FirstOrDefault(row => row.Core.ItemId == itemId &&
                service.Plan(character, new(row.Position, 0, itemId, 0), now / 1000).Accepted);
            now = service.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (source is null || source.Core.IsExpired(now / 1000)) return null;
            usedConsumables[definition.CoolGroup] = new(null, source.Uid, service.Clock.GetTimestamp(), definition.CoolTime, service.Clock)
                { Maintenance = definition.CooltimeMaintenance };
            Selected = character with { Disguise = new(Guid.NewGuid(), disguise.Type, formId, itemId,
                checked(now + disguise.Seconds * 1000L)) };
            return new ConsumableDisguiseChange(Selected, SelectionEpoch, Phase);
        });
    }

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
