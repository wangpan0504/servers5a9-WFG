using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Inventory.Services;

internal sealed record EquipmentGrowthChange(InventoryItem Before, InventoryItem After, ushort Gain);
internal sealed record EquipmentGrowthPlan(Character Expected, Guid EventId, DungeonRunIdentity Identity,
    InventoryPlan Inventory, ImmutableArray<EquipmentGrowthChange> Changes)
{
    // 本服兼容规则：穿戴的龙跃装备在等级差不超过10级的真实敌人死亡时成长。
    // 普通/精英/领主按资源kind1/2/3，难度按资源百分比；无疲劳额外惩罚，不宣称还原未知原服公式。
    public static EquipmentGrowthPlan? Create(Character character, SinglePlayerDungeonRun run, PreparedDungeonDeath death,
        ItemCatalog items, DateTimeOffset now, TimeProvider? clock = null)
    {
        if (run.Entry.IsTutorial || run.Entry.IsTrainingRoom || run.Entry.DisableMonsterAndClearExperience ||
            death.Monster.AiCharacter is not null || death.Monster.IsFriendly || !death.Monster.CanReportDeath ||
            Math.Abs(character.Level - death.Monster.CandidateLevel) > 10) return null;
        var changes = ImmutableArray.CreateBuilder<EquipmentGrowthChange>();
        var after = character.Inventory;
        foreach (var item in character.Inventory.Items)
        {
            if (item.Position.List != InventoryListType.Equipment || item.Core.Kind != ItemKind.Equipment ||
                item.IsExpired(now.ToUnixTimeSeconds()) || item.Core.IsRental || items.EquipmentGrowth.Get(item.Core.ItemId) is not { } rule ||
                item.Core.Extension90 >= rule.Limit) continue;
            int difficulty = run.Entry.Difficulty;
            int kind = death.Monster.IsBoss ? 3 : death.Monster.RewardType is 1 or 2 ? 2 : 1;
            if (!rule.KindRates.TryGetValue(kind, out int rate) || difficulty >= rule.DifficultyRates.Length) continue;
            long points = (long)rule.Point * rate * rule.DifficultyRates[difficulty] / 10000;
            int gain = (int)Math.Min(points, rule.Limit - item.Core.Extension90);
            if (gain <= 0) continue;
            var core = item.Core with { Extension90 = checked((ushort)(item.Core.Extension90 + gain)) };
            if (core.Extension90 == rule.Limit && rule.Automatic)
            {
                var output = items.Get(rule.Output);
                if (output.Id != rule.Output || output.Kind != ItemKind.Equipment ||
                    output.EquipmentSlot != items.Get(core.ItemId).EquipmentSlot)
                    throw new InvalidDataException("自动成长产物必须是同部位装备");
                core = core with { ItemId = output.Id, Extension90 = 0, Durability = Math.Min(core.Durability, output.Durability) };
            }
            var updated = item with { Core = core };
            after = after with { Items = after.Items.Replace(item, updated) };
            changes.Add(new(item, updated, checked((ushort)gain)));
        }
        long? deadline = changes.Select(change => change.Before.ExpireTime).Where(value => value > 0)
            .Select(value => (long?)value).Min();
        return changes.Count == 0 ? null : new(character, death.Death.EventId, death.Death.Identity,
            new(character.Inventory, after, "equipment_growth", new(character, deadline, clock)), changes.ToImmutable());
    }
}
