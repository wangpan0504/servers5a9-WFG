using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using static DfoServer.Game.Characters.ChannelAreaLanding;

namespace DfoServer.Game.Characters;

internal static class CharacterSelection
{
    public static Character Prepare(ICharacterRepository repository, AccountOwners.Lease owner, Character character,
        TownCatalog towns, IReadOnlySet<int> completed, GoldLimitCatalog? goldLimits, bool resumeTown = false,
        ItemCatalog? items = null, TimeProvider? clock = null, ChannelEntry? channel = null,
        int? previousChannelType = null) => owner.Execute(() =>
    {
        bool leavingRetiredTown = ChannelAreaLanding.LeavesRetiredTown(character.Position);
        bool leavingRaidInstance = ChannelAreaLanding.IsRaidInstanceArea(character.Position);
        // 同城普通/团队入口按来源频道区分；重启丢失频道历史时，保留已经独立记住的普通地区。
        // 普通卢克32/3自身的返回记忆不清除，也不把缺少来源的旧32/3存档一律判成团队地区。
        bool preservesReturn = ChannelAreaLanding.PreservesRoomReturn(towns, character.Position, previousChannelType ?? channel?.Type) ||
            previousChannelType is null && character.Position is { Town: 32, Area: 0 or 3 } &&
            character.Return is { Town: not 32 };
        if (leavingRetiredTown || leavingRaidInstance) resumeTown = false;
        // 专属中央公园的旧位置/返回地区归回正常中央公园；仍由目录核对等级、任务及房间门口。
        // 频道专属街区已有普通返回地区时，从共享房间解析该记忆，不能让当前街区覆盖它。
        // 普雷/漩涡旧存档丢失普通返回地区时使用共享房间默认地区，不从团本街区重建普通出口。
        var returnSource = leavingRetiredTown
            ? new Location(ChannelAreaLanding.CentralParkTown, 1, 0, 0)
            : leavingRaidInstance || character.Position.Town is ChannelAreaLanding.PvpTown or ChannelAreaLanding.XianVillageTown ||
              preservesReturn && (character.Return is not null || character.Position.Town == 32 || IsRaidOnlyTown(character.Position.Town))
                ? towns.Room : character.Position;
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
            if (character.Return != OrdinaryDistrict(character.Return))
                character = repository.Save(owner, character, character.Position,
                    towns.ResolveRoomReturn(towns.Room, OrdinaryDistrict(character.Return), character.Level, completed));
        }
        // 安徒恩、决斗、帝国竞技场、再战魔界及英雄魔界大战直接落专属地区；其余频道仍由共享房间出门。
        else if (channel is not null && ChannelAreaLanding.LandsInChannelTown(channel) &&
                 ChannelAreaLanding.Place(towns, channel, character.Level, completed) is { } landing)
        {
            // 战舰是频道起始城镇，不是普通城镇的一站：落点沿用进入前的返回地区，绝不把 19/1 记成房间返回地区，
            // 否则普通频道房间的 1379 与出门会落到战舰（#740 回归）。旧存档里的战舰返回地区按共享房间初始地区修复。
            var back = towns.ResolveRoomReturn(returnSource, OrdinaryDistrict(character.Return), character.Level, completed);
            if (back.Town == ChannelAreaLanding.AntonTown)
                back = towns.ResolveRoomReturn(towns.Room, null, character.Level, completed);
            if (character.Position != landing || character.Return != back)
                character = repository.Save(owner, character, landing, back);
        }
        else
        {
            var back = towns.ResolveRoomReturn(returnSource, OrdinaryDistrict(character.Return), character.Level, completed);
            if (back.Town == ChannelAreaLanding.AntonTown)
                back = towns.ResolveRoomReturn(towns.Room, null, character.Level, completed);
            if (character.Position != towns.Room || character.Return != back)
                character = repository.Save(owner, character, towns.Room, back);
        }
        // 每一步使用上一笔实际提交的快照；无变化不写库，后续失败不重放已提交的清理。
        var kindCompatibility = InventoryKindCompatibility.Plan(character, items);
        // 空间不足只延后迁移，旧分类与原位置都不改；不能为新增玩法阻止已有角色登录。
        if (kindCompatibility.Accepted)
            character = InventoryService.Commit(repository, owner, character, kindCompatibility);
        // 6 点可能没开服 → 必须有登录/选角扫兜底。A 不追溯：RepairZeroPeriod 不给 daily/weekly 的 ExpireTime=0 补戳。
        character = InventoryService.Commit(repository, owner, character,
            InventoryExpiryService.RepairZeroPeriod(character.Inventory, items));
        character = InventoryService.Commit(repository, owner, character,
            InventoryExpiryService.Plan(character.Inventory, (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()));
        if (items is not null)
            character = InventoryService.Commit(repository, owner, character,
                InventoryCloneAppearanceService.PlanCleanup(character, items));
        if (items is not null && repository is IAccountMaterialStore materials)
            character = materials.ReconcileAccountMaterials(owner, character, AccountMaterialService.Legacy(character, items),
                items.RaidAccountMaterialLimits, (clock ?? TimeProvider.System).GetUtcNow());
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

        // 选角保留既有决斗/旧魔兽返回地区兼容；普通城镇切图仍使用原有的各自规则。
        Location? OrdinaryDistrict(Location? saved) => saved?.Town switch
        {
            ChannelAreaLanding.PvpTown => null,
            ChannelAreaLanding.LegacyRetiredTown => new(ChannelAreaLanding.CentralParkTown, 1, 0, 0),
            _ => OrdinaryRoomReturn(saved)
        };
    });
}
