using Microsoft.Data.Sqlite;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class ClientSettingsTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "settings"));
        await fixture.StartAsync();
        const string account = "settingsalice", otherAccount = "settingsbob";
        byte[] initial = Payload(204, 7), shared = Payload(204, 31), first = Payload(512, 63), second = Payload(512, 127);
        byte[] otherGame = Payload(204, 193), otherCharacter = Payload(512, 251);
        ushort firstId, secondId, otherId;

        using (var peer = Open(fixture, account))
        {
            string assets = Assets(fixture);
            Save(peer, 197, initial);
            // 未选角不能用城镇事件作屏障，名称检查可证明保存没有插入额外回包。
            peer.Send(692, Name("SettingProbe"));
            peer.Expect(1, 692, [1]);
            CheckGame(fixture, account, initial);
            That(Assets(fixture) == assets, "账号设置在入城前静默保存，不改其他表");
            Save(peer, 447, first);
            peer.Closed();
            That(fixture.Query("SELECT count(*) FROM character_client_settings") is [[0L]], "未选角的角色设置不能写库");
            CheckGame(fixture, account, initial);
        }

        using (var peer = Open(fixture, account))
        {
            peer.Create("SettingFirst");
            firstId = peer.Id;
            peer.Create("SettingTwo");
            secondId = peer.Id;
            Select(peer, 0, initial);
            string assets = Assets(fixture);
            Save(peer, 197, shared);
            Save(peer, 447, first);
            peer.Barrier();
            CheckGame(fixture, account, shared);
            CheckCharacter(fixture, firstId, account, first);
            That(Assets(fixture) == assets, "账号和角色设置提交不写角色revision及其他资产");
            string stored = Settings(fixture);
            Save(peer, 197, shared);
            Save(peer, 447, first);
            peer.Barrier();
            That(Settings(fixture) == stored && Assets(fixture) == assets, "相同设置重复提交无额外通知和数据变化");

            ReturnSelection(peer);
            Select(peer, 0, shared, first);
            ReturnSelection(peer);
            Select(peer, 1, shared);
            assets = Assets(fixture);
            Save(peer, 447, second);
            peer.Barrier();
            CheckCharacter(fixture, firstId, account, first);
            CheckCharacter(fixture, secondId, account, second);
            That(Assets(fixture) == assets, "同账号第二角色共享账号设置、单独保存角色设置");
            Exit(peer);
        }

        using (var peer = Open(fixture, otherAccount))
        {
            peer.Create("SettingOther");
            otherId = peer.Id;
            peer.Select();
            That(peer.ClientSettings.Length == 0, "未保存的另一账号不发送默认设置覆盖客户端");
            Save(peer, 447, otherCharacter);
            peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_client_settings WHERE account=$p0", otherAccount) is [[0L]],
                "只保存角色设置不会补写账号默认设置");
            ReturnSelection(peer);
            peer.Select(settings: [(390, Blob(otherCharacter))]);
            peer.Barrier();
            Save(peer, 197, otherGame);
            peer.Barrier();
            CheckGame(fixture, account, shared);
            CheckGame(fixture, otherAccount, otherGame);
            CheckCharacter(fixture, otherId, otherAccount, otherCharacter);
            Exit(peer);
        }

        using (var peer = Open(fixture, account))
        {
            Select(peer, 0, shared, first);
            ReturnSelection(peer);
            Select(peer, 1, shared, second);
            Exit(peer);
        }
        await fixture.StopAsync();
        string persisted = Settings(fixture);
        await fixture.StartAsync("restart");
        That(Settings(fixture) == persisted, "独立服务进程重启保留两账号的完整设置");
        using (var peer = Open(fixture, account))
        {
            Select(peer, 0, shared, first);
            ReturnSelection(peer);
            Select(peer, 1, shared, second);
            Exit(peer);
        }
        using (var peer = Open(fixture, otherAccount))
        {
            Select(peer, 0, otherGame, otherCharacter);
            Exit(peer);
        }

        foreach (var (opcode, payload) in new (ushort, byte[])[] { (197, shared), (447, first) })
        {
            byte[] valid = Blob(payload);
            byte[][] malformed = [[], valid[..^1], [.. valid, 0], Build(w =>
            {
                w.Write((uint)(payload.Length + 1));
                w.Write(payload);
            })];
            foreach (var body in malformed)
            {
                using var peer = Open(fixture, account);
                Select(peer, 0, shared, first);
                string assets = Assets(fixture), stored = Settings(fixture);
                peer.Send(opcode, body);
                peer.Closed();
                That(Settings(fixture) == stored && Assets(fixture) == assets, "非法设置长度或尾部断开连接，原值及资产不变");
            }
        }

        foreach (var (opcode, table, payload) in new (ushort, string, byte[])[]
        {
            (197, "account_client_settings", shared), (447, "character_client_settings", first),
            (197, "character_game_settings", shared)
        })
        {
            using var peer = Open(fixture, account);
            Select(peer, 0, shared, first);
            string assets = Assets(fixture), stored = Settings(fixture);
            Sql(fixture, $"CREATE TRIGGER fail_settings BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'settings rollback'); END");
            try
            {
                Save(peer, opcode, payload);
                peer.Barrier();
                That(Settings(fixture) == stored && Assets(fixture) == assets, "相同字节跳过写入，可通过拒绝UPDATE的触发器");
                Save(peer, opcode, Payload(payload.Length, 89));
                peer.Closed();
                That(Settings(fixture) == stored && Assets(fixture) == assets, "设置SQL失败关闭连接且不改变旧值及其他表");
            }
            finally
            {
                Sql(fixture, "DROP TRIGGER fail_settings");
            }
        }
        using (var peer = Open(fixture, account))
        {
            Select(peer, 0, shared, first);
            Exit(peer);
        }
        await CharacterScopeAsync(fixture);
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA user_version") is [[7L]] && fixture.Query("PRAGMA integrity_check") is [["ok"]]
            && fixture.Query("PRAGMA foreign_key_check").Length == 0, "设置TCP数据库版本及完整性正常");
    }

    static byte[] Payload(int length, int seed) => Enumerable.Range(0, length).Select(i => (byte)(i * 37 + seed)).ToArray();
    static byte[] Blob(byte[] payload) => Build(w =>
    {
        w.Write((uint)payload.Length);
        w.Write(payload);
    });
    static void Save(TcpPeer peer, ushort opcode, byte[] payload) => peer.Send(opcode, Blob(payload));
    static void Select(TcpPeer peer, ushort slot, byte[] game, byte[]? character = null, byte[]? roleMain = null)
    {
        byte[] projected = ExpectedMain(game, roleMain, character);
        byte[] gameBody = [.. Blob(projected), 0, 0, 0, 0, 0, 0, 0, 0];
        (ushort Opcode, byte[] Body)[] settings = character is null ? [(173, gameBody)] : [(173, gameBody), (390, Blob(Character(character, projected)))];
        peer.Select(slot, settings: settings, appearanceFlags: Flags(projected));
        That(peer.ClientSettings.Length == settings.Length, "完整设置按173、390顺序位于地区绑定之后及124之前");
        peer.Barrier();
    }

    static async Task CharacterScopeAsync(TcpTestFixture fixture)
    {
        const string account = "scopeowner";
        byte[] mainA = TypedMain(0, 10, 20, 30, 40), mainB = TypedMain(15, 100, 90, 80, 70);
        byte[] roleA = TypedCharacter(mainA), roleB = TypedCharacter(mainB);
        // 模拟客户端447先提交正确角色值，197仍携带旧账号的两槽。
        Write(mainB, 68, 5);
        Write(mainB, 13, 0);
        Write(mainB, 9, 1234);
        ushort idA, idB;
        using (var peer = Open(fixture, account))
        {
            peer.Create("ScopeFirst");
            idA = peer.Id;
            peer.Create("ScopeTwo");
            idB = peer.Id;
            peer.Select();
            string assets = Assets(fixture);
            SavePair(peer, mainA, roleA, 6);
            CheckGame(fixture, account, mainA);
            CheckRoleMain(fixture, idA, account, mainA, roleA);
            That(Assets(fixture) == assets, "八项角色设置保存不改变角色及资产表");

            ReturnSelection(peer);
            // 新角色没有Main快照，首次只使用当前账号初值。
            Select(peer, 1, mainA);
            Save(peer, 447, roleB);
            peer.Barrier();
            ReturnSelection(peer);
            Select(peer, 1, mainA, roleB);
            That(fixture.Query("SELECT count(*) FROM character_game_settings WHERE character_id=$p0", idB) is [[0L]],
                "只有legacy角色选项时以有效46/49恢复两项，不补写角色Main");
            SavePair(peer, mainB, roleB, Flags(mainA));
            CheckGame(fixture, account, mainB);
            CheckRoleMain(fixture, idA, account, mainA, roleA);
            CheckRoleMain(fixture, idB, account, mainB, roleB);
            ReturnSelection(peer);
            Select(peer, 0, mainB, roleA, mainA);
            byte[] echoedA = ExpectedMain(mainB, mainA, roleA);
            Save(peer, 197, echoedA);
            peer.Barrier();
            CheckRoleMain(fixture, idB, account, mainB, roleB);
            That(Read(echoedA, 9) == 1234, "公共槽取当前账号值，客户端自动197重发不污染另一角色八项");

            ReturnSelection(peer);
            Select(peer, 1, echoedA, roleB, mainB);
            Write(mainB, 9, 4321);
            Save(peer, 197, mainB);
            peer.Barrier();
            ReturnSelection(peer);
            Select(peer, 0, mainB, roleA, echoedA);
            That(Read(ExpectedMain(mainB, echoedA, roleA), 9) == 4321, "一个角色更新公共槽后另一角色得到新公共值，保留自己的八项");

            byte previousFlags = Flags(mainA);
            for (int mask = 0; mask < 16; mask++)
            {
                mainA = TypedMain(mask, (ushort)(10 + mask), (ushort)(20 + mask), (ushort)(30 + mask), (ushort)(40 + mask));
                Write(mainA, 9, 4321);
                roleA = TypedCharacter(mainA);
                SavePair(peer, mainA, roleA, previousFlags);
                previousFlags = Flags(mainA);
                CheckRoleMain(fixture, idA, account, mainA, roleA);
                CheckRoleMain(fixture, idB, account, mainB, roleB);
                ReturnSelection(peer);
                Select(peer, 0, mainA, roleA, mainA);
            }
            ReturnSelection(peer);
            Select(peer, 1, mainA, roleB, mainB);
            That(Read(ExpectedMain(mainA, mainB, roleB), 68) == 90 && Read(ExpectedMain(mainA, mainB, roleB), 94) == 70,
                "另一角色的队员透明度及字体透明度未被16种复选项组合覆盖");
            Exit(peer);
        }
        using (var peer = Open(fixture, account))
        {
            Select(peer, 0, mainA, roleA, mainA);
            ReturnSelection(peer);
            Select(peer, 1, mainA, roleB, mainB);
            Exit(peer);
        }
        string saved = Settings(fixture);
        await fixture.StopAsync();
        await fixture.StartAsync("role-restart");
        That(Settings(fixture) == saved, "四复选项及四透明度分角色存档在进程重启后完整保留");
        using (var peer = Open(fixture, account))
        {
            Select(peer, 0, mainA, roleA, mainA);
            ReturnSelection(peer);
            Select(peer, 1, mainA, roleB, mainB);
            Exit(peer);
        }

        using (var owner = Open(fixture, account))
        using (var watcher = Open(fixture, "scopewatch"))
        {
            Select(owner, 0, mainA, roleA, mainA);
            watcher.Create("ScopeWatch");
            watcher.Select();
            owner.Send(1426);
            owner.SendBarrier();
            owner.Batch((0, 23), (0, 24), (1, 1347));
            watcher.Send(1426);
            watcher.SendBarrier();
            var entry = watcher.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 22), (1, 1347));
            owner.SendBarrier();
            owner.Batch((0, 2), (0, 3), (0, 22), (1, 1347));
            That(entry[2].Body[^23] == Flags(mainA), "公共场景同屏Actor使用选中角色的flags");
            byte previousFlags = Flags(mainA);
            Write(mainA, 1, 0);
            SavePair(owner, mainA, roleA, previousFlags);
            watcher.SendBarrier();
            var refreshed = watcher.Expect(0, 2).Body;
            That(U16(refreshed, 121) == idA && refreshed[^23] == Flags(mainA), "保存flags变化后同屏Actor原地刷新");
            watcher.Expect(1, 1347, [1]);
            owner.Send(7);
            owner.Batch((0, 6), (1, 7), (0, 2));
            watcher.Expect(0, 6, Build(w => w.Write(idA)));
            watcher.Barrier();
            Exit(owner);
            Exit(watcher);
        }
    }

    static readonly int[] Scoped = [1, 13, 55, 74, 22, 68, 89, 94];
    static ushort Read(byte[] payload, int slot) => U16(payload, slot * 2);
    static void Write(byte[] payload, int slot, ushort value)
    {
        payload[slot * 2] = (byte)value;
        payload[slot * 2 + 1] = (byte)(value >> 8);
    }
    static byte[] ExpectedMain(byte[] account, byte[]? roleMain, byte[]? character)
    {
        byte[] projected = (byte[])account.Clone();
        if (roleMain is not null)
            foreach (int slot in Scoped)
                Write(projected, slot, Read(roleMain, slot));
        if (character is not null)
        {
            if (Read(character, 46) <= 100)
                Write(projected, 68, Read(character, 46));
            if (Read(character, 49) <= 1)
                Write(projected, 13, Read(character, 49));
        }
        return projected;
    }
    static byte[] Character(byte[] character, byte[] main)
    {
        byte[] projected = (byte[])character.Clone();
        Write(projected, 46, Read(main, 68));
        Write(projected, 49, Read(main, 13));
        return projected;
    }
    static byte Flags(byte[] main) => (byte)(4 | (Read(main, 1) != 0 ? 2 : 0) |
        (Read(main, 55) == 0 ? 8 : 0) | (Read(main, 74) == 0 ? 16 : 0));
    static byte[] TypedMain(int mask, ushort first, ushort second, ushort third, ushort fourth)
    {
        byte[] main = Payload(204, 73);
        for (int index = 0; index < 4; index++)
            Write(main, Scoped[index], (ushort)((mask >> index) & 1));
        ushort[] alpha = [first, second, third, fourth];
        for (int index = 0; index < 4; index++)
            Write(main, Scoped[index + 4], alpha[index]);
        return main;
    }
    static byte[] TypedCharacter(byte[] main) => Character(Payload(512, 109), main);
    static void SavePair(TcpPeer peer, byte[] main, byte[] character, byte previousFlags)
    {
        Save(peer, 447, character);
        Save(peer, 197, main);
        byte flags = Flags(ExpectedMain(main, main, character));
        if (flags != previousFlags)
            peer.Expect(0, 357, Build(w =>
            {
                w.Write(peer.Id);
                w.Write(flags);
            }));
        peer.Barrier();
    }
    static void CheckRoleMain(TcpTestFixture fixture, ushort id, string account, byte[] main, byte[] character) =>
        That(fixture.Query("SELECT account,hex(game_options) FROM character_game_settings WHERE character_id=$p0", id) is var rows
            && rows.Length == 1 && Equals(rows[0][0], account) && Equals(rows[0][1], Convert.ToHexString(ExpectedMain(main, null, character))),
            "角色Main完整副本保留，已知角色46/49纠正68/13");
    static void ReturnSelection(TcpPeer peer)
    {
        peer.Send(7);
        peer.Expect(1, 7, [1]);
        peer.Expect(0, 2);
    }
    static void Exit(TcpPeer peer)
    {
        peer.Send(3, [0]);
        peer.Expect(1, 3, [1]);
        peer.Closed();
    }
    static void CheckGame(TcpTestFixture fixture, string account, byte[] payload) =>
        That(fixture.Query("SELECT hex(game_options) FROM account_client_settings WHERE account=$p0", account) is var rows
            && rows.Length == 1 && Equals(rows[0][0], Convert.ToHexString(payload)), "独立读库核对完整账号设置字节");
    static void CheckCharacter(TcpTestFixture fixture, ushort id, string account, byte[] payload) =>
        That(fixture.Query("SELECT account,hex(character_options) FROM character_client_settings WHERE character_id=$p0", id) is var rows
            && rows.Length == 1 && Equals(rows[0][0], account) && Equals(rows[0][1], Convert.ToHexString(payload)), "独立读库核对角色归属及完整设置字节");
    static string Settings(TcpTestFixture fixture) => fixture.Snapshot("SELECT * FROM account_client_settings ORDER BY account") +
        fixture.Snapshot("SELECT * FROM character_client_settings ORDER BY character_id") +
        fixture.Snapshot("SELECT * FROM character_game_settings ORDER BY character_id");
    static string Assets(TcpTestFixture fixture) => JsonSerializer.Serialize(fixture.Query(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('account_client_settings','character_client_settings','character_game_settings') ORDER BY name")
        .Select(row => fixture.Query($"SELECT * FROM [{row[0]}] ORDER BY rowid")));
    static void Sql(TcpTestFixture fixture, string sql)
    {
        using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
