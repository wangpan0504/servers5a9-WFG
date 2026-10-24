using System.Buffers.Binary;
using DfoServer.Game.Dungeons;

namespace DfoServer.Network.Builders;

internal static class DungeonAltarPackets
{
    // V31 0237E450只读六个u32；208为本次计时/回合、历史最佳和本次经验，不是普通评分quartet。
    public static byte[] Ranking(uint playMilliseconds, uint rounds, uint bestMilliseconds, uint bestRounds,
        uint maximumRounds, uint experience)
    {
        if (maximumRounds is < 1 or > byte.MaxValue || rounds > maximumRounds || bestRounds > maximumRounds)
            throw new ArgumentOutOfRangeException(nameof(rounds));
        byte[] body = new byte[24];
        uint[] fields = [playMilliseconds, rounds, bestMilliseconds, bestRounds, maximumRounds, experience];
        for (int at = 0; at < fields.Length; at++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at * 4), fields[at]);
        return body;
    }

    // V31 023801B0在两个u8后固定遍历四个队员槽；每项为itemId:u32、addInfo:u32。
    public static byte[] ClearRewards(byte completedRounds, byte maximumRounds,
        IReadOnlyList<IReadOnlyList<(uint ItemId, uint AddInfo)>> members)
    {
        if (maximumRounds == 0 || completedRounds > maximumRounds || members.Count != 4 ||
            members.Any(member => member.Count > byte.MaxValue)) throw new ArgumentOutOfRangeException(nameof(members));
        byte[] body = new byte[checked(6 + members.Sum(member => member.Count) * 8)];
        body[0] = completedRounds;
        body[1] = maximumRounds;
        int at = 2;
        foreach (var member in members)
        {
            body[at++] = checked((byte)member.Count);
            foreach (var (itemId, info) in member)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at), itemId);
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(at + 4), info);
                at += 8;
            }
        }
        return body;
    }

    // V31 023B0F80起读取u32副本、难度、maze、Boss坐标；尾部练习与随机增益仍由专用读端消费。
    public static byte[] Information(DungeonEntryPlan entry)
    {
        byte[] body = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(body, entry.DungeonId);
        body[4] = entry.Difficulty;
        body[5] = checked((byte)entry.Maze.SourceOrdinal);
        body[6] = checked((byte)entry.BossMap.Position.X);
        body[7] = checked((byte)entry.BossMap.Position.Y);
        body[10] = entry.IsPractice ? (byte)1 : (byte)0;
        body[11] = 0x0B;
        return body;
    }

    // V31 02390810的首次地图通知；波次演员走210，211保留空特殊演员/掉落及fog。
    public static byte[] StartMap(DungeonEntryPlan entry, uint seed, DungeonMapPayload payload = DungeonMapPayload.Build)
    {
        byte[] body = new byte[payload == DungeonMapPayload.Cached ? 8 : 15];
        body[0] = checked((byte)entry.StartMap.Position.X);
        body[1] = checked((byte)entry.StartMap.Position.Y);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(2), entry.StartMap.Id);
        if (payload == DungeonMapPayload.Cached) return body;
        body[7] = (byte)payload;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), seed);
        return body;
    }
    // V31 0237E530读count:u16和17字节记录；旧端08305992确认rank在level之前。
    public static byte[] Spawn(DungeonAltarBatch batch)
    {
        byte[] body = new byte[checked(4 + batch.Actors.Length * 17)];
        BinaryPrimitives.WriteUInt16LittleEndian(body, checked((ushort)batch.Actors.Length));
        int at = 2;
        foreach (var actor in batch.Actors)
        {
            body[at] = actor.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 1), actor.ObjectKey);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at + 3), actor.Phase.MonsterId);
            body[at + 7] = actor.Rank;
            body[at + 8] = actor.Level;
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 9), actor.Phase.Scale);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 11), actor.Phase.X);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 13), actor.Phase.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at + 15), actor.Phase.Z);
            at += 17;
        }
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at), batch.Interval);
        return body;
    }

    // 0237E7A0；回合号从1开始，时长以毫秒计。不能把本房round直接当作三房总进度。
    public static byte[] RoundInterval(int round, uint milliseconds)
    {
        byte[] body = new byte[5];
        body[0] = checked((byte)(round + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1), milliseconds);
        return body;
    }

    // 023D0F00：0分支创建限时选择UI，非0分支把已选难度传给现有UI的039727E0。
    public static byte[] DifficultyCountdown(int round, ushort seconds) =>
        [0, checked((byte)round), (byte)seconds, (byte)(seconds >> 8)];
    public static byte[] SelectedDifficulty(byte difficulty) => difficulty is >= 1 and <= 5
        ? [difficulty] : throw new ArgumentOutOfRangeException(nameof(difficulty));

    public static byte[] SpecialMonster(DungeonAltarActor actor)
    {
        if (actor.Kind >= 19) throw new ArgumentException("普通祭坛怪不能发布特殊提示", nameof(actor));
        byte[] body = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(body, actor.Phase.MonsterId);
        body[4] = actor.Kind;
        return body;
    }
}
