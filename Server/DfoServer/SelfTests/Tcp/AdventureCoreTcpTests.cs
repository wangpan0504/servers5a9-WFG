using DfoServer.GameWorld.Adventure;
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AdventureCoreTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        var catalog = resources.Adventure;
        That(catalog.MaximumLevel == 35 && catalog.Experience[35] == 3420294941 && catalog.Professions.ContainsKey((0, 5)),
            "本版2018资源包含真实经验表和第五转职，不采用旧linksystem表");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventure"), resources: resources);
        await fixture.StartAsync();
        using (var peer = Open(fixture, "adventurea"))
        {
            That(fixture.Query("SELECT level,experience,revision FROM account_adventures WHERE account='adventurea'") is [[1L, 0L, 0L]],
                "认证完成即初始化账号，不要求首次进入角色");
            peer.Send(645); peer.Expect(1, 645, [1, 0]);
            var selection = peer.Expect(0, 1343).Body;
            Information(selection, 0, "adventurea", 0);
            peer.Send(692, Name("GroupProbe")); peer.Expect(1, 692, [1]);
            peer.Send(1403, [1, 0]); peer.Expect(1, 1403, [0, 3, 0]);
            peer.Send(1403, [0, 0]); var query = peer.Expect(1, 1403).Body;
            That(query[0] == 1 && query.AsSpan(1).SequenceEqual(selection), "选角查询只读本账号，保持owner0且不发实体1349");
            peer.Create("GroupHero");
            peer.Create("GroupTwo");
            ushort other = peer.Id;
            Execute("UPDATE characters SET level=95,grow_type=33 WHERE id=" + other);
            // 非穿戴资产故意损坏；账号查询只能读取离线角色的身份与成长字段。
            Execute("INSERT INTO character_inventory_items(character_id,account,item_core) VALUES(" + other + ",'adventurea','{}'); " +
                "INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES(" + other + ",'adventurea',0,121,last_insert_rowid())");
            peer.Send(1403, [0, 0]);
            var roster = peer.Expect(1, 1403).Body.AsSpan(1).ToArray();
            Information(roster, 0, "adventurea", 2);
            That(U32(roster, 18 + 72 + 48) == 95 && roster[18 + 72] == 0 && roster[18 + 73] == 1 &&
                roster[18 + 72 + 42] == 2, "轻量账号名册展示合法二觉角色及实际频道地域，不装载其坏背包");
            peer.Select();
            Information(peer.AdventureInfo, peer.Id, "adventurea", 2);
            That(peer.AdventureRefresh.Length == 50 && U16(peer.AdventureRefresh, 0) == peer.Id &&
                U32(peer.AdventureRefresh, 2) == 40 && U32(peer.AdventureRefresh, 6) == 1, "入城在实际Actor建立后初始化1349，不把选角信息当实体状态");
            peer.Send(1403, Build(writer => writer.Write(peer.Id)));
            That(peer.Expect(1, 1403).Body[0] == 1, "入城本人查询采用已选角色");
            peer.Send(1403, Build(writer => writer.Write(other))); peer.Expect(1, 1403, [0, 3, 0]);
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
            Execute("UPDATE account_adventures SET name='测试团',revision=7 WHERE account='adventurea'");
            peer.Send(645); peer.Expect(1, 645, [1, 0]);
            Information(peer.Expect(0, 1343).Body, 0, "测试团", 2);
            peer.Send(692, Name("GroupProbe")); peer.Expect(1, 692, [1]);
            That(fixture.Query("SELECT level,experience,revision FROM account_adventures WHERE account='adventurea'") is [[1L, 0L, 7L]],
                "所有信息请求、返回选角及存量高等级角色均不补发历史经验或奖励");
        }
        using (var peer = Open(fixture, "adventureb"))
        {
            peer.Send(645); peer.Expect(1, 645, [1, 0]);
            Information(peer.Expect(0, 1343).Body, 0, "adventureb", 0);
            peer.Send(692, Name("GroupProbe")); peer.Expect(1, 692, [1]);
        }
        await fixture.StopAsync();
        await fixture.StartAsync("adventure-restarted", isolatedProcess: true);
        using (var peer = Open(fixture, "adventurea"))
        {
            peer.Send(645); peer.Expect(1, 645, [1, 0]);
            Information(peer.Expect(0, 1343).Body, 0, "测试团", 2);
            peer.Send(692, Name("GroupProbe")); peer.Expect(1, 692, [1]);
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "账号初始化、角色联动及进程重启保持外键完整");
        Console.WriteLine($"PASS {Count - checks} 冒险团选角初始化、只读查询、账号隔离及冷启动TCP断言");

        void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False;Foreign Keys=True");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }

    static void Information(byte[] body, ushort actor, string name, ushort count)
    {
        That(U16(body, 0) == actor && U32(body, 14) == 7440 && U32(body, 18) == 1 && BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(26)) == 0 &&
            U16(body, 86) == count && U32(body, 18 + 7432) == 0, "完整信息保留7440B、角色数量、初始进度及零胶囊次数");
        int nameSize = checked((int)U32(body, 18 + 7440));
        That(body.Length == 22 + 7440 + nameSize && DecodeName(body, 22 + 7440, nameSize) == name,
            "名称按原生长度及CP936回放");
    }
}
