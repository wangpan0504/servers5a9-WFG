using static DfoServer.Testing.TestCheck;
using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;
using DfoServer.Development;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using System.Buffers.Binary;
using DfoServer.Persistence;
using PvfLib;

namespace DfoServer;

internal static class CharacterPreparationTests
{
    public static void RunCore()
    {
        int checks = Count;
        var point = new Location(38,1,450,234);
        var towns = new TownCatalog(new(),point,point,new(0,[],[]),[],new Dictionary<uint,uint[]>());
        var definition = new CharacterPreparation([3448],[],[5151],[3448,5151],[new(179,7)],[],[])
        {
            InitialLearning = new(12345, Array.AsReadOnly(new CharacterSkillTree[]
            {
                new(0, 100, 20, Array.AsReadOnly(new LearnedSkill[] {new(0,179,7)})),
                new(1, 100, 20, Array.AsReadOnly(new LearnedSkill[] {new(0,179,7)}))
            })) {BonusSp=0, BonusTp=0}
        };
        string root = Path.Combine(Path.GetTempPath(),"ServerS5A9-prepared-owner-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root,"game.db");
            var repository = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("prepared-owner",() => {});
            var legacy = repository.Create(owner,new("Legacy",point,point));
            var prepared = repository.Create(owner,definition.ToCharacter("Prepared",towns));
            That(prepared.DungeonState == CharacterDungeonState.NewOrdinary && prepared.Fatigue == CharacterFatigueState.NewOrdinary,
                "离线实验新角色与普通建角一样初始化副本owner及疲劳");
            var reopened = new CharacterRepository(path);
            var saved = reopened.List(owner).Single(row => row.Id == prepared.Id);
            That(saved.DungeonState == CharacterDungeonState.NewOrdinary && saved.Fatigue == CharacterFatigueState.NewOrdinary,
                "实验角色重新开库仍可提供正式副本状态");
            That(saved.Learning.Initialized && saved.Learning.Experience == 12345 && saved.Learning.BonusSp == 0 &&
                saved.Learning.BonusTp == 0 && saved.Learning.Trees.All(tree => tree.Sp == 100 && tree.Tp == 20),
                "实验角色经验、双技能树及点数来源随建角持久化");
            bool rejected = false;
            try { (definition with {InitialLearning=CharacterLearningState.Uninitialized}).ToCharacter("Broken",towns); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "未初始化的实验角色准备在写库前拒绝");
            var reserved = reopened.ReserveDungeonAttempt(owner,saved,257);
            var restored = new CharacterRepository(path).List(owner).Single(row => row.Id == prepared.Id);
            That(restored.DungeonState is {NextObjectKey:257,LastAttempt:1} && restored.DungeonState == reserved.DungeonState &&
                restored.CompletedQuests.SequenceEqual(definition.CompletedQuests) && restored.Skills.SequenceEqual(definition.Skills),
                "实验角色可沿正式仓储预留入场尝试号且保留任务技能");
            That(reopened.List(owner).Single(row => row.Id == legacy.Id) is {DungeonState:null,Fatigue:null},
                "新角色准备修复不替未知存量角色补造副本状态");
        }
        finally { Directory.Delete(root,true); }
        Console.WriteLine($"PASS {Count-checks} 实验角色副本owner及持久化断言");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int count = Count;

        void Reject(Action action, string message)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (Exception ex) when (ex is InvalidDataException or SqliteException)
            {
                rejected = true;
            }

            That(rejected, message);
        }

