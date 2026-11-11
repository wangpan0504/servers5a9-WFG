using DfoServer.Game.Dungeons;
using DfoServer.Game.PcRoom;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    int fatigueAccelerationPercent;
    (ushort Character, long Epoch, bool Selected)? fatigueAccelerationChoice;

    // 比例由同一版网吧资源显式装配，未装配的测试/服务不凭空增加经验。
    public void ConfigureFatigueAcceleration(int percent)
    {
        if (percent < 0) throw new InvalidDataException("疲劳燃烧经验比例不能为负");
        fatigueAccelerationPercent = percent;
    }

    bool ReadFatigueAccelerationChoice()
    {
        if (Selected is not { } character) return false;
        if (fatigueAccelerationChoice is not { } saved || saved.Character != character.Id || saved.Epoch != SelectionEpoch)
        {
            bool value = repository is IFatigueAccelerationStore store && store.ReadFatigueAcceleration(owner, character.Id);
            fatigueAccelerationChoice = (character.Id, SelectionEpoch, value);
        }
        return fatigueAccelerationChoice.Value.Selected;
    }

    int AvailableFatigueAccelerationPercent()
    {
        int percent = PremiumPcRoom ? fatigueAccelerationPercent : 0;
        if (Selected is { } character && debugSettings?.AdventurerBenefits is { Catalog.Lucky: { } lucky } service &&
            service.IsLucky(character.Job, character.GrowType, (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()))
            percent = Math.Max(percent, lucky.FatigueAccelerationPercent);
        return percent;
    }

    public FatigueAccelerationReceipt? FatigueAccelerationState() => owner.Execute(() =>
        Selected is { } character ? new FatigueAccelerationReceipt(character.Id, owner.Generation, SelectionEpoch, Phase,
            ReadFatigueAccelerationChoice(), AvailableFatigueAccelerationPercent() > 0 && ReadFatigueAccelerationChoice()) : null);

    public FatigueAccelerationReceipt? ChangeFatigueAcceleration(bool enabled, bool blocked = false) => owner.Execute(() =>
    {
        if (Selected is not { } character) return null;
        var current = FatigueAccelerationState()!;
        if (blocked || !Ready || Phase != SessionPhase.Town || pendingRental is not null || HasPendingTownQuestCommit || HasPendingFormalCard)
            return current with { Error = 19 };
        if (enabled && (AvailableFatigueAccelerationPercent() <= 0 || repository is not IFatigueAccelerationStore))
            return current with { Error = 179 };
        if (current.Selected != enabled)
        {
            if (repository is not IFatigueAccelerationStore store) return current with { Error = 179 };
            store.SaveFatigueAcceleration(owner, character.Id, enabled);
            fatigueAccelerationChoice = (character.Id, SelectionEpoch, enabled);
        }
        return current with { Selected = enabled, Active = enabled && AvailableFatigueAccelerationPercent() > 0 };
    });

    public bool CanPublishFatigueAcceleration(FatigueAccelerationReceipt receipt, bool requireCurrentState = false) => owner.Execute(() =>
        owner.Generation == receipt.Generation && SelectionEpoch == receipt.Epoch && Selected?.Id == receipt.CharacterId &&
        Phase == receipt.Phase && (!requireCurrentState || receipt.Selected == ReadFatigueAccelerationChoice() &&
            receipt.Active == (AvailableFatigueAccelerationPercent() > 0 && ReadFatigueAccelerationChoice())));

    int CaptureFatigueAcceleration(DungeonEntryPlan entry) =>
        FatigueAccelerationRules.Eligible(entry) && guildDungeons?.Find(entry.DungeonId) is null &&
        ReadFatigueAccelerationChoice() ? AvailableFatigueAccelerationPercent() : 0;
}
