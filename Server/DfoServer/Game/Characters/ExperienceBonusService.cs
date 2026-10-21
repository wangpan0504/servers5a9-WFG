using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using System.Collections.Immutable;

namespace DfoServer.Game.Characters;

internal sealed class ExperienceBonusService(ItemCatalog items, RecoveryConsumableCatalog consumables,
    ExperienceBonusCatalog rules, IExperienceBonusProvider? provider = null)
{
    public ExperienceBonuses Plan(ExperienceBonusRequest request, uint basis, uint rank, GrowthContractBenefits? growth)
    {
        var context = provider?.Capture(request) ?? ExperienceBonusContext.Empty;
        if (context.Rates.IsDefault || context.PartyMembers is < 1 or > 4)
            throw new InvalidDataException("经验加成上下文无效");
        if (context.Rates.Any(rate => rate.Source is ExperienceBonusSource.GrowthContract or ExperienceBonusSource.Potion or
            ExperienceBonusSource.QuestPotion or ExperienceBonusSource.BlackDiamond or ExperienceBonusSource.InternetCafe or
            ExperienceBonusSource.Equipment or ExperienceBonusSource.Avatar or ExperienceBonusSource.Creature))
            throw new InvalidDataException("外部系统不能重复提供已有owner的经验资格");
        var rates = context.Rates.ToList();
        var character = request.Character;
        long now = request.UnixSeconds;
        if (GrowthContractPolicy.Active(character, growth, now))
            rates.Add(new(ExperienceBonusSource.GrowthContract, growth!.ExperiencePercent));
        var equipped = character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Equipment &&
            !item.IsExpired(now)).ToArray();
        if (request.Event == ExperienceEvent.Kill)
        {
            decimal percent = 0;
            foreach (var item in equipped)
            {
                var template = items.Get(item.Core.ItemId);
                if (character.Level >= template.MinimumLevel && character.Level <= template.MaximumLevel &&
                    (template.Jobs & (1 << character.Job)) != 0 &&
                    !template.ExperienceForbiddenModules.Contains(request.EquipmentModule) &&
                    Game.Inventory.Services.InventoryMoveService.RequiredSkillSatisfied(character, template))
                    percent += template.ExperiencePercent;
            }
            if (percent != 0) rates.Add(new(ExperienceBonusSource.Equipment, percent));
        }
        if (request.Event == ExperienceEvent.Clear)
        {
            if (character.BlackDiamondExpiresAt > now)
                rates.Add(new(ExperienceBonusSource.BlackDiamond, rules.BlackDiamondPercent));
            if (request.PremiumPcRoom)
                rates.Add(new(ExperienceBonusSource.InternetCafe, rules.InternetCafePercent));
            if (equipped.Any(item => item.Core.Kind == ItemKind.Avatar) || context.PartyHasAvatar)
                rates.Add(new(ExperienceBonusSource.Avatar, context.PartyMembers == 1 ? 2 : 5));
            if (equipped.Any(item => item.Position.Index == 26 && item.Core.Kind == ItemKind.Creature &&
                item.Creature is not null && !items.Get(item.Core.ItemId).CreatureEgg))
                rates.Add(new(ExperienceBonusSource.Creature, 5));
        }
        if (request.Event == ExperienceEvent.Quest)
        {
            // 同族任务药剂不叠加，取仍有效的最大比例；时限与角色归属沿原消耗品账本。
            decimal percent = 0;
            foreach (var state in character.Inventory.ConsumableStates)
                if (state.Kind == ConsumableStateKind.Effect && state.Active(request.UnixMilliseconds > 0 ? request.UnixMilliseconds : checked(now * 1000)) &&
                    consumables.TryGet(state.ItemId, out var definition))
                    percent = Math.Max(percent, definition.QuestExpBonusPermille / 10m);
            if (percent != 0) rates.Add(new(ExperienceBonusSource.QuestPotion, percent));
        }
        var result = ExperienceBonuses.Calculate(checked(basis + rank), basis, rates);
        // 本版经验秘药同时作用击杀和含评分的通关经验。
        if (request.Event != ExperienceEvent.Quest && ExpBonusPolicy.Bonus(character, checked(basis + rank), now) is > 0 and var potion)
        {
            if (result.Parts.Any(part => part.Source == ExperienceBonusSource.Potion))
                throw new InvalidDataException("经验秘药不能由外部资格重复提供");
            result = result with { Parts = result.Parts.Add(new(ExperienceBonusSource.Potion, potion))
                .OrderBy(part => part.Source).ToImmutableArray() };
            _ = result.Total;
        }
        return result;
    }
}