        var definition = CharacterPreparation.Load(pvf);
        That(definition.MainQuests.Length == 916, "matching Client main quest catalog");
        That(definition.JobQuests.SequenceEqual(new[] { 5151, 5153, 5154, 5155, 5156, 5161, 5162, 5163, 5164, 5165, 5166 }), "Ghostblade job and both awakening chains");
        That(new[] { 10100, 2670, 6928, 8664, 8665, 8672, 6170, 8703, 8709, 2941 }.All(definition.MapQuests.Contains), "map access includes non-epic unlock quests");
        That(definition.CompletedQuests.Contains(8663) && definition.CompletedQuests.Contains(8702), "map unlock prerequisite chains");
        That(!definition.CompletedQuests.Contains(6766), "other job awakening clear-map quests are not map unlocks");
        That(definition.Skills.Length == 15 && definition.Skills.Contains(new CharacterSkill(296, 1)) && definition.Skills.Contains(new CharacterSkill(297, 10)), "匹配CHR声明技能并按95级固定等级资源同步觉醒技能");
        byte[] name = CharacterName.Encoding.GetBytes("剑影");
        That(Convert.ToHexString(name) == "BDA3D3B0", "Client native CP936 character name");
        That(CharacterName.IsValid("剑影") && !CharacterName.IsValid("剑影剑影剑影剑") && !CharacterName.IsValid("😀") && !CharacterName.IsValid("剑影\n"), "bounded encodable names only");
        byte[] request = new byte[4 + name.Length];
        BinaryPrimitives.WriteInt32LittleEndian(request, name.Length);
        name.CopyTo(request, 4);
        That(CharacterRequests.Name(request, 0) == "剑影", "name request CP936 roundtrip");
        byte[] create = new byte[17 + name.Length];
        request.CopyTo(create, 1);
        create[11 + name.Length] = 255;
        That(CharacterRequests.ParseCreate(create).Name == "剑影", "Chinese ordinary creation uses byte count");
        byte[] liveCreate = Convert.FromHexString("00070000006C69636F636F6E000000000000FF0001000000");
        That(CharacterRequests.ParseCreate(liveCreate).Name == "licocon", "matching Client live ordinary creation tail accepted");
        for (byte preview = 0; preview <= 5; preview++)
        {
            byte[] selectedPreview = liveCreate.ToArray();
            selectedPreview[^4] = preview;
            var parsed = CharacterRequests.ParseCreate(selectedPreview);
            That(parsed.Job == 0 && parsed.Name == "licocon" && parsed.Tail[8] == preview,
                "建角界面分支展示索引 " + preview + " 保留请求基础职业与原始尾部");
        }
        foreach (byte preview in new byte[] { 6, 255 })
        {
            byte[] unknownTail = liveCreate.ToArray();
            unknownTail[^4] = preview;
            Reject(() => CharacterRequests.ParseCreate(unknownTail), "超出建角展示范围的索引仍拒绝");
        }
        byte[] capturedCreate = Convert.FromHexString("00020000003131000000000000FF0002000000");
        That(CharacterRequests.ParseCreate(capturedCreate) is { Job: 0, Name: "11", Tail: var capturedTail } && capturedTail[8] == 2,
            "实收男鬼剑士选择鬼泣展示后的建角请求通过解析");
        byte[] unknownJob = liveCreate.ToArray();
        unknownJob[0] = 16;
        Reject(() => CharacterRequests.ParseCreate(unknownJob), "job outside matching PVF list remains rejected");
        byte[] unknownFixed = liveCreate.ToArray();
        unknownFixed[^1] = 1;
        Reject(() => CharacterRequests.ParseCreate(unknownFixed), "remaining creation tail stays fixed");
        Reject(() => CharacterRequests.ParseCreate(liveCreate[..^1]), "truncated live creation rejected");
        Reject(() => CharacterRequests.Name([1, 0, 0, 0, 0x81], 0), "malformed CP936 rejected");
        string root = Path.Combine(Path.GetTempPath(), "ServerS5A9-role-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        var repo = new CharacterRepository(path);
        using var owner = new AccountOwners().Acquire("role-test", () =>
        {
        });
        var existing = repo.Create(owner, new("Existing", towns.Room, towns.InitialReturn) { AcceptedStoryLevel = 1 });
        for (int revision = 0; revision < 7; revision++)
            existing = repo.Save(owner, existing, existing.Position, existing.Return);
        var old = repo.List(owner).Single();
        That(old.Revision == 7 && old.Level == 1 && old.AcceptedStoryLevel == 1 && old.Position == towns.Room && old.Return == towns.InitialReturn && old.CompletedQuests.Length == 0 && old.ActiveMainQuests.Length == 0, "v1 baseline persists the existing role and story level");
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER reject_prepared_skill BEFORE INSERT ON character_skills BEGIN SELECT RAISE(ABORT,'test failure');END";
            cmd.ExecuteNonQuery();
            Reject(() => repo.Create(owner, definition.ToCharacter("回滚", towns)), "failure after quest writes aborts preparation");
            cmd.CommandText = "SELECT (SELECT count(*) FROM characters)+(SELECT count(*) FROM character_completed_quests)+(SELECT count(*) FROM character_skills)";
            That((long)cmd.ExecuteScalar()! == 1, "no partial role or quest rows after failure");
            cmd.CommandText = "DROP TRIGGER reject_prepared_skill";
            cmd.ExecuteNonQuery();
        }

