using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
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
        SelectionGuard(game, character);
        RoleScopedValues();
        Console.WriteLine($"PASS {Count - before} client settings parser, projection and selection assertions");
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

    sealed class SettingsStore : IClientSettingsStore
    {
        ImmutableArray<byte> main;
        readonly Dictionary<ushort, ImmutableArray<byte>> characters = [];
        readonly Dictionary<ushort, ImmutableArray<byte>> roleMain = [];
        public ClientSettingsSnapshot Load(AccountOwners.Lease owner, ushort id) => new(main, characters.GetValueOrDefault(id), roleMain.GetValueOrDefault(id));
        public bool SaveGameOptions(AccountOwners.Lease owner, ImmutableArray<byte> payload, ushort? id = null, ImmutableArray<byte> characterGameOptions = default)
        {
            main = payload;
            if (id is ushort characterId)
                roleMain[characterId] = characterGameOptions.IsDefault ? payload : characterGameOptions;
            return true;
        }
        public bool SaveCharacterOptions(AccountOwners.Lease owner, ushort id, ImmutableArray<byte> payload) { characters[id] = payload; return true; }
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
            for (int i = 0; i < 102; i++)
                That(BinaryPrimitives.ReadUInt16LittleEndian(projected.GameOptions.AsSpan().Slice(i * 2, 2)) ==
                    BinaryPrimitives.ReadUInt16LittleEndian((slots.Contains(i) ? own : account).AsSpan(i * 2)), "角色八项独立，其他所有账号槽不变");
            That(BinaryPrimitives.ReadUInt16LittleEndian(projected.CharacterOptions.AsSpan().Slice(46 * 2, 2)) == bits * 4 + 5 &&
                BinaryPrimitives.ReadUInt16LittleEndian(projected.CharacterOptions.AsSpan().Slice(49 * 2, 2)) == ((bits >> 1) & 1),
                "390两个已确认槽与173当前角色值一致");
            for (int i = 0; i < 512; i++)
                if (i is not (92 or 93 or 98 or 99))
                    That(projected.CharacterOptions[i] == raw[i], "不改变其余508字节及未知角色槽43/48");
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

    static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { That(true, message); return; }
        throw new InvalidOperationException("FAIL: " + message);
    }
}
