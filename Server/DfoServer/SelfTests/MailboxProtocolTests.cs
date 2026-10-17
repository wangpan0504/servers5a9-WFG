using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Testing;

namespace DfoServer;

internal static class MailboxProtocolTests
{
    internal static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
    internal static void String(BinaryWriter writer, string text)
    {
        var bytes = CharacterName.Encoding.GetBytes(text);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
    public static void Run()
    {
        int before = TestCheck.Count;
        byte[] send = Bytes(w =>
        {
            String(w, "Receiver"); w.Write(1234); w.Write((byte)0); w.Write((ushort)9); w.Write(101010912); w.Write(1);
            String(w, "中文邮件"); w.Write(0); w.Write((byte)0);
        });
        var parsed = MailboxRequests.Send(send, false);
        TestCheck.That(parsed.Recipient == "Receiver" && parsed.Gold == 1234 && parsed.Text == "中文邮件" &&
            parsed.Items.Single() == new MailboxSendItem(0, 9, 101010912, 1), "本版单附件寄送保留完整输入");
        byte[] multi = Bytes(w =>
        {
            String(w, "Receiver"); w.Write(0); w.Write((byte)2);
            w.Write((byte)1); w.Write((ushort)0); w.Write(10001); w.Write(1);
            w.Write((byte)7); w.Write((ushort)1); w.Write(10002); w.Write(1);
            String(w, ""); w.Write(0); w.Write((byte)0);
        });
        TestCheck.That(MailboxRequests.Send(multi, true).Items.SequenceEqual(new MailboxSendItem[] { new(1, 0, 10001, 1), new(7, 1, 10002, 1) }),
            "多附件数量是 u8，后续每行独立 type/slot/id/count");
        foreach (var payload in new[] { send, multi })
            for (int length = 0; length < payload.Length; length++)
            {
                int cut = length;
                Reject(() => MailboxRequests.Send(payload.AsSpan(0, cut), ReferenceEquals(payload, multi)), "截短寄送拒绝");
            }
        Reject(() => MailboxRequests.Send([.. send, 0], false), "寄送尾部不截断");
        Reject(() => MailboxRequests.Send(Bytes(w => { w.Write(30); w.Write(new byte[30]); }), false), "名称长度受缓冲区约束");
        Reject(() => MailboxRequests.Send(Bytes(w => { w.Write(1); w.Write((byte)0x81); w.Write(new byte[30]); }), false), "无效 CP936 不替换为默认字符");
        byte[] empty = Bytes(w => { String(w, "Receiver"); w.Write(0); w.Write(new byte[11]); String(w, "正文"); w.Write(0); w.Write((byte)0); });
        TestCheck.That(MailboxRequests.Send(empty, false).Items.IsEmpty, "纯正文兼容单附件全零占位");
        var claimIds = ImmutableArray.Create<uint>(1, 0x40000001);
        var idsBody = Bytes(w => { w.Write(2); w.Write(1); w.Write(0x40000001); });
        TestCheck.That(MailboxRequests.Extract(idsBody).SequenceEqual(claimIds), "批量领取对象宽度与命名空间");
        Reject(() => MailboxRequests.Extract(Bytes(w => { w.Write(2); w.Write(1); w.Write(1); })), "重复领取对象拒绝");
        Reject(() => MailboxRequests.Extract(Bytes(w => w.Write(101))), "批量数量受限");
        var state = MailboxRequests.State([.. idsBody, 3, 0]);
        TestCheck.That(state.State == 3 && state.Ids.SequenceEqual(claimIds), "状态请求在对象列表后读 u16");
        var query = MailboxRequests.Query(Bytes(w => { String(w, "Receiver"); w.Write((byte)1); }));
        TestCheck.That(query == ("Receiver", (byte)1), "收件人查询保留模式");
        MailboxRequests.Open([]);
        Reject(() => MailboxRequests.Open([0]), "打开邮箱长度严格");

        var received = new MailboxClaimChange[] { new(1, 1), new(2, 0x40000001) };
        TestCheck.Bytes(MailboxPackets.Extract(received), Bytes(w => { w.Write((byte)1); w.Write(2); w.Write(1); w.Write(1); w.Write(0); w.Write(2); w.Write(0x40000001); w.Write(0); }),
            "ACK95 每行实际邮件 key、摘要 key 和单项成功码，不能写保留零");
        var pending = new Dictionary<uint, HashSet<uint>> { [7] = [0x40000001, 0x40000002] };
        using (var ackStream = new MemoryStream(MailboxPackets.Extract([new(7, 0x40000002), new(7, 0x40000001)])))
        using (var ackReader = new BinaryReader(ackStream))
        {
            TestCheck.That(ackReader.ReadByte() == 1 && ackReader.ReadUInt32() == 2, "领取实际行数可以大于整封请求数");
            for (int row = 0; row < 2; row++)
            {
                uint mailKey = ackReader.ReadUInt32(), summaryKey = ackReader.ReadUInt32(), error = ackReader.ReadUInt32();
                TestCheck.That(error == 0 && pending.TryGetValue(mailKey, out var pendingEntries) && pendingEntries.Remove(summaryKey),
                    "独立模拟原码按合并 key 查邮件，再按摘要 key 去除实际附件");
            }
            TestCheck.That(pending[7].Count == 0 && ackStream.Position == ackStream.Length, "整封的多条领取回执完整更新客户端对象");
        }
        TestCheck.Bytes(MailboxPackets.State([new(1, 2), new(2, 3)]), [1, 2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 2, 0, 0, 0, 3, 0], "ACK134 count/u32 与逐行 u32/u16");
        pending[7] = [0x40000001, 0x40000002];
        using (var ackStream = new MemoryStream(MailboxPackets.Extract([new(7, 0x40000001), new(7, 0x40000002, MailboxError.InventoryFull)])))
        using (var ackReader = new BinaryReader(ackStream))
        {
            TestCheck.That(ackReader.ReadByte() == 1 && ackReader.ReadUInt32() == 2, "部分领取保留逐项结果，不伪造整封全部成功");
            for (int row = 0; row < 2; row++)
            {
                uint mailKey = ackReader.ReadUInt32(), summaryKey = ackReader.ReadUInt32(), error = ackReader.ReadUInt32();
                if (error == 0) pending[mailKey].Remove(summaryKey);
                else TestCheck.That(error == 4, "容量失败按本版 ACK95 单项错误码返回");
            }
            TestCheck.That(pending[7].SetEquals([0x40000002]) && ackStream.Position == ackStream.Length,
                "原生逐项消费后保留失败附件，不能清空剩余邮件对象");
        }
        TestCheck.Bytes(MailboxPackets.Result(MailboxResult.Reject(MailboxError.ExpiredItem)), [0, 235, 0], "失败错误码宽度为 u16");
        TestCheck.Bytes(MailboxPackets.Result(MailboxResult.Reject(MailboxError.MailMissing), true), [0, 21, 0], "重复领取明确失败");
        TestCheck.Bytes(MailboxPackets.List(new([], 0), 1000).Single(), new byte[6], "空列表完整两区段与剩余数量");

        var item = new InventoryItem(100, new(InventoryListType.Main, 9), new(101010912, ItemKind.Equipment, 999999998)
        { Attr = 37, Durability = 42, EnchantCardId = 7, EnchantUpgrade = 2, AmplifyType = 1, AmplifyValue = 22, Rune = 12,
            TradeRestriction = 3, Extension97 = 77, Extension121 = 55 });
        var entry = new MailboxEntry(1, 0, 2, 3, "Sender", "中文", 12, 1000, 2000, 1, false, [new(1, item)]);
        CheckList(MailboxPackets.List(new([entry], 5), 1100), [entry], 5, 1100);
        var avatar = new InventoryItem(101, new(InventoryListType.Avatar, 0), new(10001, ItemKind.Avatar, 101))
        { Avatar = new() { Color1 = 9, Socket0 = new(2, 999) } };
        var pet = new InventoryItem(102, new(InventoryListType.Pet, 0), new(10002, ItemKind.Creature, 102)) { Creature = new() { Name = "宠物", Experience = 222 } };
        var special = entry with { Id = 2, Attachments = [new(2, avatar), new(3, pet)] };
        CheckList(MailboxPackets.List(new([special], 0), 1100), [special], 0, 1100);
        TestCheck.That(avatar.Core.Value == 101 && avatar.Avatar.Socket0.EmblemId == 999 && pet.Creature.Experience == 222,
            "预览编码不改持久核心或详情");
        var full = Enumerable.Range(1, 30).Select(id => entry with { Id = (uint)id,
            Attachments = Enumerable.Range(1, 10).Select(index => new MailboxAttachment((uint)(id * 10 + index), item)).ToImmutableArray() }).ToImmutableArray();
        var batches = MailboxPackets.List(new(full, 0), 1100);
        TestCheck.That(batches.Count == 2 && batches[0][1] == 0 && batches[1][1] == 1, "超过 u8 摘要数量时完整分批，首次清空后续追加");
        CheckList(batches, full, 0, 1100);
        var large = full.Select(mail => mail with { SenderName = new('S', 29), Text = new('T', 511),
            Attachments = mail.Attachments.Select(attachment => attachment with { Item = avatar }).ToImmutableArray() }).ToImmutableArray();
        var largeBatches = MailboxPackets.List(new(large, 0), 1100);
        TestCheck.That(largeBatches.Count > 1 && largeBatches.All(packet => packet.Length <= 65520), "十件时装、最长发送者/正文按实际帧字节上限分包");
        CheckList(largeBatches, large, 0, 1100);

        TestCheck.That(MailboxService.Fee(0, 0) == 100 && MailboxService.Fee(0, 1) == 1100 &&
            MailboxService.Fee(200_020, 0) == 10_101 && MailboxService.Fee(uint.MaxValue, 10) == 214_758_464,
            "邮费固定100、每附件1000、金币5%无统一封顶及大整数无溢出");
        TestCheck.That(MailboxService.Fee(19, 2, 19) == 2100 && MailboxService.Fee(20, 2, 20) == 2102 &&
            MailboxService.Fee(10, 2, 10) == 2100 && MailboxService.Fee(0, 2, 22) == 2101,
            "金币与附件5%分别取整，附件合计取整而非逐件取整");
        TestCheck.That(MailboxService.Fee(0, 10, 4_611_686_014_132_420_609) == 230_584_300_706_631_130,
            "大金额附件手续费宽整数计算，不截断或回绕为便宜邮费");
        TestCheck.That(MailboxService.DailyGoldLimit(1) == 10_000 && MailboxService.DailyGoldLimit(95) == 90_250_000,
            "角色每日寄金币额度");
        TestCheck.That(MailboxService.Day(new(2026, 10, 7, 21, 59, 59, TimeSpan.Zero)) == 20261007 &&
            MailboxService.Day(new(2026, 10, 7, 22, 0, 0, TimeSpan.Zero)) == 20261008, "UTC+8 六点日边界");
        var template = new ItemTemplate(101010912, ItemKind.Equipment, 12, 1, 42, 1, 1, 0, 1, "[free]", 65535, 0, "测试");
        var clean = item with { Core = item.Core with { TradeRestriction = 0 } };
        TestCheck.That(MailboxService.TransferError(clean, template, "a", "b", 1000) == MailboxError.None, "自由交易完整实例允许");
        foreach (var (candidate, metadata, error) in new (InventoryItem, ItemTemplate, MailboxError)[]
        {
            (item, template, MailboxError.NotTradable),
            (clean with { Core = clean.Core with { SortLockFlag = 1 } }, template, MailboxError.LockedItem),
            (clean with { Core = clean.Core with { EquipmentLockId = 2 } }, template, MailboxError.LockedItem),
            (clean with { Core = clean.Core with { ExpireTime = 900 } }, template, MailboxError.ExpiredItem),
            (clean with { Core = clean.Core with { ExpireTime = 2000 } }, template, MailboxError.LimitedPeriod),
            (clean, template with { AttachType = "[account]" }, MailboxError.AccountBound),
            (clean, template with { AttachType = "[sealing]" }, MailboxError.NotTradable),
            (clean, template with { AttachType = "[unknown]" }, MailboxError.NotTradable),
            (clean with { Core = clean.Core with { Attr = 0 } }, template with { AttachType = "[trade limit]" }, MailboxError.NotTradable)
        }) TestCheck.That(MailboxService.TransferError(candidate, metadata, "a", "b", 1000) == error, "寄送资格拒绝 " + error);
        TestCheck.That(MailboxService.TransferError(clean, template with { AttachType = "[account]" }, "a", "a", 1000) == MailboxError.None &&
            MailboxService.TransferError(clean, template with { AttachType = "[trade limit]" }, "a", "b", 1000) == MailboxError.None,
            "同账号与有剩余交易次数的实例允许");
        Console.WriteLine($"PASS {TestCheck.Count - before} 邮件结构、列表独立消费与寄送资格断言");
    }

    internal static void CheckList(IReadOnlyList<byte[]> packets, IReadOnlyList<MailboxEntry> entries, int notLoaded, long now)
    {
        var remaining = entries.ToDictionary(mail => mail.Id);
        foreach (var packet in packets)
        {
            using var stream = new MemoryStream(packet);
            using var reader = new BinaryReader(stream);
            int summaries = reader.ReadByte();
            _ = reader.ReadByte();
            for (int index = 0; index < summaries; index++)
            {
                uint claim = reader.ReadUInt32();
                ushort sender = checked((ushort)reader.ReadUInt32());
                string name = ReadString(reader);
                uint gold = reader.ReadUInt32();
                int id = reader.ReadInt32();
                byte has = reader.ReadByte();
                int value = reader.ReadInt32();
                ushort durability = reader.ReadUInt16();
                byte attr = reader.ReadByte();
                int enchant = reader.ReadInt32();
                byte upgrade = reader.ReadByte();
                byte amplify = reader.ReadByte();
                ushort amplifyValue = reader.ReadUInt16();
                var attachment = entries.SelectMany(mail => mail.Attachments).FirstOrDefault(item => item.ClaimId == claim);
                if (attachment?.Item.Creature is not null) { _ = reader.ReadInt32(); TestCheck.That(reader.ReadByte() == 0, "宠物摘要扩展 flag0"); }
                byte[] raw = reader.ReadBytes(123);
                TestCheck.That(raw.Length == 123, "摘要复制完整固定核心");
                if (attachment?.Item.Avatar is not null)
                {
                    TestCheck.That(reader.ReadInt32() == 30 && reader.ReadBytes(30).Length == 30 && reader.ReadInt32() == 4 && reader.ReadBytes(4).Length == 4,
                        "时装两个长度前缀块");
                }
                int seconds = reader.ReadInt32();
                uint key = reader.ReadUInt32();
                TestCheck.That(reader.ReadByte() == 0 && remaining.ContainsKey(key), "摘要尾部合并 key 与额外字节");
                var mail = remaining[key];
                TestCheck.That(sender == mail.SenderId && name == mail.SenderName && seconds == mail.RemainingSeconds(now) && gold <= mail.Gold,
                    "摘要的发送者、金币和期限独立核对");
                if (attachment is not null)
                    TestCheck.That(id == attachment.Item.Core.ItemId && has == 1 && value == (attachment.Item.Avatar is null ? attachment.Item.Core.Value : attachment.Item.RemainingSeconds(now)) &&
                        durability == attachment.Item.Core.Durability && attr == attachment.Item.Core.Attr &&
                        enchant == (attachment.Item.Core.IsStackable ? attachment.Item.Core.Count : attachment.Item.Core.EnchantCardId) &&
                        upgrade == attachment.Item.Core.EnchantUpgrade && amplify == attachment.Item.Core.AmplifyType && amplifyValue == attachment.Item.Core.AmplifyValue &&
                        BitConverter.ToInt32(raw, 2) == id && BitConverter.ToUInt16(raw, 69) == attachment.Item.Core.Rune && raw[89] == attachment.Item.Core.TradeRestriction,
                        "摘要头与核心关键属性独立偏移对照");
            }
            TestCheck.That(reader.ReadUInt16() == Math.Min(short.MaxValue, notLoaded), "剩余未装载数量");
            int details = reader.ReadUInt16();
            for (int index = 0; index < details; index++)
            {
                uint id = reader.ReadUInt32();
                var mail = remaining[id];
                TestCheck.That(reader.ReadUInt32() == mail.SenderId && ReadString(reader) == mail.SenderName && ReadString(reader) == mail.Text &&
                    reader.ReadInt32() == mail.Created && reader.ReadUInt16() == mail.State && reader.ReadByte() == 0, "正文独立解码");
                remaining.Remove(id);
            }
            TestCheck.That(stream.Position == stream.Length, "列表完整消费，没有漏读或错位");
        }
        TestCheck.That(remaining.Count == 0, "分批正文全部交付");
    }
    static string ReadString(BinaryReader reader) => CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
    static void Reject(Action action, string reason)
    {
        try { action(); }
        catch (InvalidDataException) { TestCheck.That(true, reason); return; }
        throw new InvalidOperationException("FAIL: " + reason);
    }
}
