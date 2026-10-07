using DfoServer.Development.Dungeons;
using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using PvfLib;

namespace DfoServer;

internal static class DungeonEntryProbeTests
{
    public static async Task RunAsync(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = 0;
        void Check(bool condition, string name)
        {
            checks++;
            if (!condition)
                throw new Exception("FAIL: " + name);
        }

        string root = Path.Combine(Path.GetTempPath(), "S5A9-dungeon-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var catalog = DungeonCatalog.Load(pvf);
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("dungeon-probe", () => { });
            using var lifetime = new CancellationTokenSource();
            using var bytes = new MemoryStream();
            await using var sender = new GameConnectionSender(owner, bytes, lifetime, 0, (_, _, _, _, _) => { });
            var session = new GameSession(repository, towns, owner);
            var probe = new DungeonProbeState(session);
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, bytes, lifetime), owner, session, sender, probe);
            using var log = new ServerLog(Path.Combine(root, "logs"));
            var projection = new TownProjection(towns, characters, null, itemResources.EpicPieces);
            var formalHandler = new DungeonHandler(log, catalog, DungeonRoomCatalog.Load(pvf),
                CharacterGrowthCatalog.Load(pvf), DungeonKillExperienceCatalog.Load(pvf), projection);
            var disabled = Registry(formalHandler);
            var information = Registry(new DungeonProbeModule(log, catalog, DungeonEntryProbeMode.Information));
            var town = Registry(new TownHandler(new TownProjection(towns, characters, null, itemResources.EpicPieces), log));
            byte[] selection = Convert.FromHexString("030000000000000000FFFF0000000000490C00000000000000");
            var request = new Packet(1, (ushort)CmdPacketType.SELECT_DUNGEON, selection);
            var finishRequest = new Packet(1, (ushort)CmdPacketType.FINISH_LOADING, new byte[8]);
            var giveupRequest = new Packet(1, (ushort)CmdPacketType.GIVEUP_GAME, []);
            Check(!context.BlocksTownActions, "new connection does not block town commands");
            Check(await disabled.DispatchAsync(context, finishRequest) == CommandResult.Unhandled && bytes.Length == 0,
                "default handler never acknowledges finish loading");
            Check(await disabled.DispatchAsync(context, giveupRequest) == CommandResult.Unhandled && bytes.Length == 0
                && !probe.ReturnStarted, "default handler does not fabricate a dungeon return");

            Check(await disabled.DispatchAsync(context, request) == CommandResult.Unhandled && bytes.Length == 0,
                "default handler does not acknowledge an unselected dungeon request");
            Check(await information.DispatchAsync(context, request) == CommandResult.Unhandled && bytes.Length == 0
                && probe.SelectionEpoch is null, "information probe requires a selected ready character");
            var created = repository.Create(owner, new NewCharacter("Probe", towns.Room, towns.InitialReturn)
            {
                AcceptedStoryLevel = 1,
                ActiveMainQuests = [new(0, 3145, 1)]
            });
            session.Select(created.Slot);
            Check(await information.DispatchAsync(context, request) == CommandResult.Unhandled && bytes.Length == 0,
                "selected character cannot probe before ready");
            MoveToEntry();
            var before = session.Selected ?? throw new Exception("FAIL: probe character missing");
            string persistedBefore = JsonSerializer.Serialize(repository.List(owner).Single());
            long epoch = session.SelectionEpoch;
            Check(await disabled.DispatchAsync(context, request) == CommandResult.Unhandled && bytes.Length == 0
                && probe.SelectionEpoch is null, "default handler leaves a valid dungeon request unacknowledged");
            RejectConfiguration(() => new DungeonProbeModule(log, catalog, DungeonEntryProbeMode.Disabled));
            RejectConfiguration(() => new DungeonProbeModule(log, null!, DungeonEntryProbeMode.Information));
            RejectConfiguration(() => new DungeonProbeModule(log, catalog, DungeonEntryProbeMode.FinishLoadingMonsters));
            var ordinary = new GameConnectionContext(new(5, IPAddress.Loopback, bytes, lifetime), owner, session, sender);
            Check(ordinary.Activity is null && !ordinary.BlocksTownActions
                && await disabled.DispatchAsync(ordinary, request) == CommandResult.Unhandled && bytes.Length == 0,
                "normal selection runs without allocating diagnostic state");
            bool missingState = false;
            try
            {
                await information.DispatchAsync(ordinary, request);
            }
            catch (InvalidOperationException error) when (error.Message == "诊断模块缺少对应连接状态")
            {
                missingState = true;
            }
            Check(missingState && bytes.Length == 0, "diagnostic module rejects mismatched connection wiring");
            var mixed = new GameCommandRegistry();
            new DungeonSelectionHandler(new TownProjection(towns, characters, null, itemResources.EpicPieces), log).Register(mixed);
            formalHandler.Register(mixed);
            bool duplicate = false;
            try
            {
                new DungeonProbeModule(log, catalog, DungeonEntryProbeMode.Information).Register(mixed);
            }
            catch (InvalidOperationException error) when (error.Message.Contains("Duplicate command"))
            {
                duplicate = true;
            }
            Check(duplicate, "normal and diagnostic selection modules cannot both register");

