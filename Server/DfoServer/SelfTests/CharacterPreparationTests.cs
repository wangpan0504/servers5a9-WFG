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
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters)
    {
        int count = 0;
        void Check(bool ok, string message)
        {
            count++;
            if (!ok)
                throw new Exception("FAIL: " + message);
        }

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

            Check(rejected, message);
        }

        var definition = CharacterPreparation.Load(pvf);
        Check(definition.MainQuests.Length == 915, "matching Client main quest catalog");
        Check(definition.JobQuests.SequenceEqual(new[] { 5151, 5153, 5154, 5155, 5156, 5161, 5162, 5163, 5164, 5165, 5166 }), "Ghostblade job and both awakening chains");
        Check(new[] { 10100, 2670, 6928, 8664, 8665, 8672, 6170, 8703, 8709, 2941 }.All(definition.MapQuests.Contains), "map access includes non-epic unlock quests");
        Check(definition.CompletedQuests.Contains(8663) && definition.CompletedQuests.Contains(8702), "map unlock prerequisite chains");
        Check(!definition.CompletedQuests.Contains(6766), "other job awakening clear-map quests are not map unlocks");
        Check(definition.Skills.Length == 15 && definition.Skills.Contains(new CharacterSkill(296, 1)) && definition.Skills.Contains(new CharacterSkill(297, 1)), "matching CHR declared starter and awakening skills");
        byte[] name = CharacterName.Encoding.GetBytes("剑影");
        Check(Convert.ToHexString(name) == "BDA3D3B0", "Client native CP936 character name");
        Check(CharacterName.IsValid("剑影") && !CharacterName.IsValid("剑影剑影剑影剑") && !CharacterName.IsValid("😀") && !CharacterName.IsValid("剑影\n"), "bounded encodable names only");
        byte[] request = new byte[4 + name.Length];
        BinaryPrimitives.WriteInt32LittleEndian(request, name.Length);
        name.CopyTo(request, 4);
        Check(CharacterRequests.Name(request, 0) == "剑影", "name request CP936 roundtrip");
        byte[] create = new byte[17 + name.Length];
        request.CopyTo(create, 1);
        create[11 + name.Length] = 255;
        Check(CharacterRequests.ParseCreate(create).Name == "剑影", "Chinese ordinary creation uses byte count");
        byte[] liveCreate = Convert.FromHexString("00070000006C69636F636F6E000000000000FF0001000000");
        Check(CharacterRequests.ParseCreate(liveCreate).Name == "licocon", "matching Client live ordinary creation tail accepted");
        byte[] unknownTail = liveCreate.ToArray();
        unknownTail[20] = 2;
        Reject(() => CharacterRequests.ParseCreate(unknownTail), "unobserved creation tail value rejected");
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
        Check(old.Revision == 7 && old.Level == 1 && old.AcceptedStoryLevel == 1 && old.Position == towns.Room && old.Return == towns.InitialReturn && old.CompletedQuests.Length == 0 && old.ActiveMainQuests.Length == 0, "v1 baseline persists the existing role and story level");
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER reject_prepared_skill BEFORE INSERT ON character_skills BEGIN SELECT RAISE(ABORT,'test failure');END";
            cmd.ExecuteNonQuery();
            Reject(() => repo.Create(owner, definition.ToCharacter("回滚", towns)), "failure after quest writes aborts preparation");
            cmd.CommandText = "SELECT (SELECT count(*) FROM characters)+(SELECT count(*) FROM character_completed_quests)+(SELECT count(*) FROM character_skills)";
            Check((long)cmd.ExecuteScalar()! == 1, "no partial role or quest rows after failure");
            cmd.CommandText = "DROP TRIGGER reject_prepared_skill";
            cmd.ExecuteNonQuery();
        }

        var role = repo.Create(owner, definition.ToCharacter("剑影", towns));
        Check(role.Id == 2 && role.Slot == 1 && role.Level == 95 && role.Job == 0 && role.GrowType == 0x25 && role.AcceptedStoryLevel == 95, "independent Lv95 second-awakening identity");
        Reject(() => repo.Create(owner, definition.ToCharacter("剑影", towns)), "duplicate role preparation rejected");
        var reopened = new CharacterRepository(path);
        var saved = reopened.List(owner).Single(c => c.Id == role.Id);
        Check(saved.CompletedQuests.SequenceEqual(definition.CompletedQuests) && saved.Skills.SequenceEqual(definition.Skills) && saved.ActiveMainQuests.Length == 0, "quest and skill progress durable");
        var oldAfter = reopened.List(owner).Single(c => c.Id == 1);
        Check(oldAfter.Name == old.Name && oldAfter.Revision == old.Revision && oldAfter.Position == old.Position && oldAfter.Level == 1 && oldAfter.AcceptedStoryLevel == 1 && oldAfter.CompletedQuests.Length == 0 && oldAfter.ActiveMainQuests.Length == 0, "other character unchanged");
        byte[] roster = CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(role)]);
        Check(BinaryPrimitives.ReadInt32LittleEndian(roster.AsSpan(17)) == 4 && roster.AsSpan(21, 4).SequenceEqual(name) && roster[27] == 0 && roster[28] == 0x25 && roster[29] == 95, "roster uses CP936 length and native job/grow/level offsets");
        byte[] actor = TownPackets.Actor(role);
        int afterName = 121 + 2 + 4 + name.Length;
        Check(actor.AsSpan(afterName, 3).SequenceEqual(new byte[] { 0, 0x25, 95 }), "actor matches roster identity");
        byte[] player = TownPackets.Player(role, characters);
        Check(player.Length == 257 && player[143] == 0 && player[144] == 15 && player[190] == 15, "both native skill lists retain tail alignment");
        byte[] complete = TownPackets.CompletedQuests(role);
        Check(complete.Length == 30004 && BinaryPrimitives.ReadInt32LittleEndian(complete) == 30000 && complete.Skip(4).Count(x => x == 1) == definition.CompletedQuests.Length && definition.CompletedQuests.All(q => complete[4 + q] == 1), "bounded complete quest table");
        Check(TownPackets.StoryLevel(role).SequenceEqual(new byte[] { 95, 0, 0, 0 }), "accepted StoryDigest level 95");
        var moved = repo.Save(owner, saved, saved.Position with { X = 460 }, saved.Return);
        var afterMove = repo.List(owner).Single(c => c.Id == role.Id);
        Check(afterMove.Revision == moved.Revision && afterMove.GrowType == 0x25 && afterMove.CompletedQuests.SequenceEqual(definition.CompletedQuests) && afterMove.AcceptedStoryLevel == 95, "movement preserves preparation");
        using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            Check((string?)cmd.ExecuteScalar() == "ok", "prepared database integrity");
        }

        Console.WriteLine($"PASS {count} character preparation assertions; main={definition.MainQuests.Length}, map={definition.MapQuests.Length}, completed={definition.CompletedQuests.Length}");
        TownTravelTests.Run(towns, definition);
        DockTravelTests.Run(towns, definition);
        RoomPortalTests.Run(towns, definition);
        NpcTravelSpawnTests.Run(towns, definition);
    }
}
