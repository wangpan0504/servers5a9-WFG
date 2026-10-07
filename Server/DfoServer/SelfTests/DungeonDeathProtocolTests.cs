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
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new Exception("FAIL: " + name);
        }

        byte[] body = Enumerable.Range(0, 88).Select(index => (byte)(index * 17)).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(body, 0xFEDCBA98);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 0x8123);
        Check(DungeonRequests.TryParseDeath(body, out var request) && request is not null,
            "exact 88-byte death report is recognized");
        Check(request!.ObjectKey == 0xFEDCBA98 && request.Reporter == 0x8123,
            "parser preserves full unsigned object key and reporter without narrowing");
        byte[] originalTail = body[6..];
        Check(request.RemainingFields.Count == 82 && request.RemainingFields.SequenceEqual(originalTail),
            "all 82 unknown statistics bytes are retained in order");
        body[6] ^= 255;
        Check(request.RemainingFields.SequenceEqual(originalTail), "parsed statistics own their bytes");
        foreach (int length in new[] { 0, 6, 87, 89, 100 })
            Check(!DungeonRequests.TryParseDeath(new byte[length], out var invalid) && invalid is null,
                "death parser rejects unsupported body width " + length);
        foreach (ushort key in new ushort[] { 0, 1, 0x8123, ushort.MaxValue })
        {
            byte[] notice = DungeonEntryProbe.BuildDeath(key);
            Check(notice.SequenceEqual(new byte[] { (byte)key, (byte)(key >> 8), 0, 0, 0, 0, 255, 0, 0 }),
                "NOTI38 exact nine-byte death footprint for key " + key);
            Check(notice.Length == 9 && BinaryPrimitives.ReadUInt16LittleEndian(notice) == key
                && BinaryPrimitives.ReadUInt16LittleEndian(notice.AsSpan(2)) == 0
                && notice[4] == 0 && notice[5] == 0 && notice[6] == 255 && notice[7] == 0 && notice[8] == 0,
                "death notification preserves u16 key zero count mode grade and extras " + key);
        }
        Console.WriteLine($"PASS {checks} dungeon death protocol assertions");
    }
    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new Exception("FAIL: " + name);
        }

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
            Check(session.LeaveRoom() && session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                "death fixture reaches entry through real session transitions");
            var probe = new DungeonProbeState(session);
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, bytes, lifetime), owner, session, sender, probe);
            var commands = new GameCommandRegistry();
            using var log = new ServerLog(Path.Combine(root, "logs"));
            new DungeonProbeModule(log, DungeonCatalog.Load(pvf),
                DungeonEntryProbeMode.FinishLoadingMonsters, DungeonRoomCatalog.Load(pvf)).Register(commands);
            commands.Seal();
            string snapshot = JsonSerializer.Serialize(repository.List(owner).Single());
            Check(await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.SELECT_DUNGEON,
                Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000"))) == CommandResult.Handled,
                "explicit diagnostic monster mode selects matching task room");
            var firstKeys = probe.Monsters!.Monsters.Select(monster => monster.ObjectKey).ToArray();
            var death = Death(firstKeys[0], character.Id);
            long before = bytes.Length;
            Check(await commands.DispatchAsync(context, death) == CommandResult.Unhandled && bytes.Length == before,
                "handler rejects death before loading without notification");
            Check(await commands.DispatchAsync(context, new Packet(1, (ushort)CmdPacketType.FINISH_LOADING, new byte[8]))
                == CommandResult.Handled, "handler completes initial loading");
            before = bytes.Length;
            Check(await commands.DispatchAsync(context, Death(firstKeys[0], unchecked((ushort)(character.Id + 1))))
                == CommandResult.Unhandled && bytes.Length == before, "wrong reporter cannot declare another character death");
            Check(await commands.DispatchAsync(context, Death(0x10000u + firstKeys[0], character.Id)) == CommandResult.Unhandled
                && bytes.Length == before, "full uint key cannot alias current ushort object");
            Check(await commands.DispatchAsync(context, death) == CommandResult.Handled, "first matching death handled");
            VerifyFrame(before, 0, 38, DungeonEntryProbe.BuildDeath(firstKeys[0]));
            before = bytes.Length;
            Check(await commands.DispatchAsync(context, death) == CommandResult.Handled && bytes.Length == before,
                "duplicate death emits no repeated notification");
            byte[] moveBody = new byte[100];
            moveBody[0] = 1;
            moveBody[1] = 1;
            var move = new Packet(1, (ushort)CmdPacketType.MOVE_MAP, moveBody);
            Check(await commands.DispatchAsync(context, move) == CommandResult.Unhandled && bytes.Length == before,
                "partial roster death does not acknowledge passage");
            foreach (ushort key in firstKeys.Skip(1))
                Check(await commands.DispatchAsync(context, Death(key, character.Id)) == CommandResult.Handled,
                    "remaining current-room death handled " + key);
            before = bytes.Length;
            Check(await commands.DispatchAsync(context, move) == CommandResult.Handled
                && probe.Run!.CurrentMap.Id == 76122, "cleared roster permits adjacent map 76122");
            VerifyFrame(before, 1, 45, [1]);
            byte[] frames = bytes.ToArray();
            int nextOffset = checked((int)before + 17);
            Check(frames[nextOffset] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(nextOffset + 1)) == 29,
                "passage sends ACK45 then NOTI29 in order");
            before = bytes.Length;
            Check(await commands.DispatchAsync(context, death) == CommandResult.Unhandled && bytes.Length == before,
                "old room key is refused while loading the next room");
            Check(JsonSerializer.Serialize(repository.List(owner).Single()) == snapshot,
                "death and passage preserve all persisted quests rewards inventory location and revision");
            session.ReturnToRoster();
            session.Select(character.Slot);
            session.MarkReady();
            string reselectionSnapshot = JsonSerializer.Serialize(repository.List(owner).Single());
            before = bytes.Length;
            Check(await commands.DispatchAsync(context, death) == CommandResult.Unhandled && bytes.Length == before,
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
            Check(stale && bytes.Length == before, "old generation cannot publish a death report");
            Check(JsonSerializer.Serialize(repository.List(replacement).Single()) == reselectionSnapshot,
                "old death and owner replacement cannot change the legitimately reselected character");
            var previousRun = probe.Run!;
            int nextKey = probe.NextObjectKey;
            var nextSession = new GameSession(repository, towns, replacement);
            var nextProbe = new DungeonProbeState(nextSession) { NextObjectKey = 0x900 };
            probe.Reset();
            Check(previousRun.Ended && probe.Run is null && probe.Plan is null && probe.Monsters is null
                && probe.SelectionEpoch is null && !context.BlocksTownActions,
                "old connection cleanup ends and releases its diagnostic state even after owner replacement");
            Check(probe.NextObjectKey == nextKey && nextProbe.NextObjectKey == 0x900,
                "cleanup preserves connection key monotonicity and does not touch the replacement state");

            void VerifyFrame(long offset, byte category, ushort opcode, byte[] expectedBody)
            {
                byte[] sent = bytes.ToArray();
                int start = checked((int)offset);
                int length = BinaryPrimitives.ReadInt32LittleEndian(sent.AsSpan(start + 3));
                Check(length == expectedBody.Length + 16 && sent[start] == category
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
        Console.WriteLine($"PASS {checks} dungeon death handler assertions");
    }

    private static Packet Death(uint key, ushort reporter)
    {
        byte[] body = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(body, key);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), reporter);
        return new Packet(1, (ushort)CmdPacketType.DIE_MONSTER, body);
    }

}
