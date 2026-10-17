using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class MailboxTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var material = MailboxTests.TradableMaterial(resources.Archive, items);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "mailbox"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        long equipmentUid;
        var sourceCore = InventoryRewardGrantService.Create(items.Items.Get(18400)) with { Attr = 7, EnchantCardId = 123, Rune = 3,
            Random0 = new(1, 2, 3), RandomChangedIndex = 255, Extension97 = 123, GuardianGemKeys = 999 };
        int equipmentFee = 1150 + items.Items.Get(18400).ShopPrice.GetSellGold(sourceCore.Durability, items.Items.Get(18400).Durability) / 20;
        int balanceAfterEquipment = 1_000_000 - 1000 - equipmentFee;
        var secondEquipment = InventoryRewardGrantService.Create(items.Items.Get(10400));
        int secondFee = checked((int)(2100 + (material.ShopPrice.SellGold * 4L +
            items.Items.Get(10400).ShopPrice.GetSellGold(secondEquipment.Durability, items.Items.Get(10400).Durability)) / 20));
        using (var senderOwner = owners.Acquire("10000", () => { }))
        using (var receiverOwner = owners.Acquire("20001", () => { }))
        {
            var sender = repository.Create(senderOwner, new("MailSender", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)) with { Gold = 1_000_000,
                    Items = [.. InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)).Items,
                        new(0, new(InventoryListType.Main, 9), sourceCore),
                        new(0, new(InventoryListType.Main, 10), InventoryRewardGrantService.Create(items.Items.Get(10400))),
                        new(0, new(InventoryListType.Main, 121), InventoryRewardGrantService.Create(material, 10))] }
            });
            equipmentUid = sender.Inventory.Find(new(InventoryListType.Main, 9))!.Uid;
            repository.Create(receiverOwner, new("MailRecv", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 60,
                AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0)) });
            PreparePetClaimRole(repository, receiverOwner, resources);
            var service = new MailboxService(repository, items.Items, items.GoldLimits, items.EpicPieces);
            That(service.Deliver(new("tcp-initial", service.FindRecipient("MailRecv")!, "系统", "上线正文", 0, [], DateTimeOffset.UtcNow.ToUnixTimeSeconds())).Accepted,
                "系统邮件在独立 fixture 启动前持久化");
        }
        await fixture.StartAsync();
        uint savedText;
        using (var sender = Open(fixture))
        using (var receiver = Open(fixture, "20001"))
        {
            sender.Select(); receiver.Select();
            That(sender.Mailbox is [var empty] && empty.SequenceEqual(new byte[6]), "空邮箱上线包完整");
            That(ReadList(receiver.Mailbox.Single()).Details.Single().Text == "上线正文", "非空邮箱在入城阶段直接恢复正文");
            sender.Send(324, Build(w => { WriteString(w, "MailRecv"); w.Write((byte)0); }));
            var query = sender.Expect(1, 324).Body;
            That(query.SequenceEqual(Build(w => { w.Write((byte)1); WriteString(w, "MailRecv"); w.Write((byte)0); w.Write((ushort)60);
                w.Write((byte)0); w.Write((byte)0); w.Write((byte)1); w.Write((byte)0); w.Write((byte)0); })),
                "收件人查询包含 server 标记、等级、职业及全部尾字节");
            SendMail(sender, false, 1000, [(0, 9, 18400, 1)], "装备正文");
            sender.Expect(1, 94, [1]);
            var sentDelta = sender.Expect(0, 14).Body;
            CheckItem(sentDelta, 0, 0, balanceAfterEquipment); CheckItem(sentDelta, 9, -1, 0);
            sender.Barrier();
            receiver.Expect(0, 99, [1, 0]);
            receiver.Send(96); receiver.Expect(1, 96, [1, 0, 0]);
            var list = ReadList(receiver.Expect(0, 97).Body);
            var attachment = list.Summaries.Single(row => row.ItemId == 18400);
            That(attachment.ClaimId >= 0x40000000 && attachment.MailId < 0x40000000 && attachment.Gold == 1000 &&
                attachment.Core[10] == 7 && U16(attachment.Core, 69) == 3 && U32(attachment.Core, 97) == 123,
                "独立 TCP 解码确认完整核心、领取对象与合并 key");
            string unchanged = fixture.Snapshot();
            SendMail(sender, false, 1000, [(0, 9, 18400, 1)], "装备正文");
            sender.Expect(1, 94, [0, 17, 0]);
            var restored = sender.Expect(0, 14).Body;
            CheckItem(restored, 0, 0, balanceAfterEquipment); CheckItem(restored, 9, -1, 0);
            sender.Barrier();
            That(fixture.Snapshot() == unchanged, "已转出附件的重复寄送拒绝且不再次扣费");

            Extract(receiver, [attachment.ClaimId]);
            ExpectClaim(receiver, [attachment]);
            var claimDelta = receiver.Expect(0, 14).Body;
            CheckItem(claimDelta, 0, 0, 1000); CheckItem(claimDelta, 9, 18400, 999999998);
            receiver.Barrier();
            receiver.Send(96); receiver.Expect(1, 96, [1, 0, 0]);
            var afterClaim = ReadList(receiver.Expect(0, 97).Body);
            That(afterClaim.Details.Single(row => row.Id == attachment.MailId) is { State: 2, Text: "装备正文" } &&
                afterClaim.Summaries.Single(row => row.MailId == attachment.MailId) is { ItemId: 0, Gold: 0 },
                "领取不夹带清空式列表，重新打开才投影已读正文和剩余资产");
            That(fixture.Query("SELECT i.item_uid FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) WHERE i.character_id=2 AND s.list_type=0 AND s.slot_index=9") is [[long uid]] && uid == equipmentUid,
                "TCP 领取保留原装备 UID");
            unchanged = fixture.Snapshot();
            Extract(receiver, [attachment.ClaimId]); receiver.Expect(1, 95, [0, 21, 0]); receiver.Barrier();
            That(fixture.Snapshot() == unchanged, "TCP 重复领取失败，没有额外差量或资产");
            SetState(receiver, [attachment.MailId], 0); ExpectState(receiver, [attachment.MailId], 0);
            receiver.Expect(0, 98, Build(w => { w.Write(1); w.Write(attachment.MailId); }));
            That(ReadList(receiver.Expect(0, 97).Body).Details.All(row => row.Id != attachment.MailId), "删除后补页不再出现该邮件");

            SendMail(sender, true, 0, [(0, 121, material.Id, 4), (0, 10, 10400, 1)], "多附件");
            sender.Expect(1, 315, [1]);
            var multiDelta = sender.Expect(0, 14).Body;
            CheckItem(multiDelta, 0, 0, balanceAfterEquipment - secondFee); CheckItem(multiDelta, 121, material.Id, 6); CheckItem(multiDelta, 10, -1, 0);
            receiver.Expect(0, 99, [1, 0]);
            receiver.Send(96); receiver.Expect(1, 96, [1, 0, 0]);
            list = ReadList(receiver.Expect(0, 97).Body);
            var grants = list.Summaries.Where(row => row.ItemId != 0).ToArray();
            That(grants.Length == 2 && grants[0].MailId == grants[1].MailId && grants[0].ClaimId != grants[1].ClaimId,
                "多附件同一合并 key，领取对象各自独立");
            unchanged = fixture.Snapshot();
            Extract(receiver, [grants[0].ClaimId, 0x4000FFFF]); receiver.Expect(1, 95, [0, 21, 0]); receiver.Barrier();
            That(fixture.Snapshot() == unchanged, "一项无效时批量领取整批拒绝");
            Extract(receiver, [grants[0].MailId]); ExpectClaim(receiver, grants);
            var multiClaim = receiver.Expect(0, 14).Body;
            CheckItem(multiClaim, 10, 10400, 999999998); CheckItem(multiClaim, 121, material.Id, 4);
            receiver.Barrier();
            receiver.Send(96); receiver.Expect(1, 96, [1, 0, 0]);
            afterClaim = ReadList(receiver.Expect(0, 97).Body);
            That(afterClaim.Summaries.All(row => row.ItemId == 0), "整封请求收尽两条附件，重开后不存在已领资产");
            receiver.Barrier();

            SendMail(sender, false, 0, [], "保留正文"); sender.Expect(1, 94, [1]);
            CheckItem(sender.Expect(0, 14).Body, 0, 0, balanceAfterEquipment - secondFee - 100);
            receiver.Expect(0, 99, [1, 0]);
            receiver.Send(96); receiver.Expect(1, 96, [1, 0, 0]);
            list = ReadList(receiver.Expect(0, 97).Body);
            savedText = list.Details.Single(row => row.Text == "保留正文").Id;
            SetState(receiver, [savedText], 2); ExpectState(receiver, [savedText], 2);
            SetState(receiver, [savedText], 3); ExpectState(receiver, [savedText], 3);
            SetState(receiver, [savedText], 2); ExpectState(receiver, [savedText], 3);
            receiver.Barrier();
            SendMail(sender, false, 0, [], "特殊", special: 1);
            sender.Barrier();
            That(fixture.Query("SELECT count(*) FROM mailbox_messages WHERE body='特殊'") is [[0L]], "未知寄送子型不伪造成功或写邮件");
        }
        VerifyPetClaims(fixture, resources);
        VerifyWalletClaims(fixture, resources);
        await fixture.StopAsync();
        await fixture.StartAsync("restarted");
        using (var receiver = Open(fixture, "20001"))
        {
            receiver.Select();
            var saved = ReadList(receiver.Mailbox.Single()).Details.Single(row => row.Id == savedText);
            That(saved.State == 3 && saved.Text == "保留正文", "服务重启和重登恢复保存邮件正文与状态");
            Extract(receiver, [0x40000001]); receiver.Expect(1, 95, [0, 21, 0]); receiver.Barrier();
        }
        using (var malformed = Open(fixture))
        {
            malformed.Select();
            string unchanged = fixture.Snapshot();
            malformed.Send(315, Build(w => { WriteString(w, "MailRecv"); w.Write(0); w.Write((byte)11); }));
            malformed.Closed();
            That(fixture.Snapshot() == unchanged, "结构错误关闭原连接并保留全部资产");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]],
            "TCP 邮件全过程数据库与外键完整");
    }

    static void SendMail(TcpPeer peer, bool multiple, uint gold, (byte Type, short Slot, int Id, int Count)[] attachments, string text, uint special = 0) =>
        peer.Send(multiple ? (ushort)315 : (ushort)94, Build(w =>
        {
            WriteString(w, "MailRecv"); w.Write(gold);
            if (multiple) w.Write((byte)attachments.Length);
            if (!multiple && attachments.Length == 0) w.Write(new byte[11]);
            foreach (var item in attachments) { w.Write(item.Type); w.Write(item.Slot); w.Write(item.Id); w.Write(item.Count); }
            WriteString(w, text); w.Write(special); w.Write((byte)0);
        }));
    static void Extract(TcpPeer peer, IReadOnlyList<uint> ids) => peer.Send(95, Build(w => { w.Write(ids.Count); foreach (uint id in ids) w.Write(id); }));
    static void ExpectClaim(TcpPeer peer, IReadOnlyList<Summary> changes) => peer.Expect(1, 95, Build(w =>
        { w.Write((byte)1); w.Write(changes.Count); foreach (var row in changes) { w.Write(row.MailId); w.Write(row.ClaimId); w.Write(0); } }));
    static void SetState(TcpPeer peer, IReadOnlyList<uint> ids, ushort state) => peer.Send(134, Build(w => { w.Write(ids.Count); foreach (uint id in ids) w.Write(id); w.Write(state); }));
    static void ExpectState(TcpPeer peer, IReadOnlyList<uint> ids, ushort state) => peer.Expect(1, 134, Build(w =>
        { w.Write((byte)1); w.Write(ids.Count); foreach (uint id in ids) { w.Write(id); w.Write(state); } }));
    static void WriteString(BinaryWriter writer, string text)
    {
        byte[] raw = System.Text.Encoding.GetEncoding(936).GetBytes(text);
        writer.Write(raw.Length); writer.Write(raw);
    }
    static void CheckItem(byte[] body, short slot, int id, int value)
    {
        int count = U16(body, 1);
        for (int index = 0; index < count; index++)
        {
            int offset = 3 + 123 * index;
            if (U16(body, offset) == slot)
            {
                That(unchecked((int)U32(body, offset + 2)) == id && unchecked((int)U32(body, offset + 6)) == value, "邮件提交后槽位差量 " + slot);
                return;
            }
        }
        throw new InvalidOperationException("Missing mailbox delta slot " + slot);
    }
    internal sealed record Summary(uint ClaimId, uint MailId, int ItemId, uint Gold, byte[] Core, byte[]? Extra = null);
    internal sealed record Detail(uint Id, string Text, ushort State);
    internal static (Summary[] Summaries, Detail[] Details) ReadList(byte[] body, IReadOnlySet<int>? avatarItems = null,
        IReadOnlySet<int>? creatureItems = null)
    {
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream);
        int count = reader.ReadByte(); _ = reader.ReadByte();
        var summaries = new List<Summary>();
        for (int index = 0; index < count; index++)
        {
            uint claim = reader.ReadUInt32(); _ = reader.ReadUInt32(); _ = ReadString(reader);
            uint gold = reader.ReadUInt32(); int item = reader.ReadInt32(); _ = reader.ReadByte();
            _ = reader.ReadBytes(15);
            byte[]? extra = creatureItems?.Contains(item) == true ? reader.ReadBytes(5) : null;
            var core = reader.ReadBytes(123);
            if (avatarItems?.Contains(item) == true)
            {
                long start = stream.Position;
                That(reader.ReadInt32() == 30, "邮件时装摘要孔位RAW完整30B");
                That(reader.ReadBytes(30).Length == 30, "邮件时装摘要不截断孔位字段");
                That(reader.ReadInt32() == 4, "邮件时装摘要颜色RAW完整4B");
                That(reader.ReadBytes(4).Length == 4, "邮件时装摘要不截断颜色字段");
                extra = body.AsSpan(checked((int)start), 42).ToArray();
            }
            _ = reader.ReadInt32(); uint mail = reader.ReadUInt32(); _ = reader.ReadByte();
            summaries.Add(new(claim, mail, item, gold, core, extra));
        }
        _ = reader.ReadUInt16(); int detailsCount = reader.ReadUInt16();
        var details = new List<Detail>();
        for (int index = 0; index < detailsCount; index++)
        {
            uint id = reader.ReadUInt32(); _ = reader.ReadUInt32(); _ = ReadString(reader);
            string text = ReadString(reader); _ = reader.ReadInt32(); ushort state = reader.ReadUInt16(); _ = reader.ReadByte();
            details.Add(new(id, text, state));
        }
        That(stream.Position == stream.Length, "TCP 独立解码完整消费邮件列表");
        return (summaries.ToArray(), details.ToArray());
    }
    static string ReadString(BinaryReader reader) => System.Text.Encoding.GetEncoding(936).GetString(reader.ReadBytes(reader.ReadInt32()));
}
