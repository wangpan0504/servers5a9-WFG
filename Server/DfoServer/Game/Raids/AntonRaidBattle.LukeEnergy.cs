using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly bool lukeEnergyObjects;

    bool ValidLukeClearSource(RaidDungeonRun run, RaidActor actor, DungeonClearFact fact)
    {
        if (fact.PassiveSource is not { } source)
            return run.Objects?.Energy is null && fact.ConfirmedDeathEvents.Count > 0;
        if (!lukeEnergyObjects || run.Phase != 1 || run.EnergyRoom is not { IsEnergy: true } binding ||
            binding != run.Objects?.Energy || source.OperationId == Guid.Empty || source.TemplateId != binding.ObjectId ||
            source.SourceOrdinal != binding.Ordinal || fact.ClearMap is not { } map ||
            binding.Room.Dungeon != run.Dungeon || binding.Room.Map != map.Id || binding.Room.Kind != map.Kind ||
            binding.Room.Position != map.Position || binding.Room.Path != map.Path ||
            !run.MapEpochs.TryGetValue(actor, out long epoch) || source.Identity.Epoch != epoch ||
            source.Identity.Epoch == long.MaxValue || fact.Identity != source.Identity with { Epoch = source.Identity.Epoch + 1 })
            return false;
        return true;
    }
}
