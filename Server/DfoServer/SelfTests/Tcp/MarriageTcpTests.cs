using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Marriage;
using DfoServer.Network.Projection;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class MarriageTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        CharacterDefaultAppearanceTests.RunResources(resources.Archive, resources.Characters, resources.ItemResources.Items);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "marriage"), resources: resources);
        await fixture.StartAsync();
        ushort firstId, secondId;
        uint context;
        using (var first = Open(fixture, "marriagea"))
        using (var second = Open(fixture, "marriageb"))
        {
            first.Create("MarryA", 0); second.Create("MarryB", 1);
            firstId = first.Id; secondId = second.Id;
            fixture.Sql("UPDATE characters SET level=30,gold=1000000 WHERE id IN ($p0,$p1)", firstId, secondId);
            first.Select(); second.Select();
            first.Barrier(); second.Barrier();
            first.Send(1037, Convert.FromHexString("FFFFFF7F0CEA1A0088D7C803B400000000"));
            first.Expect(1, 1037, [0]);
            first.Send(1034, Request(secondId, 1));
            first.Expect(1, 1034, Ack(secondId, 1));
            second.Expect(0, 1000, Notice(firstId, 1));
            second.Send(1035, Response(firstId, 1, 1));
            second.Expect(1, 1035, AnswerAck(firstId, 1, 1));
            first.Expect(0, 1001, AnswerNotice(secondId, 1, 1));
            That(fixture.Query("SELECT count(*) FROM marriage_records") is [[0L]], "拒绝求婚不建立关系或扣费");

            first.Send(1034, Request(secondId, 1)); first.Expect(1, 1034, Ack(secondId, 1));
            second.Expect(0, 1000, Notice(firstId, 1));
            second.Send(1035, Response(firstId, 1, 0));
            second.Expect(1, 1035, AnswerAck(firstId, 1, 0));
            first.Expect(0, 1001, AnswerNotice(secondId, 1, 0));
            using var record = JsonDocument.Parse((string)fixture.Query("SELECT aggregate_json FROM marriage_records").Single()[0]);
            context = record.RootElement.GetProperty("ContextId").GetUInt32();
            byte[] information = Info(context, 1, 0);
            first.Expect(0, 1003, information); second.Expect(0, 1003, information);
            That(context != 0 && fixture.Query("SELECT count(*) FROM marriage_active_members") is [[2L]], "双方确认生成同一非零上下文与唯一关系");
            second.Send(1035, Response(firstId, 1, 0));
            Rejected(second, 1035);
            That(fixture.Query("SELECT revision FROM marriage_records") is [[1L]], "确认重放不能重复建立婚约");
            first.Send(1034, Request(secondId, 2, 0, 255)); Rejected(first, 1034);
            first.Send(1034, new byte[7]); Rejected(first, 1034);
            first.Barrier(); second.Barrier();
        }
        // 重建服务清空在线状态，复用只读资源并验证关系及每日积分由数据库恢复。
        await fixture.StopAsync(); await fixture.StartAsync("marriage-reopen");
        using (var first = Open(fixture, "marriagea"))
        using (var second = Open(fixture, "marriageb"))
        {
            first.Select(); second.Select();
            Bytes(first.MarriageInformation, Info(context, 1, 1), "订婚登录发放本游戏日的一点心意");
            Bytes(second.MarriageInformation, Info(context, 1, 1), "配偶每日点独立持久化");
            first.Send(1037, Structure(Build(w => w.Write(0u))));
            var card = first.Expect(1, 1037).Body;
            That(card.Length == 98 && card[0] == 1 && U32(card, 5) == context && card[43] == 30 && card[44] == 1,
                "角色卡查询97字节真实配偶数据，保留上下文及职业字段");
            var defaults = resources.Characters.DefaultAppearance.Resolve(1, 0);
            That(defaults.Any(pair => pair.Key <= 9), "真实职业资源包含可验证的默认装扮");
            foreach (var pair in defaults.Where(pair => pair.Key <= 9))
                That(U32(card, 46 + pair.Key * 4) == pair.Value,
                    "配偶未穿戴装扮时下发对应职业的默认外观 " + pair.Key);
            // 匹配客户端的本人伴侣查询用Target=0；前13字节来自原生栈结构，不承载目标。
            byte[] ownPartner = Convert.FromHexString("FFFFFF7F0CEA1A0088D7C803B400000000");
            first.Send(1037, ownPartner);
            Bytes(first.Expect(1, 1037).Body, card, "原生零目标查询返回当前婚约的同一伴侣卡，供婚礼窗口取得姓名");
            second.Send(1037, ownPartner);
            var firstCard = second.Expect(1, 1037).Body;
            That(firstCard.Length == 98 && firstCard[0] == 1 && U32(firstCard, 5) == context && firstCard[43] == 30 && firstCard[44] == 0,
                "双方零目标查询各自的真实伴侣，不能互换角色卡");
            first.Send(1037, Structure(Build(w => w.Write((uint)secondId))));
            Bytes(first.Expect(1, 1037).Body, firstCard,
                "查看伴侣的婚姻信息返回其配偶即本人，不重复返回被查看者");
            second.Send(1037, Structure(Build(w => w.Write((uint)firstId))));
            Bytes(second.Expect(1, 1037).Body, card, "反向查看同样以被查看者为主体解析配偶");
            using (var visitor = Open(fixture, "marriagevisitor"))
            {
                visitor.Create("MarryVisitor", 2); visitor.Select(); visitor.Barrier();
                string beforeQuery = fixture.Snapshot("SELECT marriage_id,revision,aggregate_json FROM marriage_records ORDER BY marriage_id");
                visitor.Send(1037, Structure(Build(w => w.Write((uint)firstId))));
                Bytes(visitor.Expect(1, 1037).Body, card, "未婚第三方查看一方，返回该方的真实伴侣");
                visitor.Send(1037, Structure(Build(w => w.Write((uint)secondId))));
                Bytes(visitor.Expect(1, 1037).Body, firstCard, "未婚第三方反向查看，双方职业、默认及来源外观不混用");
                visitor.Send(1037, ownPartner); visitor.Expect(1, 1037, [0]);
                first.Send(1037, Structure(Build(w => w.Write((uint)visitor.Id)))); first.Expect(1, 1037, [0]);
                That(fixture.Snapshot("SELECT marriage_id,revision,aggregate_json FROM marriage_records ORDER BY marriage_id") == beforeQuery,
                    "公开查询、本人未婚及目标未婚拒绝均不改变婚姻资产和关系版本");
                visitor.Barrier();
            }
            first.Send(1037, Structure(Build(w => w.Write(uint.MaxValue))));
            first.Expect(1, 1037, [0]);
            first.Send(1037, ownPartner[..^1]);
            first.Expect(1, 1037, [0]);
            first.Barrier();
            first.Send(1034, Request(secondId, 5));
            first.Expect(0, 14);
            first.Expect(1, 1034, Ack(secondId, 5));
            first.Expect(0, 1005, [5]); second.Expect(0, 1005, [5]);
            first.Expect(0, 1003, new byte[15]); second.Expect(0, 1003, new byte[15]);
            byte[] completed = new byte[30004];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(completed, 30000);
            completed[4 + 13700] = 1; // 本版城镇悬浮兼容位，不代表真实婚姻准备任务已完成。
            first.Expect(0, 574, new byte[4]); first.Expect(0, 356, completed);
            first.Expect(0, 1011, new byte[17]);
            second.Expect(0, 574, new byte[4]); second.Expect(0, 356, completed);
            second.Expect(0, 1011, new byte[17]);
            That(fixture.Query("SELECT count(*) FROM marriage_active_members") is [[0L]] &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", firstId) is [[500000L]], "退婚扣真实费用并释放双方唯一关系");
            first.Send(1037, ownPartner);
            first.Expect(1, 1037, [0]);
            first.Send(1034, Request(secondId, 1)); Rejected(first, 1034);
            That(fixture.Query("SELECT count(*) FROM marriage_records") is [[1L]], "当日退婚后不能通过新求婚绕过冷却");
            first.Barrier(); second.Barrier();
        }
        int sourceItemId = resources.Characters.Get(1).DefaultAvatarEquipment[3];
        fixture.Sql("""
            INSERT INTO character_inventory_items(item_uid,character_id,account,item_core)
                VALUES(999999,$p0,'marriageb','{"ItemId":101500878}');
            INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid)
                VALUES($p0,'marriageb',3,3,999999);
            INSERT INTO inventory_avatar_details(item_uid,detail)
                VALUES(999999,json_object('ClearAvatarId',$p1));
            """, secondId, sourceItemId);
        var cloneCard = new CharacterRepository(fixture.Database).FindMarriageCard(secondId)!;
        That(cloneCard.Equipment[3] == 101500878 && cloneCard.ClearAvatarIds[3] == sourceItemId,
            "离线角色卡从同一库存快照读取克隆来源");
        byte[] cloneWire = MarriageProjection.Card(context, cloneCard, resources.Characters.DefaultAppearance.Resolve(1, 0),
            id => resources.ItemResources.Items.TryGet(id, out var template) && template.CloneAvatar);
        That(U32(cloneWire, 46 + 3 * 4) == sourceItemId, "角色卡显示克隆保存的来源而非克隆模板");
        fixture.Sql("UPDATE inventory_avatar_details SET detail=json_object('ClearAvatarId',0) WHERE item_uid=999999");
        var emptyClone = new CharacterRepository(fixture.Database).FindMarriageCard(secondId)!;
        byte[] emptyCloneWire = MarriageProjection.Card(context, emptyClone, resources.Characters.DefaultAppearance.Resolve(1, 0),
            id => resources.ItemResources.Items.TryGet(id, out var template) && template.CloneAvatar);
        That(emptyClone.ClearAvatarIds[3] == 0 && U32(emptyCloneWire, 46 + 3 * 4) == sourceItemId,
            "无来源的克隆按空槽显示职业默认外观");
        fixture.Sql("DELETE FROM character_inventory_items WHERE item_uid=999999");
        NewJobPartnerCards(fixture, resources);
        That(fixture.Query("PRAGMA foreign_key_check").Length == 0 && fixture.Query("PRAGMA integrity_check") is [["ok"]],
            "婚约创建、重开和解除保持数据库完整");
        Console.WriteLine($"PASS {Count - checks} marriage TCP assertions");
    }

    static void NewJobPartnerCards(TcpTestFixture fixture, TcpTestResources resources)
    {
        using var first = Open(fixture, "default-card-first");
        using var partner = Open(fixture, "default-card-partner");
        first.Create("ViewFirst", 11); partner.Create("ViewPartner", 14);
        fixture.Sql("UPDATE characters SET level=60 WHERE id IN ($p0,$p1)", first.Id, partner.Id);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var firstOwner = owners.Acquire("default-card-first", () => { }))
        using (var secondOwner = owners.Acquire("default-card-partner", () => { }))
        {
            var a = repository.LoadCharacter(firstOwner, 0);
            var b = repository.LoadCharacter(secondOwner, 0);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var record = MarriageWeddingRules.CreateEngagement(Guid.NewGuid(), 0xf9000014, a.Id, b.Id, now) with
                { State = 4, MarriedAt = now };
            repository.CommitMarriageTogether(firstOwner, secondOwner, a, b,
                new(a.Inventory, a.Inventory, "marriage_fixture"), new(b.Inventory, b.Inventory, "marriage_fixture"), null, record);
        }
        first.Select(); first.Barrier();
        var worn = repository.FindMarriageCard(partner.Id)!.Equipment;
        var inventoryBefore = fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0", partner.Id);
        foreach (byte grow in new byte[] { 0, 1, 0x11, 0x21, 2, 0x12 })
        {
            // 对方仍在选角，直接准备隔离库的展示状态，不覆盖任何在线角色快照。
            fixture.Sql("UPDATE characters SET grow_type=$p0 WHERE id=$p1", grow, partner.Id);
            first.Send(1037, Structure(Build(w => w.Write(0u))));
            var card = first.Expect(1, 1037).Body;
            That(card.Length == 98 && card[0] == 1 && card[44] == 14 && card[45] == grow,
                "女圣职者角色卡保留真实职业及完整转职、觉醒位");
            var defaults = resources.Characters.DefaultAppearance.Resolve(14, grow);
            for (byte slot = 0; slot < 12; slot++)
            {
                int expected = slot <= 9 ? defaults.GetValueOrDefault(slot, -1) : -1;
                That(U32(card, 46 + slot * 4) == unchecked((uint)expected),
                    "对方空装扮槽使用本职业本分支默认，不借用本人外观 " + grow + "/" + slot);
            }
            That(U32(card, 94) == worn[12], "展示默认装扮不替换对方真实武器");
            first.Barrier();
        }
        That(Convert.ToInt64(fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0", partner.Id).Single()[0]) ==
            Convert.ToInt64(inventoryBefore.Single()[0]), "普通、转职和觉醒默认外观只作显示，不生成任何库存物品");
    }

    static byte[] Structure(byte[] data) => [.. new byte[13], .. data];
    static byte[] Request(ushort target, byte operation, byte type = 0, byte selection = 0) =>
        Structure(Build(w => { w.Write((uint)target); w.Write(operation); w.Write(type); w.Write(selection); }));
    static byte[] Response(ushort target, byte operation, byte answer) =>
        Structure(Build(w => { w.Write((uint)target); w.Write(operation); w.Write((ushort)0); w.Write(answer); }));
    static byte[] Notice(ushort target, byte operation) => Build(w => { w.Write(0); w.Write((uint)target); w.Write(operation); w.Write((ushort)0); });
    static byte[] Ack(ushort target, byte operation) => [1, .. Notice(target, operation)];
    static byte[] AnswerNotice(ushort target, byte operation, byte answer) => [.. Notice(target, operation), answer];
    static byte[] AnswerAck(ushort target, byte operation, byte answer) => [1, .. AnswerNotice(target, operation, answer)];
    static byte[] Info(uint context, byte state, uint love) => Build(w => { w.Write(context); w.Write(state); w.Write((byte)0); w.Write(0u); w.Write(love); w.Write((byte)0); });
    static void Rejected(TcpPeer peer, ushort opcode)
    {
        byte[] body = peer.Expect(1, opcode).Body;
        That(body.Length >= 5 && body[0] == 1 && unchecked((int)U32(body, 1)) < 0, "业务拒绝保持连接并使用真实i32错误码");
    }
}
