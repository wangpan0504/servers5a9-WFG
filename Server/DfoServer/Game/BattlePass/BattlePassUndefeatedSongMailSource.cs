using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.BattlePass;

internal sealed record BattlePassUndefeatedSongMailFact(string SourceId, int Amount);

// 只在原附件实际取得时计数；整封或单附件请求的未成功前缀不属于本次取得。
internal static class BattlePassUndefeatedSongMailSource
{
    public const string SourceKind = "mail_undefeated_song_acquisition";

    public static BattlePassUndefeatedSongMailFact? Prepare(ushort character, MailboxClaimPlan plan)
    {
        var songs = plan.Items.Where(item => item.Core.ItemId == BattlePassUndefeatedSongSource.ItemId).ToArray();
        if (songs.Length == 0) return null;
        if (character == 0 || plan.Inventory.Reason != "mail_claim")
            throw new InvalidDataException("不渝之咏邮件取得身份无效");
        long declared = 0; var seen = new HashSet<long>(); var identities = new List<(long Uid, string Text)>();
        foreach (var item in songs)
        {
            if (item.Uid <= 0 || !seen.Add(item.Uid) || item.Core.Kind != ItemKind.Material || item.Core.Count <= 0)
                throw new InvalidDataException("不渝之咏实际邮件附件核心无效");
            var matches = plan.Claims.SelectMany(claim =>
                (claim.Attachment is { } selected ? [selected] : claim.Mail.Attachments)
                .Where(attachment => attachment.Item.Uid == item.Uid)
                .Select(attachment => (claim.Mail, Attachment: attachment))).ToArray();
            if (matches is not [var original] || original.Mail.RecipientId != character ||
                original.Mail.Id == 0 || original.Mail.Revision < 0 || original.Attachment.Id == 0 ||
                original.Attachment.Item != item || !plan.Results.Any(result => result.Error == MailboxError.None &&
                    result.MailId == original.Mail.Id && result.ClaimId == original.Attachment.ClaimId))
                throw new InvalidDataException("不渝之咏邮件实际取得没有原成功附件资格");
            declared = checked(declared + item.Core.Count);
            identities.Add((item.Uid, FormattableString.Invariant($"{original.Mail.Id}:{original.Mail.Revision}:{original.Attachment.Id}:{original.Attachment.ClaimId}:{item.Uid}:{item.Core.Count}")));
        }
        int acquired = AcquiredAmount(plan.Inventory);
        if (declared != acquired || acquired <= 0)
            throw new InvalidDataException("不渝之咏原附件数量与实际入包增量不一致");
        string identity = FormattableString.Invariant($"{character}:") + string.Join(";", identities.OrderBy(row => row.Uid).Select(row => row.Text));
        string source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SourceKind + ":" + identity)).AsSpan(0, 16)).ToLowerInvariant();
        return new(source, acquired);
    }

    public static int AcquiredAmount(InventoryPlan plan)
    {
        if (plan.Reason != "mail_claim") throw new InvalidDataException("不渝之咏邮件取得必须沿原提取事务");
        long physical = checked(MainAmount(plan.After) - MainAmount(plan.Before));
        long account = (long)plan.After.AccountWallet.Material(BattlePassUndefeatedSongSource.ItemId).Balance -
            plan.Before.AccountWallet.Material(BattlePassUndefeatedSongSource.ItemId).Balance;
        if (physical < 0 || account < 0) throw new InvalidDataException("不渝之咏邮件实际增量无效");
        long amount = checked(physical + account);
        if (amount > int.MaxValue) throw new InvalidDataException("不渝之咏邮件实际增量无效");
        return checked((int)amount);
    }

    static long MainAmount(InventorySnapshot inventory)
    {
        long amount = 0;
        foreach (var item in inventory.Items.Where(item => item.Position.List == InventoryListType.Main && item.Core.ItemId == BattlePassUndefeatedSongSource.ItemId))
        {
            if (item.Core.Kind != ItemKind.Material || item.Core.Count <= 0)
                throw new InvalidDataException("不渝之咏邮件实物核心无效");
            amount = checked(amount + item.Core.Count);
        }
        return amount;
    }
}
