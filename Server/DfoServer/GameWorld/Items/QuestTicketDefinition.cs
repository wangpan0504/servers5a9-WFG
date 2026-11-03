using PvfLib;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace DfoServer.GameWorld.Items;

// Full03任务券动作；各动作独立规划，不复用普通券完成范围。
internal sealed record QuestTicketDefinition(int TemplateId, int MinimumLevel, int MaximumLevel, ushort Jobs, ItemExpiration Expiration)
{
    public const uint Action = 77;
    public const uint AchievementAction = 99;
    public const uint SideAction = 183;
    public const uint LevelAction = 182;
    public const uint EpicAction = 181;
    public const uint AllAction = 101;
    public const uint GmAction = 171;
    public const uint CouponAction = 110;
    public const uint SpecifiedAction = 130;
    public const uint FirstAwakeningAction = 119;
    public const uint SecondAwakeningAction = 122;
    // 77原source snapshot不追加字段；130在跳过规划中单独保存动作和原序targets。
    [JsonIgnore] public uint ActionIndex { get; init; } = Action;
    [JsonIgnore] public ImmutableArray<int> TargetQuestIds { get; init; } = [];
    [JsonIgnore] public int? CompletionLevel { get; init; }
    [JsonIgnore] public byte AwakeningStage => ActionIndex switch { FirstAwakeningAction => 1, SecondAwakeningAction => 2, _ => 0 };
    [JsonIgnore] public bool VillageOnly { get; init; }
    [JsonIgnore] public ImmutableArray<string> Places { get; init; } = [];
    static readonly string[] JobNames = ["swordman", "fighter", "gunner", "mage", "priest", "at gunner", "thief", "at fighter",
        "at mage", "demonic swordman", "creator mage", "at swordman", "knight", "demonic lancer", "at priest", "gun blader"];

    public static QuestTicketDefinition Parse(StackableFile raw, ItemTemplate template)
    {
        var action = StackableActionCatalog.Arguments(raw);
        bool achievement = action is { Label: "[achievement quest clear]" or "[side quest clear]", Parameters.Count: 0 } &&
            raw.TypeDeclaration is { Label: "[etc]", Parameters: [1] };
        bool levelClear = action is { Label: "[lv clear]" or "[epic quest clear]", Parameters: [>0] } &&
            raw.TypeDeclaration is { Label: "[etc]", Parameters: [1] };
        bool batch = action is { Label: "[all quest clear]" or "[gm quest clear]", Parameters.Count: 0 } &&
            raw.TypeDeclaration is { Label: "[etc]", Parameters: [1] };
        bool coupon = action is { Label: "[quest clear coupon]", Parameters.Count: 0 } &&
            raw.TypeDeclaration is { Label: "[etc]" or "[stackable]", Parameters: [1] };
        int? declaredMinimum = raw.DeclaredMinimumLevel;
        if ((achievement || levelClear || batch || coupon) && !raw.HasMinimumLevel && declaredMinimum is null) declaredMinimum = template.MinimumLevel;
        if (raw.TemplateId != template.Id || template.Kind != ItemKind.Consumable || action is null ||
            !(achievement || levelClear || batch || coupon || action is { Label: "[quest clear]", Parameters.Count: 0 } || action is { Label: "[any quest clear]", Parameters.Count: > 0 } ||
                action is { Label: "[first awaken clear]", Parameters: [1] or [1, 1] } ||
                action is { Label: "[second awaken clear]", Parameters: [0] or [0, 1] }) ||
            declaredMinimum is not { } minimum || minimum < 0 ||
            raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            (action.Label is "[first awaken clear]" or "[second awaken clear]"
                ? !raw.HasUseActionPacket || raw.DeclaredUseActionPacket != 1
                : raw.HasUseActionPacket && raw.DeclaredUseActionPacket != 0) || raw.HasUsableJobs && raw.DeclaredUsableJobs is null)
            throw new NotSupportedException("物品不是已接入的任务完成券: " + template.Id);
        ImmutableArray<int> targets = action.Label == "[any quest clear]" ? action.Parameters.ToImmutableArray() : [];
        if (targets.Any(id => id is < 1 or >= 30000) || targets.Distinct().Count() != targets.Length)
            throw new InvalidDataException("指定任务券目标必须完整、唯一且属于本版任务ID范围: " + template.Id);
        int maximum = raw.DeclaredMaximumLevel ?? int.MaxValue;
        if (maximum < minimum) throw new InvalidDataException("任务券等级范围无效: " + template.Id);
        bool awakening=action.Label is "[first awaken clear]" or "[second awaken clear]";
        ushort jobs = ushort.MaxValue;
        if (raw.DeclaredUsableJobs is { Count: > 0 } declared)
        {
            jobs = 0;
            foreach (string tag in declared)
            {
                if (tag == "[all]") { jobs = ushort.MaxValue; continue; }
                int job = Array.IndexOf(JobNames,tag.Trim('[',']'));
                if (job < 0) throw new NotSupportedException("任务券未知职业声明: " + tag);
                jobs |= checked((ushort)(1 << job));
            }
        }
        if (jobs != template.Jobs) throw new InvalidDataException("任务券职业声明与物品模板不同: " + template.Id);
        return new(template.Id,minimum,maximum,jobs,template.Expiration)
            { ActionIndex=action.Label switch { "[achievement quest clear]" => AchievementAction, "[side quest clear]" => SideAction,
                "[lv clear]" => LevelAction,
                "[epic quest clear]" => EpicAction,
                "[all quest clear]" => AllAction, "[gm quest clear]" => GmAction, "[quest clear coupon]" => CouponAction,
                "[first awaken clear]" => FirstAwakeningAction,
                "[second awaken clear]" => SecondAwakeningAction, "[any quest clear]" => SpecifiedAction, _ => Action },TargetQuestIds=targets,
                CompletionLevel=levelClear ? action.Parameters[0] : null,
                VillageOnly=awakening && StackableUsePlaces.TownOnly(raw.DeclaredActionUsablePlaces),
                Places=raw.DeclaredActionUsablePlaces?.ToImmutableArray() ?? [] };
    }

    // 期限由创建时Resolve写入实例Core，使用时检查该实际实例，不再重复套资源的历史活动日期。
    public bool Allows(byte level,byte job) => job < 16 && level >= MinimumLevel && level <= MaximumLevel &&
        (Jobs & 1 << job) != 0;
}
