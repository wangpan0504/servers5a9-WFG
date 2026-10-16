using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SettingsProtocolTests
{
    public static void Run()
    {
        int before = Count;
        byte[] game = Enumerable.Range(0, 204).Select(i => (byte)i).ToArray();
        byte[] character = Enumerable.Repeat((byte)255, 512).ToArray();
        character[86] = 0;
        character[92] = 100;
        foreach (var (data, parse) in new (byte[], Func<byte[], ImmutableArray<byte>>)[]
        {
            (game, bytes => SettingsRequests.GameOptions(bytes)),
            (character, bytes => SettingsRequests.CharacterOptions(bytes))
        })
        {
            byte[] body = Body(data);
            That(parse(body).AsSpan().SequenceEqual(data), "设置完整数据含未知槽及FFFF原样解析");
            foreach (byte[] bad in new[] { Array.Empty<byte>(), body[..3], body[..^1], body.Concat(new byte[] { 0 }).ToArray() })
                Reject<InvalidDataException>(() => parse(bad), "短包、缺数据和多余尾部拒绝");
            foreach (uint length in new[] { 0u, (uint)data.Length - 1, (uint)data.Length + 1, uint.MaxValue })
            {
                byte[] bad = body.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(bad, length);
                Reject<InvalidDataException>(() => parse(bad), "长度头必须对应匹配客户端完整设置区");
            }
        }
        That(SettingsPackets.GameOptions(game.ToImmutableArray()).SequenceEqual(Body(game).Concat(new byte[8])), "账号设置回放三段长度和主区精确");
        That(SettingsPackets.CharacterOptions(character.ToImmutableArray()).SequenceEqual(Body(character)), "角色设置回放长度及512字节原样");
        That(SettingsPackets.Visibility(0x1234, 30).SequenceEqual(new byte[] { 0x34, 0x12, 30 }), "可见性通知精确ID和一字节flags");
        Reject<InvalidDataException>(() => SettingsPackets.GameOptions(default), "未保存的主设置不能构造默认通知");
        Reject<InvalidDataException>(() => SettingsPackets.GameOptions([0]), "损坏主设置不能回放");
        Reject<InvalidDataException>(() => SettingsPackets.CharacterOptions(default), "未保存的角色设置不能构造默认通知");
        Reject<InvalidDataException>(() => SettingsPackets.CharacterOptions([0]), "损坏角色设置不能回放");
        byte[] unset = SettingsPackets.GameOptionsInitialization();
        That(unset.Length == 216 && BinaryPrimitives.ReadUInt32LittleEndian(unset) == 204 &&
            Enumerable.Range(0, 102).All(slot => BinaryPrimitives.ReadUInt16LittleEndian(unset.AsSpan(4 + slot * 2)) == 0x7FFF) &&
            unset.AsSpan(208).SequenceEqual(new byte[8]), "首次初始化发送完整原生未设置主区和两个空银行");
        unset[4] = 0;
        That(SettingsPackets.GameOptionsInitialization()[4] == 0xFF, "初始化包独立分配，不共享可变发送正文");
        SelectionGuard(game, character);
        RoleScopedValues();
        HotkeyProtocol();
        TownVisibility();
        Console.WriteLine($"PASS {Count - before} client settings parser, projection and selection assertions");
    }

    static void HotkeyProtocol()
    {
        byte[] text = "I01\t77\nI02\t55\nI03\t73\nI04\t58\nC00\t134\nJ00\t134\nK02\t31\n"u8.ToArray();
        var parsed = SettingsRequests.Hotkeys(Body(text));
        That(parsed.AsSpan().SequenceEqual(text), "198长度头之后为完整键位文本，不含echo、配置类型或参考版尾部");
        foreach (byte profile in new byte[] { 0, 1 })
            That(SettingsPackets.Hotkeys(profile, parsed).SequenceEqual(new byte[] { profile }.Concat(Body(text))),
                "454独立预期为配置u8、长度u32及原样文本，无额外尾部");
        That(HotkeySettingsRules.Profile(10) == 1 && Enumerable.Range(0, 16).Where(job => job != 10)
            .All(job => HotkeySettingsRules.Profile((byte)job) == 0), "本版job10单独使用缔造者键位配置");
        foreach (byte[] bad in new[] { Array.Empty<byte>(), Body(text)[..^1], Body(text).Concat(new byte[] { 0 }).ToArray(),
            Body("I01\t\n"u8.ToArray()), Body("I01\t77x\n"u8.ToArray()), Body("I01\t77\0\n"u8.ToArray()), Body(new byte[1400]) })
            Reject<InvalidDataException>(() => SettingsRequests.Hotkeys(bad), "键位截断、尾部、坏整数、NUL及原生不可读长度拒绝");
        var unbound = SettingsRequests.Hotkeys(Body("I01\t134\nI02\t134\nI03\t134\nI04\t134\n"u8.ToArray()));
        That(SettingsPackets.Hotkeys(0, unbound).AsSpan(5).SequenceEqual(unbound.AsSpan()),
            "保留用户明确取消绑定的合法键值，不擅自补默认移动键");
        var saved = new ClientSettingsSnapshot([.. new byte[204]], default, Hotkeys: parsed);
        That(ClientSettingsRules.Project(saved).Hotkeys == parsed && !new ClientSettingsSnapshot(default, default, Hotkeys: parsed).IsDefault,
            "角色主区协调不丢失独立账号键位，也不生成未保存主区");
        Reject<InvalidDataException>(() => SettingsPackets.Hotkeys(2, parsed), "未知配置不能回放到另一原生键位bank");
    }

    static void SelectionGuard(byte[] game, byte[] character)
    {
        game = game.ToArray();
        character = character.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(game.AsSpan(68 * 2), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(game.AsSpan(13 * 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(character.AsSpan(46 * 2), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(character.AsSpan(49 * 2), 1);
        string root = Path.Combine(Path.GetTempPath(), "S5A9-settings-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("settings.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) }, point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("settings-session", () => { });
            repository.Create(owner, new("SettingFirst", point, point));
            repository.Create(owner, new("SettingTwo", point, point));
            var store = new SettingsStore();
            var service = new ClientSettingsService(store);
            var session = new GameSession(repository, towns, owner);
            That(session.SaveGameOptions(service, game.ToImmutableArray()), "账号设置可在选角前保存");
            Reject<InvalidDataException>(() => session.SaveCharacterOptions(service, character.ToImmutableArray()), "角色设置未选角不能保存");
            session.Select(0);
            That(session.SaveCharacterOptions(service, character.ToImmutableArray()), "角色设置绑定当前选择");
            var receipt = session.LoadClientSettings(service);
            That(receipt.Settings.GameOptions.AsSpan().SequenceEqual(game) && receipt.Settings.CharacterOptions.AsSpan().SequenceEqual(character) &&
                session.CanPublishClientSettings(receipt), "未MarkReady的入城准备可以回放当前角色设置");
            session.ReturnToRoster();
            That(!session.CanPublishClientSettings(receipt), "回名册使旧设置发布失效");
            session.Select(0);
            That(!session.CanPublishClientSettings(receipt), "同角色新epoch不会发布旧设置");
            var next = session.LoadClientSettings(service);
            session.ReturnToRoster();
            session.Select(1);
            That(!session.CanPublishClientSettings(next), "换角色不能发送前一个角色设置");
            var latest = session.LoadClientSettings(service);
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => session.CanPublishClientSettings(latest), "旧generation不能发布设置");
            Reject<InvalidOperationException>(() => session.SaveGameOptions(service, game.ToImmutableArray()), "旧generation不能保存设置");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    static byte[] Body(byte[] data)
    {
        byte[] body = new byte[data.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, (uint)data.Length);
        data.CopyTo(body, 4);
        return body;
    }

    static void TownVisibility()
    {
        var point = new Location(38, 0, 622, 196, 5);
        // 职业共用状态消费；未知外观bits不能混入位置状态，第三尾字节保持原值。
        foreach (byte job in Enumerable.Range(0, 16).Select(value => (byte)value))
        foreach (var (flags, state) in new (byte, byte)[] { (4, 1), (6, 3), (28, 1), (30, 3), (253, 1), (255, 3) })
        {
            var character = new Character(0x1234, 0, "Visibility", 7, point, null) { Job = job, VisibilityFlags = flags };
            Bytes(TownPackets.Position(character, false), [0x34, 0x12, 0x6e, 2, 0xc4, 0, 5, 100, 0],
                "各职业坐标通知22不携带外观状态，方向和速度保持");
            Bytes(TownPackets.Position(character, true), [0x34, 0x12, 38, 0, 0x6e, 2, 0xc4, 0, 5, state],
                "各职业区域通知23保留转职显示开关和坐标");
            Bytes(TownPackets.Transition(character), [38, 0, 1, 0, 0x34, 0x12, 0x6e, 2, 0xc4, 0, 5, state, 1],
                "各职业场景通知24保留转职显示开关、状态bit0与第三尾字节");
            That(character.VisibilityFlags == flags && character.Revision == 7, "构包不修改角色显示设置及revision");
        }
    }

    sealed class SettingsStore : IClientSettingsStore
    {
        public bool SaveEmotion(AccountOwners.Lease owner, ushort id, ushort emotion) => throw new NotSupportedException("此夹具只验证原有设置正文");
        ImmutableArray<byte> main;
        readonly Dictionary<ushort, ImmutableArray<byte>> characters = [];
        readonly Dictionary<ushort, ImmutableArray<byte>> roleMain = [];
        ImmutableArray<byte> hotkeys;
        public ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort id) => new(main, characters.GetValueOrDefault(id), roleMain.GetValueOrDefault(id), hotkeys);
        public bool SaveGameOptions(AccountOwners.Lease owner, ImmutableArray<byte> payload, ushort? id = null, ImmutableArray<byte> characterGameOptions = default)
        {
            main = payload;
            if (id is ushort characterId)
                roleMain[characterId] = characterGameOptions.IsDefault ? payload : characterGameOptions;
            return true;
        }
        public bool SaveCharacterOptions(AccountOwners.Lease owner, ushort id, ImmutableArray<byte> payload) { characters[id] = payload; return true; }
        public bool SaveHotkeys(AccountOwners.Lease owner, byte profile, ImmutableArray<byte> text) { hotkeys = text; return true; }
    }

    static void RoleScopedValues()
    {
        byte[] account = Enumerable.Range(0, 204).Select(index => (byte)index).ToArray();
        byte[] own = Enumerable.Range(0, 204).Select(index => (byte)(255 - index)).ToArray();
        int[] slots = [1, 13, 55, 74, 22, 68, 89, 94];
        for (int bits = 0; bits < 16; bits++)
        {
            for (int i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(own.AsSpan(slots[i] * 2), (ushort)((bits >> i) & 1));
            for (int i = 4; i < 8; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(own.AsSpan(slots[i] * 2), (ushort)(bits * 4 + i));
            byte[] raw = Enumerable.Repeat((byte)255, 512).ToArray();
            var projected = ClientSettingsRules.Project(new(account.ToImmutableArray(), raw.ToImmutableArray(), own.ToImmutableArray()));
            byte[] expectedMain = account.ToArray();
            foreach (int slot in slots)
                own.AsSpan(slot * 2, 2).CopyTo(expectedMain.AsSpan(slot * 2));
            Bytes(projected.GameOptions.AsSpan(), expectedMain, "角色八项独立，其他所有账号槽不变");
            byte[] expectedCharacter = raw.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(expectedCharacter.AsSpan(92), (ushort)(bits * 4 + 5));
            BinaryPrimitives.WriteUInt16LittleEndian(expectedCharacter.AsSpan(98), (ushort)((bits >> 1) & 1));
            Bytes(projected.CharacterOptions.AsSpan(), expectedCharacter, "只修改已确认槽，不改变其余508字节及未知角色槽43/48");
            byte expectedFlags = (byte)(4 | ((bits & 1) != 0 ? 2 : 0) | ((bits & 4) == 0 ? 8 : 0) | ((bits & 8) == 0 ? 16 : 0));
            That(ClientSettingsRules.VisibilityFlags(projected.GameOptions) == expectedFlags, "三项外观及无关bits匹配16种开关组合");
            That(account.AsSpan().SequenceEqual(Enumerable.Range(0, 204).Select(index => (byte)index).ToArray()), "回放不就地修改账号存档");
        }
        var missing = ClientSettingsRules.Project(new(default, default));
        That(missing.GameOptions.IsDefault && missing.CharacterOptions.IsDefault, "不合成未经确认的完整默认通知");
        byte[] legacy = Enumerable.Repeat((byte)255, 512).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(46 * 2), 37);
        BinaryPrimitives.WriteUInt16LittleEndian(legacy.AsSpan(49 * 2), 0);
        var migrated = ClientSettingsRules.Project(new(account.ToImmutableArray(), legacy.ToImmutableArray()));
        That(BinaryPrimitives.ReadUInt16LittleEndian(migrated.GameOptions.AsSpan().Slice(68 * 2, 2)) == 37 &&
            BinaryPrimitives.ReadUInt16LittleEndian(migrated.GameOptions.AsSpan().Slice(13 * 2, 2)) == 0, "旧角色已保存两项不被账号数据覆盖");
    }

}
