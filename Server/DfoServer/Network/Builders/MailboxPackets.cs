using DfoServer.Game.Mailbox;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

internal static class MailboxPackets
{
    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
    public static byte[] Result(MailboxResult result, bool extracting = false, bool state = false) => result.Accepted ? [1] : Build(writer =>
    {
        writer.Write((byte)0);
        int error = extracting ? result.Error switch
        {
            MailboxError.GoldCarryLimit => 10, MailboxError.InvalidState => 19, MailboxError.MailMissing => 21, _ => 4
        } : result.Error switch
        {
            MailboxError.RecipientMissing => 3, MailboxError.SelfSend => 7, MailboxError.InsufficientGold => 10,
            MailboxError.RecipientGoldLimit => 14, MailboxError.Empty => 22, MailboxError.NotTradable => 23,
            MailboxError.DailyGoldLimit => 70, MailboxError.InvalidState => 114, MailboxError.LockedItem => 214,
            MailboxError.AccountBound => 217, MailboxError.LimitedPeriod => 219, MailboxError.ExpiredItem => 235,
            MailboxError.StorageFull when state => 22, _ => 17
        };
        writer.Write(checked((ushort)error));
    });
    public static byte[] Open(MailboxPage page) => Build(writer => { writer.Write((byte)1); writer.Write((ushort)Math.Min(short.MaxValue, page.NotLoaded)); });
    public static byte[] Extract(IReadOnlyList<MailboxClaimChange> changes) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(changes.Count);
        foreach (var change in changes) { writer.Write(change.MailId); writer.Write(change.ClaimId); writer.Write(0); }
    });
    public static byte[] State(IReadOnlyList<MailboxStateChange> changes) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(changes.Count);
        foreach (var change in changes) { writer.Write(change.Id); writer.Write(change.State); }
    });
    public static byte[] Remove(IReadOnlyList<uint> ids) => Build(writer =>
    {
        writer.Write(ids.Count);
        foreach (uint id in ids) writer.Write(id);
    });
    public static byte[] Alarm() => [1, 0];
    public static byte[] Query(MailboxResult result) => !result.Accepted ? Result(result) : Build(writer =>
    {
        var recipient = result.Recipient ?? throw new InvalidDataException("邮件查询缺少收件人");
        writer.Write((byte)1);
        PacketEncoding.WriteString(writer, recipient.Name);
        writer.Write((byte)0); // 本服 server 标记，0234D0F9 读；不是 growType。
        writer.Write((ushort)recipient.Level);
        writer.Write(recipient.Job);
        writer.Write(recipient.GrowType);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((byte)0); // 删除/限制标记；仅查询存在的角色。
    });

    public static IReadOnlyList<byte[]> List(MailboxPage page, long utcSeconds)
    {
        var batches = new List<byte[]>();
        var entries = new List<MailboxEntry>();
        int summaries = 0;
        int bytes = 6;
        foreach (var mail in page.Entries)
        {
            int count = Math.Max(1, mail.Attachments.Length);
            int nameBytes = DfoServer.Game.Characters.CharacterName.Encoding.GetByteCount(mail.SenderName);
            int size = 23 + nameBytes + DfoServer.Game.Characters.CharacterName.Encoding.GetByteCount(mail.Text) +
                (168 + nameBytes) * count + mail.Attachments.Sum(attachment => attachment.Item.Avatar is not null ? 42 : attachment.Item.Creature is not null ? 5 : 0);
            if (count > byte.MaxValue || size > 65514)
                throw new InvalidDataException("单封邮件超过本版帧上限");
            if (summaries + count > byte.MaxValue || bytes + size > 65520)
            {
                batches.Add(ListBatch(entries, summaries, batches.Count != 0, page.NotLoaded, utcSeconds));
                entries.Clear();
                summaries = 0;
                bytes = 6;
            }
            entries.Add(mail);
            summaries += count;
            bytes += size;
        }
        if (entries.Count != 0 || batches.Count == 0)
            batches.Add(ListBatch(entries, summaries, batches.Count != 0, page.NotLoaded, utcSeconds));
        return batches;
    }

    static byte[] ListBatch(IReadOnlyList<MailboxEntry> entries, int summaries, bool append, int notLoaded, long now) => Build(writer =>
    {
        writer.Write(checked((byte)summaries));
        writer.Write(append ? (byte)1 : (byte)0); // 023E0372：0 清空，1 追加。
        foreach (var mail in entries)
        {
            if (mail.Attachments.IsEmpty)
                Summary(writer, mail, null, mail.Gold, now);
            else
                for (int index = 0; index < mail.Attachments.Length; index++)
                    Summary(writer, mail, mail.Attachments[index], index == 0 ? mail.Gold : 0, now);
        }
        writer.Write((ushort)Math.Min(short.MaxValue, notLoaded));
        writer.Write(checked((ushort)entries.Count));
        foreach (var mail in entries)
        {
            writer.Write(mail.Id);
            writer.Write((uint)mail.SenderId);
            PacketEncoding.WriteString(writer, mail.SenderName);
            PacketEncoding.WriteString(writer, mail.Text);
            writer.Write(checked((int)mail.Created));
            writer.Write(mail.State);
            writer.Write((byte)0);
        }
    });

    static void Summary(BinaryWriter writer, MailboxEntry mail, MailboxAttachment? attachment, uint gold, long now)
    {
        var item = attachment?.Item;
        var core = item?.Core;
        if (item?.Avatar is not null)
            core = core! with { Value = item.RemainingSeconds(now) };
        else if (item?.Creature is not null)
            core = core! with { Marker16 = item.RemainingSeconds(now) };
        writer.Write(attachment?.ClaimId ?? mail.Id);
        writer.Write((uint)mail.SenderId);
        PacketEncoding.WriteString(writer, mail.SenderName);
        writer.Write(gold);
        writer.Write(core?.ItemId ?? 0);
        writer.Write(item is null ? (byte)0 : (byte)1);
        writer.Write(core?.Value ?? 0);
        writer.Write(core?.Durability ?? 0);
        writer.Write(core?.Attr ?? 0);
        writer.Write(core?.IsStackable == true ? core.Count : core?.EnchantCardId ?? 0);
        writer.Write(core?.EnchantUpgrade ?? 0);
        writer.Write(core?.AmplifyType ?? 0);
        writer.Write(core?.AmplifyValue ?? 0);
        if (item?.Creature is not null)
        {
            // 本版 type26 在固定核心前读 i32 + flag；flag0 不消费三个成长扩展。
            writer.Write(core!.Marker16);
            writer.Write((byte)0);
        }
        if (core is null)
            writer.Write(new byte[ItemListProtocolWriter.EntrySize]);
        else
            ItemListProtocolWriter.Write(writer, 0, core);
        if (item?.Avatar is not null)
        {
            ItemListProtocolWriter.WriteAvatarSockets(writer, item.Avatar);
            ItemListProtocolWriter.WriteAvatarColors(writer, item.Avatar);
        }
        // Rune/Random 来自上述核心；024B2ED0、02291830 只复制对象状态，不再读包。
        writer.Write(mail.RemainingSeconds(now));
        writer.Write(mail.Id);
        writer.Write((byte)0);
    }
}
