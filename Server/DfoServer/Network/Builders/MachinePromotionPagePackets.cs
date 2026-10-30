using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DfoServer.Game.CashShopPromotions;

namespace DfoServer.Network.Builders;

internal static class MachinePromotionPagePackets
{
    const string Title = "CP_PROMO/1";
    const int BodyMaxBytes = 4000;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static readonly int[] RewardIds = [490705441, 490705442, 490705443, 490705444, 490705445,
        490705446, 490705447, 490705448, 490705449];

    internal static byte[] Snapshot(string nonce, ushort characterId, PromotionPageSnapshot snapshot)
    {
        if (!Nonce(nonce) || snapshot is null ||
            snapshot.Campaign != "spring-2020" || snapshot.Period != "replica-1" ||
            snapshot.RulesVersion != "v1" || snapshot.Realm != "default" || snapshot.Group != "package" ||
            snapshot.CatalogRevision < 0 || snapshot.ProgressRevision is < 0 || snapshot.Count is < 0 ||
            snapshot.Count.HasValue != snapshot.ProgressRevision.HasValue ||
            snapshot.MaximumPurchases != 5000 || snapshot.TtlMs is < 1 or > 10000 ||
            snapshot.Status is not ("ready" or "disabled" or "unavailable"))
            throw Invalid();

        string rewards;
        string overflowReward;
        if (snapshot.Status == "unavailable")
        {
            if (snapshot.AwardMask27 is not null || snapshot.OverflowAwardCount is not null ||
                !snapshot.Rewards.IsDefaultOrEmpty || snapshot.OverflowReward is not null)
                throw Invalid();
            rewards = overflowReward = "-";
        }
        else
        {
            if (snapshot.Count is not long count || snapshot.ProgressRevision is null ||
                snapshot.AwardMask27 is not uint mask || snapshot.OverflowAwardCount is not long overflow ||
                snapshot.Rewards.IsDefault || snapshot.Rewards.Length != 9 ||
                snapshot.OverflowReward is null)
                throw Invalid();
            // 这里只拒绝不一致的既有事实，不用累计数制造奖项或补写账本。
            uint expectedMask = 0;
            for (int round = 0; round < 3; round++)
                for (int tier = 2; tier <= 10; tier++)
                    if (count >= tier + round * 10)
                        expectedMask |= 1u << (round * 9 + tier - 2);
            if (mask != expectedMask || overflow != (count > 30 ? count - 30 : 0))
                throw Invalid();

            string[] records = new string[9];
            for (int i = 0; i < records.Length; i++)
                records[i] = Record(snapshot.Rewards[i], RewardIds[i]);
            rewards = string.Join(';', records);
            overflowReward = Record(snapshot.OverflowReward, 490705415);
        }

        string body = string.Join('|', "1", "snapshot", nonce, Number(characterId),
            Number(snapshot.CatalogRevision), Number(snapshot.ProgressRevision), snapshot.Campaign,
            snapshot.Period, snapshot.RulesVersion, snapshot.Realm, snapshot.Group, snapshot.Status,
            Number(snapshot.Count), "5000", snapshot.AwardMask27?.ToString(CultureInfo.InvariantCulture) ?? "-",
            Number(snapshot.OverflowAwardCount), Number(snapshot.TtlMs), rewards, overflowReward);
        return Envelope(body);
    }

    internal static byte[] Changed(string nonce, ushort characterId, long catalogRevision, long progressRevision)
    {
        if (!Nonce(nonce) || catalogRevision < 0 || progressRevision < 0)
            throw Invalid();
        return Envelope(string.Join('|', "1", "changed", nonce, Number(characterId),
            Number(catalogRevision), Number(progressRevision)));
    }

    static bool Nonce(string value) => value is { Length: 32 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    static string Number(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    static string Record(PromotionPageReward reward, int expectedId)
    {
        if (reward is null || reward.ItemId != expectedId || reward.Count != 1 ||
            string.IsNullOrWhiteSpace(reward.Name) || reward.Name[0] == (char)0xfeff ||
            reward.Name.Any(c => c <= (char)0x1f || c is >= (char)0x7f and <= (char)0x9f))
            throw Invalid();
        byte[] name;
        try
        {
            if (StrictUtf8.GetByteCount(reward.Name) > 192)
                throw Invalid();
            name = StrictUtf8.GetBytes(reward.Name);
        }
        catch (EncoderFallbackException error)
        {
            throw new InvalidDataException("活动奖励名称不是合法Unicode", error);
        }
        string encoded = Convert.ToBase64String(name).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Number(reward.ItemId) + ",1," + encoded;
    }

    static byte[] Envelope(string body)
    {
        // 专用机器帧在原AAS显示器前消费，不能调用会按600B截断的普通公告Builder。
        if (body.Length is < 1 or > BodyMaxBytes || body.Any(c => c > 127 || c == 0 || c == '\r' || c == '\n'))
            throw Invalid();
        byte[] result = new byte[19 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 10);
        Encoding.ASCII.GetBytes(Title).CopyTo(result, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(14), (uint)body.Length);
        Encoding.ASCII.GetBytes(body).CopyTo(result, 18);
        return result;
    }

    static InvalidDataException Invalid() => new("机器活动字段不满足版本1合同");
}