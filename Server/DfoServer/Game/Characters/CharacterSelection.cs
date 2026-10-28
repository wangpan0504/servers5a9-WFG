using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Characters;

internal static class CharacterSelection
{
    public static Character Prepare(ICharacterRepository repository, AccountOwners.Lease owner, Character character,
        TownCatalog towns, IReadOnlySet<int> completed, GoldLimitCatalog? goldLimits, bool resumeTown = false,
        ItemCatalog? items = null, TimeProvider? clock = null, ChannelEntry? channel = null) => owner.Execute(() =>
    {
        if (resumeTown && towns.IsGuildHall(character.Position, completed))
        {
            var admitted = repository is Game.Guild.IGuildHallStore hall
                ? hall.SaveGuildHallPosition(owner, character, character.Position, character.Return) : null;
            if (admitted is null) resumeTown = false;
            else character = admitted;
        }
        if (character.IsPvpPrivate)
        {
            // 专用角色没有普通城镇入口；重登仍使用决斗地区，不能先落共享赛丽亚房间。
            if (channel?.Type is not (8 or 24)) throw new InvalidDataException("专用角色不能在非决斗频道选角");
            var decision=ChannelAreaLanding.DecideArea(towns,10,0,character.Level,completed);
            var landing=decision.Spot ?? throw new InvalidDataException("专用角色决斗落点不可用");
            if (resumeTown && character.Position.Town==10 && towns.Areas.TryGetValue((10,character.Position.Area),out var pvpArea) &&
                pvpArea.Map(completed).Error is null) landing=character.Position;
            if(character.Position!=landing || character.Return!=landing)
                character=repository.Save(owner,character,landing,landing);
        }
        else if (resumeTown)
        {
            // 换频道保留已经提交的城镇快照，不借重连传送到新区域，也不覆盖原返回地区。
            if (!towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area)
                || area.Map(completed).Error is not null)
                throw new InvalidDataException("频道重连的城镇地图不可用");
            // 旧存档可能把战舰街区记成返回地区；共享房间的 1379 与出门都取它，按初始地区修复，位置保持不变。
            if (character.Return is { Town: ChannelAreaLanding.AntonTown })
                character = repository.Save(owner, character, character.Position,
                    towns.ResolveRoomReturn(towns.Room, null, character.Level, completed));
        }
        // 只有安徒恩（客户端 03B87FE0 起始地点 19/0）直接落频道地区；其余频道出生赛丽亚房间，出门由 TownTravel.LeaveRoom 按频道落点。
        else if (channel is not null && ChannelAreaLanding.LandsInChannelTown(channel) &&
                 ChannelAreaLanding.Place(towns, channel, character.Level, completed) is { } landing)
        {
            // 战舰是频道起始城镇，不是普通城镇的一站：落点沿用进入前的返回地区，绝不把 19/1 记成房间返回地区，
            // 否则普通频道房间的 1379 与出门会落到战舰（#740 回归）。旧存档里的战舰返回地区按共享房间初始地区修复。
            var back = towns.ResolveRoomReturn(character.Position, OrdinaryDistrict(character.Return), character.Level, completed);
            if (back.Town == ChannelAreaLanding.AntonTown)
                back = towns.ResolveRoomReturn(towns.Room, null, character.Level, completed);
            if (character.Position != landing || character.Return != back)
                character = repository.Save(owner, character, landing, back);
        }
        else
        {
            var back = towns.ResolveRoomReturn(character.Position, OrdinaryDistrict(character.Return), character.Level, completed);
            if (back.Town == ChannelAreaLanding.AntonTown)
                back = towns.ResolveRoomReturn(towns.Room, null, character.Level, completed);
            if (character.Position != towns.Room || character.Return != back)
                character = repository.Save(owner, character, towns.Room, back);
        }
        // 每一步使用上一笔实际提交的快照；无变化不写库，后续失败不重放已提交的清理。
        var talismanCompatibility = InventoryTalismanCompatibility.Plan(character, items);
        // 空间不足只延后迁移，旧分类与原位置都不改；不能为新增玩法阻止已有角色登录。
        if (talismanCompatibility.Accepted)
            character = InventoryService.Commit(repository, owner, character, talismanCompatibility);
        character = InventoryService.Commit(repository, owner, character,
            InventoryExpiryService.RepairZeroPeriod(character.Inventory, items));
        character = InventoryService.Commit(repository, owner, character,
            InventoryExpiryService.Plan(character.Inventory, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()));
        if (!character.Inventory.ConsumableStates.IsEmpty)
        {
            var restored = ConsumableStatePolicy.CrossBoundary(character.Inventory,
                DfoServer.Game.Inventory.Models.ConsumableMaintenance.Reselect, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds());
            if (restored != character.Inventory)
                character = InventoryService.Commit(repository, owner, character, new(true, 0,
                    new(character.Inventory, restored, "consumable_reselect", new(character))));
        }
        if (goldLimits is not null)
            character = InventoryService.Commit(repository, owner, character, new CurrencyService(goldLimits).Initialize(character));
        return character;

        // 共享房间的返回地区只能是普通地区：频道起始城镇（安徒恩战舰 19）的街区不参与，旧存档按初始地区修复。
        Location? OrdinaryDistrict(Location? saved) => saved is { Town: ChannelAreaLanding.AntonTown } ? null : saved;
    });
}
