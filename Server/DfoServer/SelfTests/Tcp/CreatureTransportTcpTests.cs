using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class CreatureTransportTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        var clock = new ConsumableUseSessionTests.FakeClock();
        await using var fixture = new TcpTestFixture(options, Path.Combine(root,"creature-transport"), resources: resources, clock: clock);
        var assets = resources.ItemResources;
        That(assets.Items.ActionConsumable(10007991) is { Label: "[creature transport stone]", Parameters: [4012] },
            "实测10007991使用当前PVF动作，不将规则编号4012当道具消耗数量");
        That(assets.Items.ActionConsumable(490705421) is { Label: "[creature transport stone]", Parameters: [>= 0] },
            "421的365天邮件仍要求当前PVF真实跨界动作定义");
        var repo = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort senderId, recipientId;
        InventoryItem pet;
        using (var owner = owners.Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var profile = resources.Characters.Get(0);
            var starter = InventoryRewardGrantService.CreateStarter(profile);
            var sender = repo.Create(owner, new("PetSender", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = profile.Skills,
                Inventory = starter with { Items = [.. starter.Items,
                    new(0, new(InventoryListType.Main,82), new(490705421, GameWorld.Items.ItemKind.Consumable,2)),
                    new(0, new(InventoryListType.Pet,16), new(63008, GameWorld.Items.ItemKind.Creature,0)
                        { EnchantCardId=1234, Extension27=99, TradeRestriction=1 })
                    { Creature = new() { Name="PetKeep", Level=5, Experience=50, Satiety=41,
                        ExpireTime=(int)clock.GetUtcNow().ToUnixTimeSeconds()+100 } } ] }
            });
            senderId = sender.Id;
            pet = sender.Inventory.Find(new(InventoryListType.Pet,16))!;
            var recipient = repo.Create(owner, new("PetReceiver", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level=60, AcceptedStoryLevel=1, Skills=profile.Skills, Inventory=starter });
            recipientId = recipient.Id;
            using var other = owners.Acquire(fixture.Options.DevelopmentAccount+"-other", () => { });
            var foreign = repo.Create(other, new("ForeignPet",resources.Towns.Room,resources.Towns.InitialReturn));
            var policy = new CreatureTransportMailPolicy(1);
            var service = new CreatureTransportService(assets.Items.Get,assets.Items.ActionConsumable,assets.Creatures,repo,() => policy,clock);
            That(service.Plan(sender,CharacterRosterEntry.FromCharacter(foreign),new(82,16,1,1),"[seria room]",out var forged)==0,
                "源规则规划不猜测账号归属，跨账号身份须由提交事务重新确认");
            string before = State();
            Reject<InvalidOperationException>(() => service.Commit(owner,forged!),"持久化拒绝伪造的其他账号收件人");
            That(State()==before,"跨账号伪造不会修改源、附件、邮件或任何角色快照");
            That(service.Plan(sender,CharacterRosterEntry.FromCharacter(recipient),new(82,16,1,1),"[seria room]",out var staleRoster)==0,
                "当前真实收件名册可规划");
            // 真实名册有空槽时，行号1不等于持久槽7，也不等于数据库角色ID。
            fixture.Sql("UPDATE characters SET slot=7 WHERE id=$p0", recipientId);
            before = State();
            Reject<InvalidOperationException>(() => service.Commit(owner,staleRoster!),"持久化拒绝已变动的旧目标名册槽");
            That(State()==before,"旧目标名册行拒绝不修改原资产");
        }
        await fixture.StartAsync();
        uint mailId;
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            foreach (var body in new[] { Request(7), Request(uint.MaxValue), new byte[8], Request(1,0) })
            {
                string before = State(); peer.Send(557, body); peer.Expect(1,557,[0,7,0]); peer.Barrier();
                That(State() == before, "不存在的名册行号、畸形长度及装备跨界模式明确拒绝且资产不变");
            }
            fixture.Sql("CREATE TRIGGER fail_creature_transport AFTER INSERT ON mailbox_audit_log " +
                "WHEN NEW.action_name='creature_transport' BEGIN SELECT RAISE(ABORT,'transport rollback'); END");
            string rollback = State();
            peer.Send(557, Request(1)); peer.Expect(1,557,[0,7,0]); peer.Barrier();
            That(State() == rollback, "邮件及附件写完后的SQL失败回滚石头、宠物、详情、revision、冷却和邮件审计");
            fixture.Sql("DROP TRIGGER fail_creature_transport");
            peer.Send(557, Request(1)); peer.Expect(1,557,[1,82,0,16,0,1,0,0,0,1]);
            peer.Expect(0,14,[7,1,0,16,0,255,255,255,255,..new byte[117]]); peer.Barrier();
            That(fixture.Query("SELECT character_id,item_core FROM character_inventory_items WHERE item_uid=$p0",pet.Uid)
                is [[var newOwner,var core]] && Convert.ToInt32(newOwner)==recipientId && Equals(core,JsonSerializer.Serialize(pet.Core)),
                "跨界保留同一UID、绑定、附魔及完整核心，只把所有者交给同账号收件角色");
            That(fixture.Query("SELECT detail FROM inventory_creature_details WHERE item_uid=$p0",pet.Uid)
                is [[var detail]] && Equals(detail,JsonSerializer.Serialize(pet.Creature)) &&
                fixture.Query("SELECT COUNT(*) FROM character_inventory_slots WHERE item_uid=$p0",pet.Uid) is [[0L]],
                "有限期、名称、经验及饱食保持不变，附件托管期间没有第二份真实库存槽");
            mailId = Convert.ToUInt32(fixture.Query("SELECT mail_id FROM mailbox_messages WHERE recipient_id=$p0",recipientId).Single().Single());
            long created = clock.GetUtcNow().ToUnixTimeSeconds();
            That(fixture.Query("SELECT created_at,expires_at FROM mailbox_messages WHERE mail_id=$p0",mailId)
                is [[var sentAt,var expiresAt]] && Convert.ToInt64(sentAt)==created && Convert.ToInt64(expiresAt)==created+365L*86400 &&
                pet.Creature!.ExpireTime==created+100,
                "557提交的421邮件保存365天，原宠物自身100秒寿命不延长");
            string committed = State();
            peer.Send(557, Request(1)); peer.Expect(1,557,[0,17,0]); peer.Barrier();
            That(State()==committed, "目标已转出后的重放不再次扣剩余石头或生成邮件");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(1); peer.Barrier();
            That(peer.Id==recipientId, "重连按原名册行号进入有槽位空洞的收件角色");
            peer.Send(95,Build(w => { w.Write(1); w.Write(mailId); }));
            var ack = peer.Expect(1,95).Body;
            That(ack.Length==17 && ack[0]==1 && U32(ack,1)==1 && U32(ack,5)==mailId,
                "跨界邮件走既有整封领取协议，不使用普通寄送交易资格或重复收费");
            peer.Expect(0,105); peer.Expect(0,14); peer.Barrier();
            That(fixture.Query("SELECT character_id,list_type FROM character_inventory_slots WHERE item_uid=$p0",pet.Uid)
                is [[var owner,var list]] && Convert.ToInt32(owner)==recipientId && Convert.ToInt32(list)==7 &&
                fixture.Query("SELECT detail FROM inventory_creature_details WHERE item_uid=$p0",pet.Uid)
                    is [[var detail]] && Equals(detail,JsonSerializer.Serialize(pet.Creature)),
                "跨界宠物可真实领取回Pet栏，原UID和全部详情在重连后仍一致");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(); peer.Barrier();
            That(fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s ON s.item_uid=i.item_uid " +
                "WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=82",senderId) is [[var source]] &&
                JsonSerializer.Deserialize<ItemCore>((string)source)!.Count==1,
                "再次进入源角色仍只扣一颗石头，不因收件或重连重复消费");
        }
        byte[] Request(uint index,byte mode=1) => Build(w => { w.Write((ushort)82); w.Write((ushort)16); w.Write(mode); w.Write(index); });
        string State() => JsonSerializer.Serialize(new
        {
            Roles=fixture.Query("SELECT id,revision,daily_booster_usage FROM characters ORDER BY id"),
            Items=fixture.Query("SELECT item_uid,character_id,item_core FROM character_inventory_items ORDER BY item_uid"),
            Details=fixture.Query("SELECT item_uid,detail FROM inventory_creature_details ORDER BY item_uid"),
            Slots=fixture.Query("SELECT character_id,list_type,slot_index,item_uid FROM character_inventory_slots ORDER BY character_id,list_type,slot_index"),
            Mails=fixture.Query("SELECT * FROM mailbox_messages ORDER BY mail_id"),
            Attachments=fixture.Query("SELECT * FROM mailbox_attachments ORDER BY attachment_id"),
            States=fixture.Query("SELECT * FROM character_consumable_states ORDER BY character_id,item_id")
        });
    }
}
