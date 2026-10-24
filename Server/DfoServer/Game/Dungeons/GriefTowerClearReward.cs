using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 选择、完整物品核心及溢出归属一起冻结；重试只使用原对象，不重新抽样或计算期限。
internal sealed record GriefTowerClearReward(GriefTowerProgress Before, GriefTowerFloor Floor,
    GriefTowerReward Reward, DungeonCardDelivery Delivery)
{
    public static GriefTowerClearReward Prepare(GriefTowerCatalog tower, GriefTowerProgress before, int dungeonId,
        Character character, ItemCatalog items, EpicPieceCatalog pieces, Func<int,int> draw, DateTimeOffset now)
    {
        if (before.InstanceId == Guid.Empty || before.TaskAttempt != character.DungeonState?.LastAttempt ||
            before.State.CharacterDay <= 0 || before.State.CharacterCleared ||
            !tower.FloorsByDungeon.TryGetValue(dungeonId,out var floor) || floor.Layer != before.State.Layer)
            throw new InvalidOperationException("悲叹奖励必须属于已提交且尚未通关的当前挑战");
        var reward = GriefTowerRewardPolicy.Prepare(tower,floor,draw);
        var inventory = character.Inventory;
        var cores = ImmutableArray.CreateBuilder<DungeonCardReward>(reward.Items.Length);
        var mails = ImmutableArray.CreateBuilder<ItemCore>();
        foreach (var selected in reward.Items)
        {
            var template = items.Get(selected.ItemId);
            var core = InventoryRewardGrantService.Create(template,selected.Count,now);
            if (!core.IsStackable || core.IsExpired(now.ToUnixTimeSeconds()))
                throw new NotSupportedException("悲叹具名奖励仅支持本版未过期的堆叠物品");
            var granted = InventoryRewardGrantService.TryGrantCore(character with { Inventory = inventory },core,template,pieces,now);
            if (!granted.Accepted && granted.Error != 17)
                throw new InvalidDataException("悲叹冻结奖励不能发放: " + granted.Error);
            if (granted.Accepted) inventory = granted.Plan!.After;
            else mails.Add(core);
            cores.Add(new(0,core,reward.Rule,GriefTowerRewardTable.ResourcePath,now.ToUnixTimeSeconds()));
        }
        // 复用完整物品批次及永久溢出邮件合同；悲叹没有普通金币牌或收费选牌。
        return new(before,floor,reward,new(new(character.Inventory,inventory,"dungeon_card_reward"),
            cores.ToImmutable(),mails.ToImmutable(),0));
    }
}