            foreach (int length in new[] { 0, 4, 24, 26, 27 })
            {
                Check(await information.DispatchAsync(context, request with { Body = new byte[length] }) == CommandResult.Unhandled
                    && bytes.Length == 0 && probe.SelectionEpoch is null, "unsupported width does not consume the probe: " + length);
            }

            // 每个待确认字段单独偏离已实收形状，都不能发送或消耗本次选角的诊断机会。
            foreach (int offset in new[] { 5, 7, 8, 9, 11, 15, 20, 21 })
            {
                byte[] altered = (byte[])selection.Clone();
                altered[offset] ^= 1;
                Check(await information.DispatchAsync(context, request with { Body = altered }) == CommandResult.Unhandled
                    && bytes.Length == 0 && probe.SelectionEpoch is null, "unknown field rejected at offset " + offset);
            }

            foreach (int offset in new[] { 0, 4, 16 })
            {
                byte[] altered = (byte[])selection.Clone();
                altered[offset] ^= 1;
                Check(await information.DispatchAsync(context, request with { Body = altered }) == CommandResult.Unhandled
                    && bytes.Length == 0 && probe.SelectionEpoch is null, "unsupported resource request rejected at offset " + offset);
            }

            Check(!context.BlocksTownActions, "unimplemented or rejected requests do not block town commands");

            Check(await information.DispatchAsync(context, request) == CommandResult.Handled && probe.SelectionEpoch == epoch,
                "explicit information mode consumes one probe for this selection");
            VerifyInformationFrames(1);
            Check(context.BlocksTownActions, "sent information blocks town writes for this selection");
            Check(ReferenceEquals(session.Selected, before) && session.Phase == SessionPhase.Town && session.Ready
                && JsonSerializer.Serialize(repository.List(owner).Single()) == persistedBefore,
                "probe does not change phase, position, revision or quest state");
            Check(await information.DispatchAsync(context, request) == CommandResult.Unhandled && bytes.Length == 52,
                "duplicate selection does not resend information or a success acknowledgement");
            Check(await information.DispatchAsync(context, finishRequest) == CommandResult.Unhandled && bytes.Length == 52
                && !probe.LoadingReplied && probe.Plan is null,
                "information-only mode cannot finish a map that was never started");

            var moveTarget = MovementTarget(towns, before);
            foreach (ushort opcode in new ushort[] { 35, 631 })
            {
                var movement = new Packet(1, opcode, MovementBody(moveTarget, opcode == 631));
                Check(await town.DispatchAsync(context, movement) == CommandResult.Handled
                    && ReferenceEquals(session.Selected, before) && bytes.Length == 52
                    && JsonSerializer.Serialize(repository.List(owner).Single()) == persistedBefore,
                    "probe blocks valid town movement without saving: " + opcode);
            }

            var townTarget = new Location(38, 0, 19, 259);
            Check(towns.CanTravel(before.Position, townTarget, before.Level, before.CompletedQuests.ToHashSet(), before.Return),
                "blocked travel fixture is otherwise an allowed PVF route");
            Check(await town.DispatchAsync(context, new(1, 36, AreaBody(before.Position, townTarget))) == CommandResult.Handled
                && ReferenceEquals(session.Selected, before) && bytes.Length == 52
                && JsonSerializer.Serialize(repository.List(owner).Single()) == persistedBefore,
                "probe blocks valid area travel without saving or projecting town packets");

