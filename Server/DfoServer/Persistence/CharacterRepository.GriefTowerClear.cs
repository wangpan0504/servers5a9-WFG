using System.Text.Json;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    static string GriefRewardJson(GriefTowerClearReward clear) =>
        JsonSerializer.Serialize(new { clear.Reward, Cores = clear.Delivery.Rewards });

    static uint? ReadGriefClear(SqliteConnection db,SqliteTransaction tx,AccountOwners.Lease owner,
        PreparedDungeonSettlement prepared)
    {
        if (prepared.Grief is not { } clear) return null;
        using var query=db.CreateCommand();query.Transaction=tx;
        query.Parameters.AddWithValue("$id",prepared.Fact.Identity.CharacterId);
        query.Parameters.AddWithValue("$instance",prepared.Fact.Identity.InstanceId.ToString("N"));
        query.CommandText="""
            SELECT account,event_id,task_attempt,game_day,layer,dungeon_id,reward_json,mail_id
            FROM character_grief_tower_clears WHERE character_id=$id AND instance_id=$instance
            """;
        using var reader=query.ExecuteReader();
        if (!reader.Read() || reader.GetString(0)!=owner.Account || reader.GetString(1)!=prepared.EventId.ToString("N") ||
            reader.GetInt64(2)!=prepared.Fact.Identity.TaskAttempt || reader.GetInt32(3)!=clear.Before.State.CharacterDay ||
            reader.GetInt32(4)!=clear.Floor.Layer || reader.GetInt64(5)!=prepared.Fact.DungeonId ||
            reader.GetString(6)!=GriefRewardJson(clear))
            throw new InvalidDataException("悲叹已提交回执的实例、楼层或冻结奖励不一致");
        return reader.IsDBNull(7) ? null : checked((uint)reader.GetInt64(7));
    }

    uint? CommitGriefClear(SqliteConnection db,SqliteTransaction tx,AccountOwners.Lease owner,
        Character expected,PreparedDungeonSettlement prepared)
    {
        if (prepared.Grief is not { } clear) return null;
        var identity=prepared.Fact.Identity;
        var before=clear.Before;
        var currentProgress=ReadGriefTower(db,tx,owner.Account,expected.Id);
        if (before.InstanceId!=identity.InstanceId || before.TaskAttempt!=identity.TaskAttempt ||
            before.State.CharacterCleared || before.State.CharacterDay<=0 || before.State.Layer!=clear.Floor.Layer ||
            clear.Floor.DungeonId!=prepared.Fact.DungeonId || clear.Floor.Layer is <1 or >100 ||
            prepared.Card is not null || prepared.Options is not null ||
            currentProgress.State!=before.State || currentProgress.InstanceId!=before.InstanceId ||
            currentProgress.TaskAttempt!=before.TaskAttempt)
            throw new InvalidOperationException("悲叹通关挑战或楼层已变化");
        var delivery=clear.Delivery;
        if (clear.Reward.Rule!=GriefTowerRewardPolicy.Rule || clear.Reward.Group!=clear.Floor.RewardGroup ||
            clear.Reward.Items.IsDefault || clear.Reward.Items.Length>10 || delivery.Rewards.Length!=clear.Reward.Items.Length ||
            !SameInventory(delivery.Inventory.After with { Items=delivery.Inventory.Before.Items },delivery.Inventory))
            throw new InvalidDataException("悲叹冻结奖励数量或规则不一致");
        for (int i=0;i<delivery.Rewards.Length;i++)
        {
            var selected=clear.Reward.Items[i];var reward=delivery.Rewards[i];
            if (selected.Slot is <1 or >10 || i>0 && selected.Slot<=clear.Reward.Items[i-1].Slot ||
                reward.Gold!=0 || reward.ExtraItem is not null || reward.HopeEquipment is not null || reward.HopeSealingItem is not null || reward.RuleId!=clear.Reward.Rule ||
                reward.ResourceSource!=GameWorld.Dungeons.GriefTowerRewardTable.ResourcePath ||
                reward.Item is not { IsStackable:true } core || core.ItemId!=selected.ItemId || core.Count!=selected.Count)
                throw new InvalidDataException("悲叹完整核心与冻结槽位不一致");
        }
        ValidateCardDelivery(delivery,delivery.Rewards,0);
        var current=Require(db,tx,owner.Account,expected.Name);
        if (!SameInventory(current.Inventory,delivery.Inventory))
            throw new InvalidOperationException("悲叹奖励背包前提已变化");
        using var command=db.CreateCommand();command.Transaction=tx;
        command.Parameters.AddWithValue("$id",expected.Id);
        command.Parameters.AddWithValue("$account",owner.Account);
        command.Parameters.AddWithValue("$instance",identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$attempt",identity.TaskAttempt);
        command.Parameters.AddWithValue("$day",before.State.CharacterDay);
        command.Parameters.AddWithValue("$layer",clear.Floor.Layer);
        command.Parameters.AddWithValue("$dungeon",prepared.Fact.DungeonId);
        command.CommandText="""
            SELECT count(*) FROM character_grief_tower_entries WHERE character_id=$id AND account=$account
              AND instance_id=$instance AND task_attempt=$attempt AND game_day=$day AND layer=$layer AND dungeon_id=$dungeon
            """;
        if ((long)command.ExecuteScalar()! !=1) throw new InvalidDataException("悲叹通关没有匹配的已提交准入回执");
        if (delivery.Inventory.Changed)
            WriteInventory(db,tx,owner,expected.Id,delivery.Inventory,delivery.Inventory.After);
        uint? mail=null;
        if (!delivery.MailItems.IsEmpty)
        {
            var recipient=new MailboxRecipient(expected.Id,owner.Account,expected.Name,expected.Job,expected.GrowType,current.Level);
            var attachments=delivery.MailItems.Select(core=>new InventoryItem(0,new(InventoryListType.Main,9),core)).ToImmutableArray();
            var message=new MailboxDelivery("grief-clear:"+prepared.EventId.ToString("N"),recipient,"悲叹之塔奖励",
                "背包空间不足，通关物品已通过永久邮件保存。",0,attachments,delivery.Rewards[0].CreatedAt);
            var (error,id)=DeliverMailInTransaction(db,tx,message);
            if (error!=MailboxError.None) throw new InvalidOperationException("悲叹奖励邮件失败: "+error);
            mail=id;
        }
        command.CommandText="""
            UPDATE character_grief_tower SET cleared=1,layer=layer+1 WHERE character_id=$id AND account=$account
              AND instance_id=$instance AND task_attempt=$attempt AND game_day=$day AND layer=$layer AND cleared=0
            """;
        if (command.ExecuteNonQuery()!=1) throw new InvalidOperationException("悲叹通关资格已变化");
        command.Parameters.AddWithValue("$event",prepared.EventId.ToString("N"));
        command.Parameters.AddWithValue("$reward",GriefRewardJson(clear));
        command.Parameters.AddWithValue("$mail",(object?)mail ?? DBNull.Value);
        command.Parameters.AddWithValue("$revision",current.Revision);
        command.CommandText="""
            INSERT INTO character_grief_tower_clears(character_id,account,instance_id,event_id,task_attempt,game_day,
              layer,dungeon_id,reward_json,mail_id,revision)
            VALUES($id,$account,$instance,$event,$attempt,$day,$layer,$dungeon,$reward,$mail,$revision)
            """;
        command.ExecuteNonQuery();
        return mail;
    }
}
