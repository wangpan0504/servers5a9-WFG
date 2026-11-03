using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Testing;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class TournamentSettlementTests
{
    static void VerifyExperienceMarkers()
    {
        var tournament = new DungeonDefinition(123, "fixture/tournament.dgn", 75, 75, [],
            [new("tournament dungeon", [new(0, 1)]), new("disable exp", [])]);
        That(tournament.DisableMonsterAndClearExperience,
            "大会明确disable exp标记沿现有零经验合同冻结，不补造正经验");
        foreach (DungeonRawSection[] sections in new DungeonRawSection[][]
        {
            [new("disable exp", [])],
            [new("tournament dungeon", [new(0, 1)])],
            [new("tournament dungeon", [new(0, 0)]), new("disable exp", [])],
            [new("tournament dungeon", [new(0, 1)]), new("tournament dungeon", [new(0, 1)]), new("disable exp", [])],
            [new("tournament dungeon", [new(0, 1)]), new("disable exp", [new(0, 1)])]
        })
            That(!(tournament with { RawSections = sections }).DisableMonsterAndClearExperience,
                "普通副本、缺失或畸形大会身份及带值禁经验标签不能取得大会零经验合同");
        That((tournament with { RawSections = [new("no experience monster and result", [])] }).DisableMonsterAndClearExperience,
            "已有普通副本no experience monster and result合同保持");
    }

    static void VerifyResourceExperiencePolicies(PvfArchive pvf)
    {
        var dungeons = SelfTestCatalogs.Dungeons(pvf);
        var rooms = SelfTestCatalogs.Rooms(pvf);
        var actor = new Character(1, 0, "TournamentExp", 0, new(38, 1, 1, 1), null) { Level = 90, GrowType = 19 };
        foreach (int id in new[] { 120, 121, 122, 123, 124 })
        {
            var definition = dungeons.Get(id);
            var rewards = definition.TournamentRewards ?? throw new InvalidDataException("大会缺少自身经验表");
            bool disabled = id is 123 or 124;
            That(definition.DisableMonsterAndClearExperience == disabled &&
                (rewards.Experience[4] == 0) == disabled,
                $"真实大会禁经验标记与自身四轮经验表一致：{id}");
            byte difficulty = definition.TryGetDesignatedDifficulty(out var fixedDifficulty) ? fixedDifficulty : (byte)0;
            That(DungeonEntryPlan.TryCreate(dungeons, actor, (uint)id, 0, difficulty, out var entry, out var reason) &&
                entry!.DisableMonsterAndClearExperience == disabled,
                $"90级大会入场冻结资源禁经验合同：{id}/{reason}");
            var room = rooms.Get(entry!.StartMap, requirePassiveDefinitions: false);
            var plan = DungeonTournamentPlan.CreateRoomPlan(rooms, room, rooms.ReadTournamentSource(entry.StartMap), 1,
                actor.Id, 100, entry.BasisLevel!.Value, entry.PlayerTeam);
            var authority = new NativeDungeonCombatAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out reason) &&
                authority.BindTo(run!) && run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                $"真实大会零经验验证使用完整四轮Run：{id}/{reason}");
            var active = run!;
            foreach (var opponent in plan.Tournament!.Opponents)
            {
                var issued = authority.ValidateAndIssue(active, active.Snapshot.Identity,
                    new(opponent.ObjectKey, actor.Id, 0x0111, actor.Id, 0, 0, 0, opponent.Level, 0, 0, 0, true, new string('A', 64)));
                That(issued is { Outcome: DungeonRunOutcome.Accepted, Death: not null } &&
                    active.AcceptConfirmedDeath(issued.Death!).Outcome == DungeonRunOutcome.Accepted,
                    $"禁经验不撤销真实轮次死亡与通关资格：{id}/{opponent.Round}");
            }
            var reward = new DungeonClearExperienceReward(rewards.Experience[4], 0,
                rewards.Experience[4], -1, DungeonTournamentRewards.Rule, definition.Path);
            var wrong = disabled ? reward with { BaseExperience = 1, TotalExperience = 1 }
                : reward with { BaseExperience = 0, TotalExperience = 0 };
            That(active.PrepareSettlement(active.ClearFact!, [1], wrong).Outcome == DungeonRunOutcome.Rejected &&
                active.PrepareSettlement(active.ClearFact!, [1], reward) is { Outcome: DungeonRunOutcome.Accepted, Pending: not null },
                $"真实大会仅接受与冻结禁经验合同相符的结算，普通大会仍拒绝零值：{id}");
        }
    }
}