            session.ReturnToRoster();
            Check(!context.BlocksTownActions, "return to roster removes the selected-character probe block");
            Check(await information.DispatchAsync(context, request) == CommandResult.Unhandled && bytes.Length == 52,
                "returning to roster cannot send another probe without a character");
            session.Select(created.Slot);
            session.MarkReady();
            Check(!context.BlocksTownActions, "new selection epoch ignores the old probe marker");
            // 在真实房间状态设置已发诊断标记，避免用本就无法离房的城镇状态误证入口拦截。
            probe.SelectionEpoch = session.SelectionEpoch;
            string roomBefore = JsonSerializer.Serialize(repository.List(owner).Single());
            Check(towns.IsRoom(session.Selected!.Position), "leave-room block fixture has a real room to leave");
            Check(await town.DispatchAsync(context, new(1, 1426, [])) == CommandResult.Handled && bytes.Length == 52
                && towns.IsRoom(session.Selected!.Position) && JsonSerializer.Serialize(repository.List(owner).Single()) == roomBefore,
                "probe blocks a valid leave-room request without position or revision changes");
            session.ReturnToRoster();
            session.Select(created.Slot);
            MoveToEntry();
            var resumed = session.Selected!;
            var resumedTarget = MovementTarget(towns, resumed);
            Check(!context.BlocksTownActions && await town.DispatchAsync(context, new(1, 35, MovementBody(resumedTarget, false))) == CommandResult.Handled
                && session.Selected!.Position == resumedTarget && session.Selected.Revision == resumed.Revision + 1 && bytes.Length == 52,
                "town movement resumes and persists after a fresh selection epoch");
            long nextEpoch = session.SelectionEpoch;
            string persistedAfterReselect = JsonSerializer.Serialize(repository.List(owner).Single());
            Check(nextEpoch != epoch && await information.DispatchAsync(context, request) == CommandResult.Handled
                && probe.SelectionEpoch == nextEpoch, "reselecting permits one new probe for the new selection epoch");
            VerifyInformationFrames(2);
            Check(JsonSerializer.Serialize(new CharacterRepository(Path.Combine(root, "game.db")).List(owner).Single()) == persistedAfterReselect,
                "database reopen confirms the second probe did not persist gameplay changes");

            Check(DungeonEntryPlan.TryCreate(catalog, session.Selected!, 3, 3145, 0, out var plan, out _)
                && plan is not null && plan.StartMap.Id == 76121, "builder fixture comes from the real 3145 first room");
            VerifyBuilders(plan ?? throw new Exception("FAIL: probe plan missing"));
            using (var startBytes = new MemoryStream())
            using (var startLifetime = new CancellationTokenSource())
            {
                await using var startSender = new GameConnectionSender(owner, startBytes, startLifetime, 0, (_, _, _, _, _) => { });
                var startProbe = new DungeonProbeState(session);
                var startContext = new GameConnectionContext(new(2, IPAddress.Loopback, startBytes, startLifetime), owner, session, startSender, startProbe);
                var startCommands = Registry(new DungeonProbeModule(log, catalog, DungeonEntryProbeMode.StartMap));
                Check(!startContext.BlocksTownActions && await startCommands.DispatchAsync(startContext, request) == CommandResult.Handled
                    && startContext.BlocksTownActions, "start-map mode explicitly sends and marks its own diagnostic context");
                byte[] frames = startBytes.ToArray();
                Check(frames.Length == 91, "start-map mode sends exactly two frames with no success acknowledgement");
                int offset = 0;
                foreach (var expected in new[] { (Opcode: (ushort)28, Length: 52, Sequence: (ushort)0), (Opcode: (ushort)29, Length: 39, Sequence: (ushort)1) })
                {
                    var frame = frames.AsSpan(offset, expected.Length);
                    Check(frame[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]) == expected.Opcode
                        && BinaryPrimitives.ReadInt32LittleEndian(frame[3..]) == expected.Length
                        && BinaryPrimitives.ReadUInt16LittleEndian(frame[11..]) == expected.Sequence
                        && BinaryPrimitives.ReadUInt32LittleEndian(frame[7..]) == GameFrame.Crc(frame[11..]),
                        "start-map candidate maintains the notification order and frame contract: " + expected.Opcode);
                    offset += expected.Length;
                }