        var role = repo.Create(owner, definition.ToCharacter("剑影", towns));
        That(role.Id == 2 && role.Slot == 1 && role.Level == 95 && role.Job == 0 && role.GrowType == 0x25 && role.AcceptedStoryLevel == 95, "independent Lv95 second-awakening identity");
        Reject(() => repo.Create(owner, definition.ToCharacter("剑影", towns)), "duplicate role preparation rejected");
        var reopened = new CharacterRepository(path);
        var saved = reopened.List(owner).Single(c => c.Id == role.Id);
        var growth = CharacterGrowthCatalog.Load(pvf);
        That(saved.Learning.Initialized && saved.Learning.Experience == growth.GetLevelStartExperience(95) &&
            saved.Learning.BonusSp == 0 && saved.Learning.BonusTp == 0,
            "95级实验角色重开库后保留匹配等级的累计经验与明确点数来源");
        That(CharacterGrowthPlan.AddExperience(saved, 1, growth).Success,
            "准备角色沿正式经验成长规则接受击杀经验，不因快照无效拒绝");
        That(CharacterGrowthPlan.AddExperience(saved with {Learning=saved.Learning with {Experience=0}}, 1, growth).Failure == CharacterGrowthFailure.InvalidSnapshot,
            "95级零经验反例仍被成长校验拒绝，未绕过门禁");
        That(saved.CompletedQuests.SequenceEqual(definition.CompletedQuests) && saved.Skills.SequenceEqual(definition.Skills) && saved.ActiveMainQuests.Length == 0, "quest and skill progress durable");
        var oldAfter = reopened.List(owner).Single(c => c.Id == 1);
        That(oldAfter.Name == old.Name && oldAfter.Revision == old.Revision && oldAfter.Position == old.Position && oldAfter.Level == 1 && oldAfter.AcceptedStoryLevel == 1 && oldAfter.CompletedQuests.Length == 0 && oldAfter.ActiveMainQuests.Length == 0, "other character unchanged");
        byte[] roster = CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(role)]);
        That(BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(17)) == 4 && roster.AsSpan(21, 4).SequenceEqual(name) && roster[27] == 0 && roster[28] == 0x25 && roster[29] == 95, "roster uses CP936 length and native job/grow/level offsets");
        byte[] actor = TownPackets.Actor(role);
        int afterName = 121 + 2 + 4 + name.Length;
        That(actor.AsSpan(afterName, 3).SequenceEqual(new byte[] { 0, 0x25, 95 }), "actor matches roster identity");
        byte[] player = TownPackets.Player(role, new CharacterStatComputer(characters).Compute(role), 1);
        That(player.Length == 257 && player[143] == 255 && player[144] == 15 && player[190] == 15,
            "未开通方案前缀为FF，两组技能仍完整保留原尾部对齐");
        foreach (int tree in new[] { 0, 1 })
        {
            var expanded = role with { Learning = role.Learning with { SelectedTree = tree } };
            byte[] expandedPlayer = TownPackets.Player(expanded, new CharacterStatComputer(characters).Compute(expanded), 1);
            byte[] expected = player.ToArray();
            expected[143] = (byte)tree;
            That(expandedPlayer.SequenceEqual(expected) && TownPackets.Actor(expanded)[^9] == tree,
                "已开通Actor资格与Player当前方案一致，两组技能正文保持 " + tree);
        }
        byte[] complete = TownPackets.CompletedQuests(role);
        That(complete.Length == 30004 && BinaryPrimitives.ReadInt32LittleEndian(complete) == 30000 &&
            complete[4 + 13700] == 1 && complete.Skip(4).Count(x => x == 1) == definition.CompletedQuests.Append(13700).Distinct().Count() &&
            definition.CompletedQuests.All(q => complete[4 + q] == 1), "有界完成表保留真实进度并投影独立月轮山显示标记");
        That(TownPackets.StoryLevel(role).SequenceEqual(new byte[] { 95, 0, 0, 0 }), "accepted StoryDigest level 95");
        var moved = repo.Save(owner, saved, saved.Position with { X = 460 }, saved.Return);
        var afterMove = repo.List(owner).Single(c => c.Id == role.Id);
        That(afterMove.Revision == moved.Revision && afterMove.GrowType == 0x25 && afterMove.CompletedQuests.SequenceEqual(definition.CompletedQuests) && afterMove.AcceptedStoryLevel == 95, "movement preserves preparation");
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            That((string?)cmd.ExecuteScalar() == "ok", "prepared database integrity");
        }

        Console.WriteLine($"PASS {Count - count} character preparation assertions; main={definition.MainQuests.Length}, map={definition.MapQuests.Length}, completed={definition.CompletedQuests.Length}");
        TownTravelTests.Run(towns, definition);
        DockTravelTests.Run(towns, definition);
        RoomPortalTests.Run(towns, definition);
        NpcTravelSpawnTests.Run(towns, definition);
    }
}
