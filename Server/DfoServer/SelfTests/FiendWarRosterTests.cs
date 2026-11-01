using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

// 名册尾部 tail[40]（ARAD 名册行 +0x499）= 超时空漩涡资格：已完成任务 8672；公会不再作为硬门槛。
// 为 0 时客户端在选角前弹 IRDPopupWindow 1099（KB-20261021T015635-5c920e4e59，
// 证据 evidence/popup1099-fiendwar-roster-flag-20261021.txt）。
internal static class FiendWarRosterTests
{
    // 单行名册包末尾固定为 {1,0} + 8 字节 0，行尾 48 字节即 tail。
    const int RosterTrailer = 10;
    const int TailLength = 48;

    public static void Run()
    {
        int checks = TestCheck.Count;
        TestCheck.That(CharacterPackets.FiendWarEligibleTailOffset == 40, "超时空漩涡资格位于名册尾部 tail[40]");
        TestCheck.That(CharacterRosterEntry.FiendWarEntryQuest == 8672, "超时空漩涡资格任务为 8672");
        TestCheck.That(CharacterRosterEntry.IsFiendWarEligible(true, true) && CharacterRosterEntry.IsFiendWarEligible(true, false) &&
            !CharacterRosterEntry.IsFiendWarEligible(false, true) && !CharacterRosterEntry.IsFiendWarEligible(false, false), "资格要求前置任务，取消公会门槛");
        RunPacket();
        RunRepository();
        Console.WriteLine($"PASS {TestCheck.Count - checks} 超时空漩涡名册资格位断言");
    }

    static void RunPacket()
    {
        var position = new Location(38, 0, 100, 100);
        var guild = new Game.Guild.GuildMembership(1, "测试公会", 1, 1, "剑影", "", 0, 0);
        Character Role(int[] quests, bool member) => new Character(123, 2, "剑影", 7, position, position)
            { Level = 95, GrowType = 37, CompletedQuests = quests, Guild = member ? guild : null };
        byte[] eligible = Tail(Role([8672], true));
        byte[] questOnly = Tail(Role([8672], false));
        byte[] guildOnly = Tail(Role([10], true));
        byte[] neither = Tail(Role([], false));
        TestCheck.That(eligible[40] == 1, "完成 8672 且在公会：tail[40]=1");
        TestCheck.That(questOnly[40] == 1, "完成任务且无公会：tail[40]=1");
        TestCheck.That(guildOnly[40] == 0, "缺任务 8672：tail[40]=0");
        TestCheck.That(neither[40] == 0, "两项都缺：tail[40]=0");
        TestCheck.That(eligible[37] == 0 && eligible[38] == 0 && eligible[39] == 0 && eligible[41] == 0,
            "未完成对应任务的 tail[37]/[38]/[39]/[41] 保持 0");
        TestCheck.That(eligible[24] == 3 && eligible[27] == 4, "原有 tail[24]/[27] 不变");
        TestCheck.That(Enumerable.Range(0, TailLength).All(i => i == 40 || eligible[i] == neither[i]), "资格只改变 tail[40] 一个字节");
    }

    static byte[] Tail(Character character) => TailOf(CharacterRosterEntry.FromCharacter(character));

    static byte[] TailOf(CharacterRosterEntry entry)
    {
        byte[] body = CharacterPackets.Roster([entry]);
        return body.AsSpan(body.Length - RosterTrailer - TailLength, TailLength).ToArray();
    }

    static void RunRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-fiendwar-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("fiendwar-roster", () => { });
            var position = new Location(1, 0, 10, 10);
            NewCharacter Role(string name, int[] quests) => new(name, position, position) { CompletedQuests = quests };
            var both = repository.Create(owner, Role("FwBoth", [8672]));
            var questOnly = repository.Create(owner, Role("FwQuest", [8672]));
            var guildOnly = repository.Create(owner, Role("FwGuild", [10]));
            // 旧资格历史仍可读取，但不再开启专用名册标志。
            var beastDone = repository.Create(owner, Role("FwBeast", [5927]));
            var beastMiss = repository.Create(owner, Role("FwPlain", [10]));
            using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'FwGuild','fwguild',$both,0);
                    INSERT INTO guild_members(guild_id,character_id,joined_at_unix) VALUES(1,$both,0),(1,$guild,0);
                    """;
                command.Parameters.AddWithValue("$both", both.Id);
                command.Parameters.AddWithValue("$guild", guildOnly.Id);
                command.ExecuteNonQuery();
            }
            var roster = repository.ListRoster(owner);
            bool Flag(ushort id) => roster.Single(row => row.Id == id).FiendWarEligible;
            TestCheck.That(Flag(both.Id) && Flag(questOnly.Id) && !Flag(guildOnly.Id), "名册查询要求任务完成，无公会也可获得资格");
            TestCheck.That(TailOf(roster.Single(row => row.Id == beastDone.Id))[39] == 0 &&
                TailOf(roster.Single(row => row.Id == beastMiss.Id))[39] == 0, "旧5927历史可读但名册保留字节始终为零");
            var full = repository.List(owner);
            TestCheck.That(CharacterPackets.Roster(roster).SequenceEqual(CharacterPackets.Roster(full.Select(CharacterRosterEntry.FromCharacter).ToArray())),
                "轻量名册与完整角色的资格字节一致");
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }
}