                Check(frames[68] == 0 && frames[69] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(frames.AsSpan(82)) == 76121,
                    "actual NOTI29 payload selects the PVF task first room");
                Check(await startCommands.DispatchAsync(startContext, request) == CommandResult.Unhandled && startBytes.Length == 91
                    && JsonSerializer.Serialize(repository.List(owner).Single()) == persistedAfterReselect,
                    "start-map candidate is not repeated and does not persist gameplay state");
                Check(await startCommands.DispatchAsync(startContext, finishRequest) == CommandResult.Unhandled && startBytes.Length == 91
                    && !startProbe.LoadingReplied, "start-map mode does not implicitly enable loading replies");
            }

            foreach (string conflict in new[] { "none", "revision", "epoch" })
            {
                using var loadingOwner = owners.Acquire("loading-" + conflict, () => { });
                using var loadingLifetime = new CancellationTokenSource();
                using var loadingBytes = new MemoryStream();
                await using var loadingSender = new GameConnectionSender(loadingOwner, loadingBytes, loadingLifetime, 0, (_, _, _, _, _) => { });
                var loadingCharacter = repository.Create(loadingOwner, new NewCharacter("Load" + conflict, towns.Room, towns.InitialReturn)
                {
                    AcceptedStoryLevel = 1,
                    ActiveMainQuests = [new(0, 3145, 1)]
                });
                var loadingSession = new GameSession(repository, towns, loadingOwner);
                loadingSession.Select(loadingCharacter.Slot);
                loadingSession.MarkReady();
                Check(loadingSession.LeaveRoom() && loadingSession.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                    "loading fixture reaches the actual dungeon entry: " + conflict);
                var loadingProbe = new DungeonProbeState(loadingSession);
                var loadingContext = new GameConnectionContext(new(4, IPAddress.Loopback, loadingBytes, loadingLifetime), loadingOwner, loadingSession, loadingSender, loadingProbe);
                var loadingCommands = Registry(new DungeonProbeModule(log, catalog, DungeonEntryProbeMode.FinishLoading));
                Check(await loadingCommands.DispatchAsync(loadingContext, finishRequest) == CommandResult.Unhandled
                    && loadingBytes.Length == 0 && !loadingProbe.LoadingReplied,
                    "finish-loading mode requires a previously delivered start-map candidate: " + conflict);
                Check(await loadingCommands.DispatchAsync(loadingContext, giveupRequest) == CommandResult.Unhandled
                    && loadingBytes.Length == 0 && !loadingProbe.ReturnStarted,
                    "diagnostic return requires initialized entry state: " + conflict);
                Check(await loadingCommands.DispatchAsync(loadingContext, request) == CommandResult.Handled
                    && loadingBytes.Length == 91 && loadingProbe.Plan is not null && !loadingProbe.LoadingReplied,
                    "successful start-map delivery binds the plan for finish loading: " + conflict);
                Check(await loadingCommands.DispatchAsync(loadingContext, giveupRequest) == CommandResult.Unhandled
                    && loadingBytes.Length == 91 && !loadingProbe.ReturnStarted,
                    "diagnostic return cannot precede finish-loading reply: " + conflict);
                if (conflict == "revision")
                {
                    var target = MovementTarget(towns, loadingSession.Selected!);
                    Check(loadingSession.Move(new(target.X, target.Y, 5, 0, 0, 0)), "loading revision fixture changes a committed character state");
                }
                else if (conflict == "epoch")
                {
                    loadingSession.ReturnToRoster();
                    loadingSession.Select(loadingCharacter.Slot);
                    loadingSession.MarkReady();
                    Check(!loadingContext.BlocksTownActions && loadingProbe.Plan is not null,
                        "loading epoch fixture keeps an old plan after character reselect");
                }

                string beforeLoading = JsonSerializer.Serialize(repository.List(loadingOwner).Single());
                if (conflict != "none")
                {
                    Check(await loadingCommands.DispatchAsync(loadingContext, finishRequest) == CommandResult.Unhandled
                        && loadingBytes.Length == 91 && !loadingProbe.LoadingReplied,
                        "stale resource plan cannot publish loading completion: " + conflict);
                    // 单独隔离回城的身份检查，不让未收尾这一前置条件掩盖旧 revision/epoch。
                    loadingProbe.LoadingReplied = true;
                    Check(await loadingCommands.DispatchAsync(loadingContext, giveupRequest) == CommandResult.Unhandled
                        && loadingBytes.Length == 91 && !loadingProbe.ReturnStarted && !loadingProbe.ReturnedToTown,
                        "stale resource plan cannot return a different town snapshot: " + conflict);
                }
                else
                {
                    foreach (int length in new[] { 0, 4, 7, 9, 25 })
                    {
                        Check(await loadingCommands.DispatchAsync(loadingContext, finishRequest with { Body = new byte[length] }) == CommandResult.Unhandled
                            && loadingBytes.Length == 91 && !loadingProbe.LoadingReplied,
                            "unknown loading width does not consume the reply: " + length);
                    }

                    for (int index = 0; index < 8; index++)
                    {
                        byte[] unknown = new byte[8];
                        unknown[index] = 1;
                        Check(await loadingCommands.DispatchAsync(loadingContext, finishRequest with { Body = unknown }) == CommandResult.Unhandled
                            && loadingBytes.Length == 91 && !loadingProbe.LoadingReplied,
                            "nonzero loading field does not consume the reply at byte " + index);
                    }

                    Check(await loadingCommands.DispatchAsync(loadingContext, finishRequest) == CommandResult.Handled
                        && loadingProbe.LoadingReplied && loadingContext.BlocksTownActions,
                        "matching finish-loading request sends the diagnostic reply once and keeps town writes blocked");
                    byte[] frames = loadingBytes.ToArray();
                    Check(frames.Length == 149, "finish-loading sequence has exactly five frames");
                    int offset = 0;
                    foreach (var expected in new[]
                    {
                        (Kind: (byte)0, Opcode: (ushort)28, Length: 52, Sequence: (ushort)0),
                        (Kind: (byte)0, Opcode: (ushort)29, Length: 39, Sequence: (ushort)1),
                        (Kind: (byte)1, Opcode: (ushort)37, Length: 17, Sequence: (ushort)2),
                        (Kind: (byte)0, Opcode: (ushort)30, Length: 21, Sequence: (ushort)3),
                        (Kind: (byte)0, Opcode: (ushort)3, Length: 20, Sequence: (ushort)4)
                    })
                    {
                        var frame = frames.AsSpan(offset, expected.Length);
                        Check(frame[0] == expected.Kind && BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]) == expected.Opcode
                            && BinaryPrimitives.ReadInt32LittleEndian(frame[3..]) == expected.Length
                            && BinaryPrimitives.ReadUInt16LittleEndian(frame[11..]) == expected.Sequence
                            && BinaryPrimitives.ReadUInt32LittleEndian(frame[7..]) == GameFrame.Crc(frame[11..]),
                            "finish-loading frame order and CRC: " + expected.Opcode);
                        offset += expected.Length;
                    }

                    Check(frames[107] == 1 && frames.AsSpan(124, 5).SequenceEqual(new byte[5]),
                        "ACK37 has only the success byte and NOTI30 has the five-byte candidate");
                    Check(frames[145] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(frames.AsSpan(146)) == loadingCharacter.Id && frames[148] == 1,
                        "NOTI3 publishes the selected character dungeon state after loading notifications");
                    Check(await loadingCommands.DispatchAsync(loadingContext, finishRequest) == CommandResult.Unhandled && loadingBytes.Length == 149,
                        "repeated loading completion never resends ACK37, NOTI30 or player state");
                    foreach (int length in new[] { 1, 4, 8 })
                    {
                        Check(await loadingCommands.DispatchAsync(loadingContext, giveupRequest with { Body = new byte[length] }) == CommandResult.Unhandled
                            && loadingBytes.Length == 149 && !loadingProbe.ReturnStarted && loadingContext.BlocksTownActions,
                            "unknown giveup width does not consume return or unlock town writes: " + length);
                    }

                    var returnCharacter = loadingSession.Selected!;
                    // 这是诊断快照返回；没有建立正式实例，也不生成惩罚费、奖励或清图事实。
                    Check(await loadingCommands.DispatchAsync(loadingContext, giveupRequest) == CommandResult.Handled
                        && loadingProbe.ReturnStarted && loadingProbe.ReturnedToTown && !loadingContext.BlocksTownActions,
                        "successful diagnostic return unlocks town writes after both frames are sent");
                    byte[] returnedFrames = loadingBytes.ToArray();
                    Check(returnedFrames.Length == 195, "diagnostic return adds exactly ACK42 and NOTI24");
                    int returnOffset = 149;
                    foreach (var expected in new[]
                    {
                        (Kind: (byte)1, Opcode: (ushort)42, Length: 17, Sequence: (ushort)5),
                        (Kind: (byte)0, Opcode: (ushort)24, Length: 29, Sequence: (ushort)6)
                    })
                    {
                        var frame = returnedFrames.AsSpan(returnOffset, expected.Length);
                        Check(frame[0] == expected.Kind && BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]) == expected.Opcode
                            && BinaryPrimitives.ReadInt32LittleEndian(frame[3..]) == expected.Length
                            && BinaryPrimitives.ReadUInt16LittleEndian(frame[11..]) == expected.Sequence
                            && BinaryPrimitives.ReadUInt32LittleEndian(frame[7..]) == GameFrame.Crc(frame[11..]),
                            "diagnostic return frame order and CRC: " + expected.Opcode);
                        returnOffset += expected.Length;
                    }

                    var transition = returnedFrames.AsSpan(182, 13);
                    Check(returnedFrames[165] == 1 && transition[0] == returnCharacter.Position.Town && transition[1] == returnCharacter.Position.Area
                        && BinaryPrimitives.ReadUInt16LittleEndian(transition[2..]) == 1
                        && BinaryPrimitives.ReadUInt16LittleEndian(transition[4..]) == returnCharacter.Id
                        && BinaryPrimitives.ReadUInt16LittleEndian(transition[6..]) == returnCharacter.Position.X
                        && BinaryPrimitives.ReadUInt16LittleEndian(transition[8..]) == returnCharacter.Position.Y
                        && transition[10] == returnCharacter.Position.Direction && transition[11] == 1 && transition[12] == 1,
                        "NOTI24 returns the exact town snapshot and ACK42 has only success");
                    Check(await loadingCommands.DispatchAsync(loadingContext, giveupRequest) == CommandResult.Unhandled
                        && loadingBytes.Length == 195 && !loadingContext.BlocksTownActions,
                        "duplicate giveup never repeats return frames or reblocks town");
                }

                Check(JsonSerializer.Serialize(repository.List(loadingOwner).Single()) == beforeLoading && loadingSession.Phase == SessionPhase.Town,
                    "loading diagnostics preserve persisted character state and do not claim a dungeon phase: " + conflict);
            }

            foreach (bool replaceOwner in new[] { false, true })
            {
                string account = replaceOwner ? "probe-owner-race" : "probe-revision-race";
                using var raceOwner = owners.Acquire(account, () => { });
                using var raceLifetime = new CancellationTokenSource();
                using var raceBytes = new HeldProbeStream();
                await using var raceSender = new GameConnectionSender(raceOwner, raceBytes, raceLifetime, 0, (_, _, _, _, _) => { });
                var raceCharacter = repository.Create(raceOwner, new NewCharacter(replaceOwner ? "OwnerRace" : "RevRace", towns.Room, towns.InitialReturn)
                {
                    AcceptedStoryLevel = 1,
                    ActiveMainQuests = [new(0, 3145, 1)]
                });
                var raceSession = new GameSession(repository, towns, raceOwner);
                raceSession.Select(raceCharacter.Slot);
                raceSession.MarkReady();
                Check(raceSession.LeaveRoom() && raceSession.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)),
                    "async race fixture reaches the real dungeon entry");
                var raceProbe = new DungeonProbeState(raceSession);
                var raceContext = new GameConnectionContext(new(3, IPAddress.Loopback, raceBytes, raceLifetime), raceOwner, raceSession, raceSender, raceProbe);
                var raceCommands = Registry(new DungeonProbeModule(log, catalog, DungeonEntryProbeMode.StartMap));
                var pending = raceCommands.DispatchAsync(raceContext, request);
                AccountOwners.Lease? newOwner = null;
                try
                {
                    await raceBytes.Started.WaitAsync(TimeSpan.FromSeconds(5));
                    if (replaceOwner)
                        newOwner = owners.Acquire(account, () => { });
                    else
                    {
                        var target = MovementTarget(towns, raceSession.Selected!);
                        Check(raceSession.Move(new(target.X, target.Y, 5, 0, 0, 0)), "revision race commits a concurrent state change while notification is pending");
                    }

                    string afterConcurrentChange = JsonSerializer.Serialize(repository.List(newOwner ?? raceOwner).Single());
                    raceBytes.Release();
                    bool rejected = false;
                    try
                    {
                        await pending;
                    }
                    catch (InvalidOperationException error) when (error.Message == (replaceOwner ? "Stale account generation" : "Dungeon probe selection changed"))
                    {
                        rejected = true;
                    }

                    byte[] sent = raceBytes.ToArray();
                    Check(rejected && sent.Length == 52 && sent[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(sent.AsSpan(1)) == 28,
                        "state change across information await prevents NOTI29: " + account);
                    Check(JsonSerializer.Serialize(repository.List(newOwner ?? raceOwner).Single()) == afterConcurrentChange,
                        "failed continuation does not rewrite state after the concurrent change: " + account);
                }
                finally
                {
                    raceBytes.Release();
                    newOwner?.Dispose();
                }
            }

            using var replacement = owners.Acquire("dungeon-probe", () => { });
            bool staleRejected = false;
            try
            {
                await information.DispatchAsync(context, request);
            }
            catch (InvalidOperationException error) when (error.Message == "Stale account generation")
            {
                staleRejected = true;
            }

            Check(staleRejected && bytes.Length == 104, "stale owner cannot send an information probe");
            Check(JsonSerializer.Serialize(repository.List(replacement).Single()) == persistedAfterReselect,
                "replacing the owner preserves the character after a rejected old request");

            void MoveToEntry()
            {
                session.MarkReady();
                Check(session.LeaveRoom() && session.Selected!.Position.Town == 38 && session.Selected.Position.Area == 0,
                    "probe fixture follows the actual room exit to Elvengard");
                Check(session.Travel(new(38, 2, 1085, 249, 5, 38, 0, 0)), "probe fixture follows the left dungeon gate route");
            }

            void VerifyInformationFrames(int count)
            {
                byte[] frames = bytes.ToArray();
                Check(frames.Length == count * 52, "information mode sends only the expected complete frames");
                for (int index = 0; index < count; index++)
                {
                    var frame = frames.AsSpan(index * 52, 52);
                    Check(frame[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(frame[1..]) == 28
                        && BinaryPrimitives.ReadInt32LittleEndian(frame[3..]) == 52
                        && BinaryPrimitives.ReadUInt16LittleEndian(frame[11..]) == index
                        && BinaryPrimitives.ReadUInt32LittleEndian(frame[7..]) == GameFrame.Crc(frame[11..]),
                        "information frame is NOTI28 with sequence and valid CRC, without ACK16");
                    var body = frame[16..];
                    Check(BinaryPrimitives.ReadUInt32LittleEndian(body) == 3 && body[4] == 0 && body[7] == 1
                        && body[8] == 4 && body[9] == 0 && body[10] == byte.MaxValue && body[11] == byte.MaxValue
                        && BinaryPrimitives.ReadUInt32LittleEndian(body[20..]) == uint.MaxValue,
                        "information candidate uses the actual task maze and boss position");
                }
            }

            void VerifyBuilders(DungeonEntryPlan plan)
            {
                byte[] informationBody = DungeonPackets.Information(plan);
                Check(informationBody.SequenceEqual(Convert.FromHexString(
                    "03000000000000010400FFFF0000000000000000FFFFFFFF000000000000000000000000")) &&
                    DungeonEntryProbe.BuildInformation(plan).SequenceEqual(informationBody),
                    "正式NOTI28保持独立固定36B预期，诊断委托同一字节实现");
                byte[] start = DungeonPackets.StartMap(plan, uint.MaxValue);
                Check(start.Length == 23 && start[0] == 0 && start[1] == 1
                    && BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(3)) == uint.MaxValue
                    && start[13] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(start.AsSpan(14)) == 76121 && start[22] == byte.MaxValue,
                    "正式start-map纯Builder保留首房间及完整seed，不推进会话或发送");
                Check(DungeonEntryProbe.BuildStartMap(plan, uint.MaxValue).SequenceEqual(start) &&
                    DungeonEntryProbe.BuildFinishLoading().SequenceEqual(DungeonPackets.LoadingNotice()) &&
                    DungeonEntryProbe.BuildPlayerState(plan.CharacterId).SequenceEqual(DungeonPackets.DungeonPlayerState(plan.CharacterId)) &&
                    DungeonEntryProbe.BuildDeath(0x8123).SequenceEqual(DungeonPackets.MonsterDeath(0x8123, [])),
                    "诊断加载、玩家状态及死亡只委托正式纯Builder");
                Check(DungeonPackets.LoadingNotice().SequenceEqual(new byte[5]) &&
                    DungeonPackets.DungeonPlayerState(0x8123).SequenceEqual(new byte[] { 1, 0x23, 0x81, 1 }) &&
                    DungeonPackets.MonsterDeath(0x8123, []).SequenceEqual(new byte[] { 0x23, 0x81, 0, 0, 0, 0, 255, 0, 0 }),
                    "5B加载、4B状态及9B零掉落死亡保持独立固定预期");
                foreach (int value in new[] { -1, 256 })
                {
                    Overflow(() => DungeonPackets.Information(plan with { Maze = plan.Maze with { SourceOrdinal = value } }), "maze ordinal");
                    Overflow(() => DungeonPackets.Information(plan with { BossMap = plan.BossMap with { Position = new(value, 0) } }), "boss x");
                    Overflow(() => DungeonPackets.Information(plan with { BossMap = plan.BossMap with { Position = new(0, value) } }), "boss y");
                    Overflow(() => DungeonPackets.StartMap(plan with { StartMap = plan.StartMap with { Position = new(value, 0) } }, 1), "start x");
                    Overflow(() => DungeonPackets.StartMap(plan with { StartMap = plan.StartMap with { Position = new(0, value) } }, 1), "start y");
                }

                Overflow(() => DungeonPackets.StartMap(plan with { StartMap = plan.StartMap with { Id = -1 } }, 1), "negative map id");
            }

            void Overflow(Action action, string field)
            {
                bool rejected = false;
                try
                {
                    action();
                }
                catch (OverflowException)
                {
                    rejected = true;
                }

                Check(rejected, "candidate builder rejects truncated " + field);
            }

            void RejectConfiguration(Action action)
            {
                bool rejected = false;
                try
                {
                    action();
                }
                catch (ArgumentException)
                {
                    rejected = true;
                }
                Check(rejected, "invalid diagnostic configuration fails before registration");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine($"PASS {checks} dungeon entry probe assertions");
    }

    private static Location MovementTarget(TownCatalog towns, Character character)
    {
        var completed = character.CompletedQuests.ToHashSet();
        for (int delta = -10; delta <= 10; delta++)
        {
            int x = character.Position.X + delta;
            if (delta == 0 || x is < ushort.MinValue or > ushort.MaxValue)
                continue;
            var target = character.Position with { X = (ushort)x };
            if (towns.IsPosition(target, completed))
                return target;
        }

        throw new Exception("FAIL: no valid nearby town movement fixture");
    }

    private static byte[] MovementBody(Location target, bool extended)
    {
        byte[] body = new byte[extended ? 13 : 7];
        BinaryPrimitives.WriteUInt16LittleEndian(body, target.X);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), target.Y);
        body[4] = 5;
        return body;
    }

    private static byte[] AreaBody(Location from, Location target)
    {
        byte[] body = new byte[16];
        body[0] = target.Town;
        body[1] = target.Area;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), target.X);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), target.Y);
        body[6] = 5;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(7), from.Town);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(9), from.Area);
        return body;
    }

    private static GameCommandRegistry Registry(IGameCommandModule handler)
    {
        var commands = new GameCommandRegistry();
        handler.Register(commands);
        commands.Seal();
        return commands;
    }

    private sealed class HeldProbeStream : MemoryStream
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;
        public void Release() => release.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span[1..]) == 28)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
