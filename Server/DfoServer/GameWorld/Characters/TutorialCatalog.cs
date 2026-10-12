using System.Collections.Frozen;
using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal sealed record TutorialFlow(byte Job, uint DungeonId, bool IntroAfterTutorial, string IntroPath, Location Destination);
internal readonly record struct TutorialReward(int ItemId, int Count);

internal sealed class TutorialCatalog
{
    public const string FlowPath = "etc/tutorial/tutorialflow.etc";
    public const string RewardPath = "etc/(r)serverparameter.etc";
    readonly FrozenDictionary<byte, TutorialFlow> flows;
    public ImmutableArray<TutorialReward> Rewards { get; }
    public IEnumerable<TutorialFlow> Flows => flows.Values;
    public TutorialFlow? Get(byte job) => flows.GetValueOrDefault(job);

    public TutorialCatalog(PvfArchive pvf)
    {
        var jobs = CharacterCatalog.ReadJobList(pvf).ToDictionary(row =>
            new PvfDocument(pvf.ReadText("character/" + row.Value)).Section("job").Trim('`', ' ', '\r', '\n'),
            row => checked((byte)row.Key), StringComparer.OrdinalIgnoreCase);
        var result = new List<TutorialFlow>();
        foreach (var row in TutorialFile.Flows(pvf.ReadTokens(FlowPath), pvf.ResolveString))
        {
            if (!jobs.TryGetValue(row.Job, out byte job)) continue;
            if (row.DungeonId <= 0 || row.AfterTutorial is not (0 or 1) || string.IsNullOrWhiteSpace(row.IntroPath) ||
                row.Town is < 0 or > 255 || row.Area is < 0 or > 255 || row.X is < 0 or > 65535 || row.Y is < 0 or > 65535)
                throw new InvalidDataException("训练副本、影片或回城参数越界");
            result.Add(new(job, checked((uint)row.DungeonId), row.AfterTutorial == 1, row.IntroPath,
                new((byte)row.Town, (byte)row.Area, (ushort)row.X, (ushort)row.Y, 0)));
        }
        flows = result.ToFrozenDictionary(row => row.Job);
        var values = TutorialFile.Rewards(pvf.ReadTokens(RewardPath), pvf.ResolveString);
        if (values.Any(row => row.Progress != 38 || row.ItemId <= 0 || row.Count <= 0))
            throw new InvalidDataException("训练奖励只能读取完整的已确认检查点38物品/数量声明");
        Rewards = values.Select(row => new TutorialReward(row.ItemId, row.Count)).ToImmutableArray();
        if (Rewards.Length > byte.MaxValue) throw new InvalidDataException("训练奖励超过应答条数范围");
    }
    public bool Owns(byte job, uint dungeonId, DungeonDefinition definition) => Get(job)?.DungeonId == dungeonId && definition.IsTutorial;
}
