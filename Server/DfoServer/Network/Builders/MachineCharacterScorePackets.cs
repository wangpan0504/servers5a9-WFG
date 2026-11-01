using System.Globalization;

namespace DfoServer.Network.Builders;

// 本服客户端扩展的机器文本合同；任何无效字段拒绝，不能截断后伪作有效帧。
internal static class MachineCharacterScorePackets
{
    internal const string Title = "CP_SCORE/1";
    internal const string CombatTitle = "CP_SCORE/2";
    internal static bool Nonce(string value) => value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool Token(string value, int maximum) => value.Length is > 0 && value.Length <= maximum &&
        value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '/' or '-');

    internal static byte[] Build(string nonce, ushort characterId, long revision, string profile, string version,
        string status, int? score, bool capped, int ttlMs)
    {
        if (!Nonce(nonce) || revision < 0 || !Token(profile, 48) || !Token(version, 32) || ttlMs is < 1 or > 10000 ||
            status is not ("complete" or "partial" or "unrated") ||
            (status == "unrated" ? score is not null || capped : score is null or < 0))
            throw new InvalidDataException("机器评分字段不满足版本1合同");
        string text = string.Join('|', "1", nonce, characterId.ToString(CultureInfo.InvariantCulture),
            revision.ToString(CultureInfo.InvariantCulture), profile, version, status,
            score?.ToString(CultureInfo.InvariantCulture) ?? "-", capped ? "1" : "0", ttlMs.ToString(CultureInfo.InvariantCulture));
        if (text.Length > ServerNoticePackets.BodyMaxBytes) throw new InvalidDataException("机器评分帧超限");
        var body = ServerNoticePackets.Build(Title, text, out bool truncated);
        if (truncated) throw new InvalidDataException("机器评分帧不得截断");
        return body;
    }

    // 版本2：官方风格战斗力乘区；百分比字段为基点（1% = 100），grade 为 0..9 段位序号。
    // 2|nonce|charId|rev|cp-official|v1|status|total|base|white_bp|yellow_bp|crit_bp|equip_bp|grade|ttl
    internal static byte[] BuildCombat(string nonce, ushort characterId, long revision, string status, int total, int baseScore,
        int whiteBp, int yellowBp, int critBp, int equipBp, int grade, int ttlMs)
    {
        if (!Nonce(nonce) || revision < 0 || ttlMs is < 1 or > 10000 || status is not ("complete" or "partial") ||
            total < 0 || baseScore < 0 || whiteBp < 0 || yellowBp < 0 || critBp < 0 || equipBp < 0 || grade is < 0 or > 9)
            throw new InvalidDataException("机器战斗力字段不满足版本2合同");
        string I(long v) => v.ToString(CultureInfo.InvariantCulture);
        string text = string.Join('|', "2", nonce, I(characterId), I(revision), Game.Characters.CombatPowerScore.Profile,
            Game.Characters.CombatPowerScore.Version, status, I(total), I(baseScore), I(whiteBp), I(yellowBp), I(critBp), I(equipBp),
            I(grade), I(ttlMs));
        if (text.Length > ServerNoticePackets.BodyMaxBytes) throw new InvalidDataException("机器战斗力帧超限");
        var body = ServerNoticePackets.Build(CombatTitle, text, out bool truncated);
        if (truncated) throw new InvalidDataException("机器战斗力帧不得截断");
        return body;
    }
}
