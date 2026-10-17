using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 实验性（#259）：比赛副本（魂·黄龙/青龙大会）对阵表与对手计划。
// 依据来自 Go 项目 dungeon_tournament.go（注释称反汇编 90 项目 DNF.exe），未经 V31 客户端验证：
// 4 轮 16 队；对手从地图 [tournament enemies] 随机取，按战力升序，最弱者与玩家同组；每轮对手 = 玩家对阵位置的另一队；最后一轮当 Boss。
internal sealed record TournamentTeam(IReadOnlyList<TournamentEnemy> Members, bool Player);
internal sealed record TournamentOpponent(int Round, ushort ObjectKey, int Code, byte Level, byte Type);

internal sealed record DungeonTournamentPlan(int PartyCount, IReadOnlyList<IReadOnlyList<TournamentTeam>> Rounds,
    IReadOnlyList<TournamentOpponent> Opponents)
{
    public const int RoundCount = 4;
    public const int TeamCount = 16;

    // 返回含对手的房间计划：对手以 [fixed] monster 追加在原房间怪物之后，按轮次顺序，首轮最先。
    public static DungeonMonsterPlan CreateRoomPlan(DungeonRoomCatalog rooms, DungeonRoomDefinition room, TournamentSource source,
        uint seed, ushort characterId, ushort firstObjectKey, int basisLevel, int? playerTeam)
    {
        var bracket = PlanBracket(source,seed);
        var synthetic = new List<DungeonRoomMonster>();
        for (int round = 0; round < RoundCount; round++)
            foreach (var member in OpponentOf(bracket[round]).Members)
                synthetic.Add(rooms.TournamentMonster(member.Code,source.OpponentX,source.OpponentY,round == RoundCount - 1));
        var extended = room with { Monsters = [.. room.Monsters, .. synthetic] };
        var plan = DungeonMonsterPlan.FromRoom(extended,characterId,firstObjectKey,basisLevel,playerTeam: playerTeam);
        var opponents = new List<TournamentOpponent>();
        int index = 0;
        for (int round = 0; round < RoundCount; round++)
            foreach (var member in OpponentOf(bracket[round]).Members)
            {
                var planned = plan.Monsters.Single(monster => monster.SourceOrdinal == room.Monsters.Count + index && monster.AiCharacter is null);
                if (planned.MonsterId != member.Code) throw new InvalidDataException("比赛对手计划与对阵表不一致");
                opponents.Add(new(round + 1,planned.ObjectKey,member.Code,checked((byte)planned.CandidateLevel),planned.SpawnType));
                index++;
            }
        return plan with { Tournament = new(source.PartyCount,bracket,opponents) };
    }

    static TournamentTeam OpponentOf(IReadOnlyList<TournamentTeam> bracket)
    {
        int player = bracket.ToList().FindIndex(team => team.Player);
        if (player < 0) throw new InvalidDataException("对阵表缺少玩家");
        return bracket[player ^ 1];
    }

    static long Strength(TournamentTeam team) => team.Members.Sum(member => (long)member.Strength);

    public static IReadOnlyList<IReadOnlyList<TournamentTeam>> PlanBracket(TournamentSource source,uint seed)
    {
        int party = source.PartyCount;
        var candidates = source.Enemies.Where(enemy => enemy.PartyCount == party).ToList();
        if (party is < 1 or > 2 || candidates.Count < (TeamCount - 1) * party)
            throw new InvalidDataException("比赛对手数量不足");
        var random = new Random(unchecked((int)seed));
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        var teams = new List<TournamentTeam>();
        for (int i = 0; i < TeamCount - 1; i++)
            teams.Add(new(candidates.Skip(i * party).Take(party).ToArray(),false));
        teams = teams.OrderBy(Strength).ToList();      // OrderBy 稳定
        var first = teams[0];
        var rest = teams.Skip(1).ToList();
        for (int i = rest.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }
        int playerPosition = random.Next(TeamCount);
        var current = new TournamentTeam[TeamCount];
        current[playerPosition] = new(Enumerable.Repeat(new TournamentEnemy(party,0,0),party).ToArray(),true);
        current[playerPosition ^ 1] = first;
        int cursor = 0;
        for (int i = 0; i < TeamCount; i++)
            if (i != playerPosition && i != (playerPosition ^ 1)) current[i] = rest[cursor++];
        var rounds = new List<IReadOnlyList<TournamentTeam>>();
        IReadOnlyList<TournamentTeam> bracket = current;
        for (int round = 0; round < RoundCount; round++)
        {
            rounds.Add(bracket);
            var winners = new TournamentTeam[bracket.Count / 2];
            for (int i = 0; i < winners.Length; i++)
            {
                var a = bracket[2 * i]; var b = bracket[2 * i + 1];
                winners[i] = a.Player || !b.Player && (Strength(a) > Strength(b) || Strength(a) == Strength(b) && random.Next(2) == 0) ? a : b;
            }
            bracket = winners;
        }
        return rounds;
    }
}