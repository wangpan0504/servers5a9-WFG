using static DfoServer.Testing.TestCheck;
using DfoServer.Development.Dungeons;
using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using PvfLib;
using DfoServer.Development;
using DfoServer.Network.Parsers;

namespace DfoServer;

internal static class DungeonDeathProtocolTests
{
    public static void Run()
    {
        int checks = Count;

        byte[] body = Enumerable.Range(0, 88).Select(index => (byte)(index * 17)).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xFEDCBA98);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 0x8123);
        That(DungeonRequests.TryParseDeath(body, out var request) && request is not null,
            "exact 88-byte death report is recognized");
        That(request!.ObjectKey == 0xFEDCBA98 && request.Reporter == 0x8123,
            "parser preserves full unsigned object key and reporter without narrowing");
        byte[] originalTail = body[6..];
        That(request.RemainingFields.Count == 82 && request.RemainingFields.SequenceEqual(originalTail),
            "all 82 unknown statistics bytes are retained in order");
        body[6] ^= 255;
        That(request.RemainingFields.SequenceEqual(originalTail), "parsed statistics own their bytes");
        foreach (int length in new[] { 0, 6, 87, 89, 100 })
            That(!DungeonRequests.TryParseDeath(new byte[length], out var invalid) && invalid is null,
                "death parser rejects unsupported body width " + length);
        foreach (int contextLength in new[] { 0,22 })
        {
            // 独立按生产者调用顺序编码；029083A0仅在上下文非空时追加u64和七个u16。
            using var encoded = new MemoryStream();
            using (var writer = new BinaryWriter(encoded, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0xF2345678u); writer.Write(ushort.MaxValue); writer.Write(new byte[30]);
                writer.Write((byte)0xA5); writer.Write((byte)1); writer.Write((byte)0); writer.Write((ushort)0xFEDC);
                writer.Write((byte)0); writer.Write(0xF1234521u);
                if (contextLength != 0)
                {
                    writer.Write(0xE23456789ABCDEF1ul);
                    for (ushort at = 0; at < 7; at++) writer.Write((ushort)(0xF000 + at));
                }
                writer.Write((byte)0); writer.Write((byte)0xD7);
                writer.Write(ClientBodyChecksum.Compute(encoded.ToArray()));
            }
            byte[] passive = encoded.ToArray();
            byte[] context = passive[46..^6];
            That(DungeonRequests.TryParsePassiveDeath(passive,out var parsed) && passive.Length == 52 + contextLength &&
                parsed is { ObjectIdentifier: 0xF2345678, ActorFlags: 0xF1234521, Unknown36: 0xA5, Unknown39: 0xFEDC,
                    UnknownLast: 0xD7, ChecksumMatches: true } && parsed.ContextFields.SequenceEqual(context),
                "独立被动物件格式保留52/74B、未知高位及可选上下文，不套用88B死亡统计");
            That(!DungeonRequests.TryParseDeath(passive,out _) && !DungeonRequests.TryParseSinglePlayerDeathStatistics(passive,out _),
                "被动物件格式不能产生普通怪物统计或战斗事实");
            passive[0] ^= 1;
            That(DungeonRequests.TryParsePassiveDeath(passive,out var changed) && changed is { ChecksumMatches: false },
                "被动物件正文变化保留摘要不匹配，不签发死亡");
            foreach (int at in new[] { 4,6,30,31,35,37,38,41,passive.Length - 6 })
            {
                byte original = passive[at]; passive[at] ^= 0x80;
                That(!DungeonRequests.TryParsePassiveDeath(passive,out var invalid) && invalid is null,
                    "独立被动物件常量域不能借用其他39生产者 " + at);
                passive[at] = original;
            }
            Array.Fill(passive,(byte)0);
            That(context.SequenceEqual(parsed!.ContextFields), "被动物件上下文冻结副本，不保留可变输入");
        }
        foreach (int length in new[] { 43,48,51,53,70,73,75,88 })
            That(!DungeonRequests.TryParsePassiveDeath(new byte[length],out var invalid) && invalid is null,
                "直接writer长度与部分公共尾部不能变成完整被动物件报告 " + length);
        // zz在3151安全区的实收终末报告：零参与者、真实角色号12，不是FFFF被动物件格式。
        byte[] retreat = Convert.FromHexString("720200000C000000000000000000000000004D51000000000000440000000022038C0200000000AF7A0110000000764B00000000000022038C020000000000000000000000004494B57B");
        That(DungeonRequests.TryParseScriptedDeath(retreat,out var scripted) &&
            scripted is { ObjectKey: 626, Reporter: 12, RewriteFlag: 0x22, ChecksumMatches: true },
            "74B撤退报告保留实际角色号用于审计；剧情通关仍由资源演员及实例事件账本确认");
        retreat[^1] ^= 1;
        That(DungeonRequests.TryParseScriptedDeath(retreat,out scripted) && scripted is { ChecksumMatches: false },
            "撤退演员摘要不匹配不会变成有效场景终末事实");
        // 匹配ARAD已实收的13条单人报文，固定预期独立取自原字节；解析不签发死亡或证明本轮实机击杀。
        // 第七例首个u64及u32非零，不能只验证全零前缀。
        (string Hex, uint Key, ulong First, uint FirstCount, ulong Second, uint SecondCount, ulong Contribution, uint Checksum)[] samples =
        [
            ("010100000100000000000000000000000000C902000000000000020000000111010100430200000000000002001302A5000000000084670003000000F8010000000000002D021801A801BD000C001F0007000000A0DF2458", 257, 0, 0, 713, 2, 579, 0x5824DFA0),
            ("03010000010000000000000000000000000080020000000000000600000001110101005C020000000000000600FB02C60000000000877E0003000000F801000000000000870207015C01C6000C001F000A00000092A06A85", 259, 0, 0, 640, 6, 604, 0x856AA092),
            ("000100000100000000000000000000000000D4020000000000000400000001110101003B020000000000000400DF01E2000000000024010003000000F8010000000000003D02C5001802E2000C001F000C0000000BA70BE1", 256, 0, 0, 724, 4, 571, 0xE10BA70B),
            ("020100000100000000000000000000000000A30200000000000006000000011101010044020000000000000600FB02CC000000000047150003000000F8010000000000008302D0006702CC000C001F0011000000C3DBD2AC", 258, 0, 0, 675, 6, 580, 0xACD2DBC3),
            ("0601000001000000000000000000000000004303000000000000040000000111010100DF0200000000000004003702DE0000000000E14A0003000000F8010000000000009A01DE00BF01DE000C001F00230000005D1058E9", 262, 0, 0, 835, 4, 735, 0xE958105D),
            ("080100000100000000000000000000000000A00200000000000002000000011101010037020000000000000200A902AD0000000000F16D0003000000F8010000000000000A02F4001C02CF000C001F00240000006C67E37D", 264, 0, 0, 672, 2, 567, 0x7DE3676C),
            ("040100000100080000000000000001000000E90200000000000005000000011101010026020000000000000500D302B60000000000A62E0003000000F801000000000000D701D000D101B6000C001F0027000000BFDD7F32", 260, 8, 1, 745, 5, 550, 0x327FDDBF),
            ("0701000001000000000000000000000000005E030000000000000500000001110101005A030000000000000500CA02C400000000003E0F0003000000F801000000000000D501270194010D010C001F002300000028E0604F", 263, 0, 0, 862, 5, 858, 0x4F60E028),
            ("05010000010000000000000000000000000060020000000000000500000001110101004B02000000000000050011014A01000000001C490003000000F80100000000000088010A01A001E1000C001F002700000038C01E7E", 261, 0, 0, 608, 5, 587, 0x7E1EC038),
            ("0C0100000100000000000000000000000000BA02000000000000020000000111010100110200000000000002003B031301000000006C3D0003000000F80100000000000087020701460206010C002100260000005B7907C1", 268, 0, 0, 698, 2, 529, 0xC107795B),
            ("0A0100000100000000000000000000000000B702000000000000020000000111010100190200000000000002003B0318010000000052690003000000F8010000000000002D0218012D0218010C0021002600000058328B66", 266, 0, 0, 695, 2, 537, 0x668B3258),
            ("0B0100000100000000000000000000000000680200000000000002000000011101010028020000000000000200200324010000000049160003000000F8010000000000008302D0001F0224010C00210026000000620E8954", 267, 0, 0, 616, 2, 552, 0x54890E62),
            ("0901000001000000000000000000000000009C02000000000000060000000111010100410200000000000006002803120100000000B30B0003000000F8010000000000003D02C500EF0212010C0021002B0000000B9E768E", 265, 0, 0, 668, 6, 577, 0x8E769E0B)
        ];
        foreach (var sample in samples)
        {
            byte[] captured = Convert.FromHexString(sample.Hex);
            That(DungeonRequests.TryParseDeath(captured, out var parsed) && parsed!.ObjectKey == sample.Key && parsed.Reporter == 1
                && parsed.SinglePlayerStatistics is not null, "真实普通单人死亡包识别单参与者统计布局");
            var statistics = parsed!.SinglePlayerStatistics!;
            That(statistics.Unknown6 == sample.First && statistics.Unknown14 == sample.FirstCount && statistics.Unknown18 == sample.Second
                && statistics.Unknown26 == sample.SecondCount, "两个完整u64和u32统计字段保持本次实收值");
            That(statistics.Participant == new DungeonDeathParticipant(0x111, 1, sample.Contribution, checked((ushort)sample.SecondCount)),
                "参与者类型、对象key、完整累计HP降低量和事件次数保持实收字节");
            That(statistics.RewriteFlag == 0 && statistics.SpawnLevel == 3 && statistics.SpawnHpSnapshot == 504 &&
                statistics.BodyChecksum == sample.Checksum && statistics.ChecksumMatches &&
                ClientBodyChecksum.Compute(captured.AsSpan(0, 84)) == sample.Checksum,
                "实际正文的改写分支、等级、创建HP快照和检查值与独立固定预期一致");
            byte[] remaining = captured[45..];
            captured[45] ^= 255;
            That(statistics.RemainingFields.Count == 43 && statistics.RemainingFields.SequenceEqual(remaining)
                && statistics.RemainingFields is System.Collections.IList list && list.IsReadOnly,
                "统计后续43B独立冻结，解析不解释为奖励");
        }

        foreach (var sample in new (string Hex,uint Key,ushort ApcKey,bool ApcFirst)[]
        {
            ("720200001200000000000000000000000000ED210000000000000300000002110112005821000000000000010011F13602060000000000000002008502DE0000000000C2660014000000341A000000000000FD02DE008002DE000B00340021000000B7D1F6A5",626,566,false),
            ("780200001200000000000000000000000000CA210000000000000200000002110112005921000000000000010011F1360202000000000000000100B402C60000000000B9090014000000341A00000000000022030901A602C6000B003400210000002B8148E1",632,566,false),
            ("840200001200000000000000000000000000F41D000000000000040000000211F185020700000000000000020011011200311D00000000000002000E01EB0000000000F91300140000003018000000000000AB0010012401EB000B00340030000000DB1A04B9",644,645,true)
        })
        {
            var captured = Convert.FromHexString(sample.Hex);
            That(DungeonRequests.TryParseDeath(captured,out var parsed) && parsed is { Reporter: 18 } && parsed.ObjectKey == sample.Key &&
                parsed.SinglePlayerStatistics is { Participants.Length: 2, ChecksumMatches: true, SpawnLevel: 20, RemainingFields.Count: 43 },
                "真实友方APC参与的102B集合按动态边界解析，不漏末尾检查值 " + sample.Key);
            var statistics = parsed!.SinglePlayerStatistics!;
            That(statistics.Participants[sample.ApcFirst ? 0 : 1] is { Kind: 0xF111 } apc && apc.ObjectKey == sample.ApcKey &&
                statistics.Participants[sample.ApcFirst ? 1 : 0] is { Kind: 0x0111, ObjectKey: 18 } &&
                statistics.Participant == statistics.Participants[0],"原序全列表保留，首行不当killer或固定本人 " + sample.Key);
            var frozen = statistics.Participants;
            captured[31] ^= 1;
            That(statistics.Participants.SequenceEqual(frozen) && DungeonRequests.TryParseDeath(captured,out var changed) &&
                changed!.SinglePlayerStatistics is { ChecksumMatches: false },"参与者冻结且完整正文篡改仍校验失败");
            foreach (var invalid in new[] { captured[..^1], captured.Concat(new byte[1]).ToArray(),
                captured[..88], WithCount(captured,1), WithCount(captured,3) })
                That(!DungeonRequests.TryParseSinglePlayerDeathStatistics(invalid,out _),"集合数量、条目和尾部必须完整等长");
        }
        // 仅APC贡献和254来源由匹配原生循环允许；资格不属于Parser，kind完整保留交Game核对。
        var singleSource = Convert.FromHexString(samples[0].Hex);
        BinaryPrimitives.WriteUInt16LittleEndian(singleSource.AsSpan(31),0xF111);
        BinaryPrimitives.WriteUInt16LittleEndian(singleSource.AsSpan(33),566);
        BinaryPrimitives.WriteUInt32LittleEndian(singleSource.AsSpan(singleSource.Length-4),ClientBodyChecksum.Compute(singleSource.AsSpan(0,singleSource.Length-4)));
        That(DungeonRequests.TryParseDeath(singleSource,out var onlyApc) && onlyApc!.SinglePlayerStatistics is
            { Participants.Length: 1, Participant.Kind: 0xF111, Participant.ObjectKey: 566, ChecksumMatches: true },
            "原生仅APC来源不强制伪造玩家贡献行");
        var maximum = new byte[74+14*254];
        singleSource.AsSpan(0,31).CopyTo(maximum); maximum[30] = 254;
        for (int at = 0; at < 254; at++) singleSource.AsSpan(31,14).CopyTo(maximum.AsSpan(31+14*at));
        singleSource.AsSpan(45).CopyTo(maximum.AsSpan(31+14*254));
        BinaryPrimitives.WriteUInt32LittleEndian(maximum.AsSpan(maximum.Length-4),ClientBodyChecksum.Compute(maximum.AsSpan(0,maximum.Length-4)));
        That(DungeonRequests.TryParseDeath(maximum,out var many) && many!.SinglePlayerStatistics is { Participants.Length: 254, ChecksumMatches: true } &&
            !DungeonRequests.TryParseSinglePlayerDeathStatistics(WithCount(maximum,255),out _),"原生来源上界254保留，255不借用动态入口");

        byte[] invalidChecksum = Convert.FromHexString(samples[0].Hex);
        invalidChecksum[84] ^= 1;
        That(DungeonRequests.TryParseDeath(invalidChecksum, out var mismatch) && mismatch!.SinglePlayerStatistics is { ChecksumMatches: false }
            && mismatch.RemainingFields.SequenceEqual(invalidChecksum[6..]),
            "正文检查值不匹配仍保留诊断88B长度接受和完整原字节");
        byte[] invalidPayload = Convert.FromHexString(samples[0].Hex);
        invalidPayload[60] ^= 1;
        That(DungeonRequests.TryParseSinglePlayerDeathStatistics(invalidPayload, out var changedSnapshot) &&
            changedSnapshot!.SpawnHpSnapshot == 505 && changedSnapshot.BodyChecksum == samples[0].Checksum && !changedSnapshot.ChecksumMatches,
            "创建HP字节变化不会继续匹配未变的检查值");
        byte[] rewritePayload = Convert.FromHexString(samples[0].Hex);
        rewritePayload[55] = 0x80;
        That(DungeonRequests.TryParseDeath(rewritePayload, out var rewrite) && rewrite!.SinglePlayerStatistics is { RewriteFlag: 0x80, ChecksumMatches: false },
            "Parser保留完整改写标志而不规范化、默认或签发战斗资格");
        byte[] wideStatistics = Convert.FromHexString(samples[0].Hex);
        BinaryPrimitives.WriteUInt64LittleEndian(wideStatistics.AsSpan(6), 0xFEDCBA9876543210);
        BinaryPrimitives.WriteUInt64LittleEndian(wideStatistics.AsSpan(18), 0xF123456789ABCDEF);
        BinaryPrimitives.WriteUInt64LittleEndian(wideStatistics.AsSpan(35), 0xE23456789ABCDEF1);
        BinaryPrimitives.WriteUInt32LittleEndian(wideStatistics.AsSpan(56), 0xC456789A);
        BinaryPrimitives.WriteUInt64LittleEndian(wideStatistics.AsSpan(60), 0xD3456789ABCDEF12);
        BinaryPrimitives.WriteUInt32LittleEndian(wideStatistics.AsSpan(84), 0xF56789AB);
        That(DungeonRequests.TryParseSinglePlayerDeathStatistics(wideStatistics, out var wide)
            && wide!.Unknown6 == 0xFEDCBA9876543210 && wide.Unknown18 == 0xF123456789ABCDEF
            && wide.Participant.CumulativeHpDecrease == 0xE23456789ABCDEF1 && wide.SpawnLevel == 0xC456789A
            && wide.SpawnHpSnapshot == 0xD3456789ABCDEF12 && wide.BodyChecksum == 0xF56789AB && !wide.ChecksumMatches,
            "统计、创建HP快照及等级保留完整无符号高位，不截断或猜成服务端伤害");
        byte[] originalSkill = Convert.FromHexString("002E00D0072900000100");
        That(SkillRequests.TryParseUse(originalSkill, out var tenByteSkill) && tenByteSkill!.RemainingFields.Count == 0 &&
            tenByteSkill.BodyChecksum is null && tenByteSkill.ChecksumMatches is null,
            "共享正文检查算法后10B技能路径仍无伪造检查值");
        byte[] capturedSkill = Convert.FromHexString("002E00D00729000001004AADA701");
        That(SkillRequests.TryParseUse(capturedSkill, out var fourteenByteSkill) && fourteenByteSkill!.BodyChecksum == 0x01A7AD4A &&
            fourteenByteSkill.ChecksumMatches == true && ClientBodyChecksum.Compute(originalSkill) == 0x01A7AD4A,
            "C2S38和39共享检查公式，技能固定实收预期保持一致");
        capturedSkill[10] ^= 1;
        That(SkillRequests.TryParseUse(capturedSkill, out var invalidSkill) && invalidSkill!.ChecksumMatches == false,
            "共享算法后14B技能不匹配继续保留观察接受行为");
        foreach (byte count in new byte[] { 0, 2, 255 })
        {
            wideStatistics[30] = count;
            That(!DungeonRequests.TryParseSinglePlayerDeathStatistics(wideStatistics, out var unsupported) && unsupported is null,
                "未知参与者数量不使用单人偏移 " + count);
            That(DungeonRequests.TryParseDeath(wideStatistics, out var diagnostic) && diagnostic!.SinglePlayerStatistics is null,
                "统计识别失败保留旧诊断原字节，不制造战斗资格");
        }
        foreach (int length in new[] { 0, 30, 45, 74, 87, 89, 102 })
            That(!DungeonRequests.TryParseSinglePlayerDeathStatistics(new byte[length], out var unsupported) && unsupported is null,
                "未确认统计宽度不产生部分对象 " + length);
        foreach (ushort key in new ushort[] { 0, 1, 0x8123, ushort.MaxValue })
        {
            byte[] notice = DungeonEntryProbe.BuildDeath(key);
            That(notice.SequenceEqual(new byte[] { (byte)key, (byte)(key >> 8), 0, 0, 0, 0, 255, 0, 0 }),
                "NOTI38 exact nine-byte death footprint for key " + key);
            That(notice.Length == 9 && BinaryPrimitives.ReadUInt16LittleEndian(notice) == key
                && BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(2)) == 0
                && notice[4] == 0 && notice[5] == 0 && notice[6] == 255 && notice[7] == 0 && notice[8] == 0,
                "death notification preserves u16 key zero count mode grade and extras " + key);
        }
        Console.WriteLine($"PASS {Count - checks} dungeon death protocol assertions");
        static byte[] WithCount(byte[] body,byte count) { var changed = body.ToArray(); changed[30] = count; return changed; }
    }
    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf)
    {
        int checks = Count;

        string root = Path.Combine(Path.GetTempPath(), "S5A9-death-protocol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("death-protocol", () => { });
            using var lifetime = new CancellationTokenSource();
            using var bytes = new MemoryStream();
            await using var sender = new GameConnectionSender(owner, bytes, lifetime, 0, (_, _, _, _, _) => { });
            var session = new GameSession(repository, towns, owner);
            var character = repository.Create(owner, new NewCharacter("DeathProbe", towns.Room, towns.InitialReturn)
            {
                AcceptedStoryLevel = 1,
                ActiveMainQuests = [new(0, 3145, 1)]
            });
            session.Select(character.Slot);
            session.MarkReady();
            That(session.LeaveRoom() && session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "death fixture reaches entry through real session transitions");
            var probe = new DungeonProbeState(session);
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, bytes, lifetime), owner, session, sender, probe);
            var commands = new GameCommandRegistry();
            using var log = new ServerLog(Path.Combine(root, "logs"));
            new DungeonProbeModule(log, DungeonCatalog.Load(pvf),
                DungeonEntryProbeMode.FinishLoadingMonsters, DungeonRoomCatalog.Load(pvf)).Register(commands);
            commands.Seal();
            string snapshot = JsonSerializer.Serialize(repository.List(owner).Single());
            That(await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SELECT_DUNGEON,
                Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000"))) == CommandResult.Handled,
                "explicit diagnostic monster mode selects matching task room");
            var firstKeys = probe.Monsters!.Monsters.Select(monster => monster.ObjectKey).ToArray();
            var death = Death(firstKeys[0], character.Id);
            long before = bytes.Length;
            That(await commands.DispatchAsync(context, death) == CommandResult.Unhandled && bytes.Length == before,
                "handler rejects death before loading without notification");
            That(await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.FINISH_LOADING, new byte[8]))
                == CommandResult.Handled, "handler completes initial loading");
            before = bytes.Length;
            That(await commands.DispatchAsync(context, Death(firstKeys[0], unchecked((ushort)(character.Id + 1))))
                == CommandResult.Unhandled && bytes.Length == before, "wrong reporter cannot declare another character death");
            That(await commands.DispatchAsync(context, Death(0x10000u + firstKeys[0], character.Id)) == CommandResult.Unhandled
                && bytes.Length == before, "full uint key cannot alias current ushort object");
            That(await commands.DispatchAsync(context, death) == CommandResult.Handled, "first matching death handled");
            VerifyFrame(before, 0, 38, DungeonEntryProbe.BuildDeath(firstKeys[0]));
            before = bytes.Length;
            That(await commands.DispatchAsync(context, death) == CommandResult.Handled && bytes.Length == before,
                "duplicate death emits no repeated notification");
            byte[] moveBody = new byte[100];
            moveBody[0] = 1;
            moveBody[1] = 1;
            var move = new Packet(1, (ushort)CmdPacketType.MOVE_MAP, moveBody);
            That(await commands.DispatchAsync(context, move) == CommandResult.Unhandled && bytes.Length == before,
                "partial roster death does not acknowledge passage");
            foreach (ushort key in firstKeys.Skip(1))
                That(await commands.DispatchAsync(context, Death(key, character.Id)) == CommandResult.Handled,
                    "remaining current-room death handled " + key);
            before = bytes.Length;
            That(await commands.DispatchAsync(context, move) == CommandResult.Handled
                && probe.Run!.CurrentMap.Id == 76122, "cleared roster permits adjacent map 76122");
            VerifyFrame(before, 1, 45, [1]);
            byte[] frames = bytes.ToArray();
            int nextOffset = checked((int)before + 17);
            That(frames[nextOffset] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(nextOffset + 1)) == 29,
                "passage sends ACK45 then NOTI29 in order");
            before = bytes.Length;
            That(await commands.DispatchAsync(context, death) == CommandResult.Unhandled && bytes.Length == before,
                "old room key is refused while loading the next room");
            That(JsonSerializer.Serialize(repository.List(owner).Single()) == snapshot,
                "death and passage preserve all persisted quests rewards inventory location and revision");
            session.ReturnToRoster();
            session.Select(character.Slot);
            session.MarkReady();
            string reselectionSnapshot = JsonSerializer.Serialize(repository.List(owner).Single());
            before = bytes.Length;
            That(await commands.DispatchAsync(context, death) == CommandResult.Unhandled && bytes.Length == before,
                "actual reselection changes epoch and invalidates the previous run");
            using var replacement = owners.Acquire("death-protocol", () => { });
            bool stale = false;
            try
            {
                await commands.DispatchAsync(context, death);
            }
            catch (InvalidOperationException error) when (error.Message == "Stale account generation")
            {
                stale = true;
            }
            That(stale && bytes.Length == before, "old generation cannot publish a death report");
            That(JsonSerializer.Serialize(repository.List(replacement).Single()) == reselectionSnapshot,
                "old death and owner replacement cannot change the legitimately reselected character");
            var previousRun = probe.Run!;
            int nextKey = probe.NextObjectKey;
            var nextSession = new GameSession(repository, towns, replacement);
            var nextProbe = new DungeonProbeState(nextSession) { NextObjectKey = 0x900 };
            probe.Reset();
            That(previousRun.Ended && probe.Run is null && probe.Plan is null && probe.Monsters is null
                && probe.SelectionEpoch is null && !context.BlocksTownActions,
                "old connection cleanup ends and releases its diagnostic state even after owner replacement");
            That(probe.NextObjectKey == nextKey && nextProbe.NextObjectKey == 0x900,
                "cleanup preserves connection key monotonicity and does not touch the replacement state");

            void VerifyFrame(long offset, byte category, ushort opcode, byte[] expectedBody)
            {
                byte[] sent = bytes.ToArray();
                int start = checked((int)offset);
                int length = BinaryPrimitives.ReadInt32LittleEndian(sent.AsSpan(start + 3));
                That(length == expectedBody.Length + 16 && sent[start] == category
                    && BinaryPrimitives.ReadUInt16LittleEndian(sent.AsSpan(start + 1)) == opcode
                    && sent.AsSpan(start + 16, expectedBody.Length).SequenceEqual(expectedBody)
                    && BinaryPrimitives.ReadUInt32LittleEndian(sent.AsSpan(start + 7))
                        == GameFrame.Crc(sent.AsSpan(start + 11, length - 11)),
                    "complete frame category opcode body and CRC " + opcode);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"PASS {Count - checks} dungeon death handler assertions");
    }

    private static Packet Death(uint key, ushort reporter)
    {
        byte[] body = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(body, key);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), reporter);
        return new Packet(1, (ushort)CmdPacketType.DIE_MONSTER, body);
    }

}
