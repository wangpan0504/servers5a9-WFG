using System.Collections.Immutable;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Currency;

namespace DfoServer.Game.Quests;

internal sealed record QuestCoverageRow(int Id, string Path, string? Grade, string Condition, int SubType,
    QuestResourceEntry Entry, bool ImplementedPath, ImmutableArray<string> Restrictions,
    ImmutableArray<string> UnconfirmedDeclarations, ImmutableArray<int> MissingReferences)
{
    public string? Name { get; init; }
    public int MinimumLevel { get; init; }
    public int MaximumLevel { get; init; }
    public bool RegionalAutoDaily { get; init; }
    public ImmutableArray<string> SupportedDedicatedPaths { get; init; } = [];
    public ImmutableArray<int> MissingDungeonReferences { get; init; } = [];
    public QuestTargetSupport TargetSupport { get; init; }
    public string TargetKind { get; init; } = nameof(QuestTargetKind.Unknown);
    public string ProgressSources { get; init; } = nameof(QuestProgressSource.None);
    public int? DeclaredSubType { get; init; }
    public int ClientSubType { get; init; }
    public byte? NativeType { get; init; }
    public QuestParameterState ParameterState { get; init; }
    public string ParameterReason { get; init; } = "";
}
internal sealed record QuestCoverageReport(int Definitions, int ImplementedPaths, ImmutableArray<QuestCoverageRow> Quests,
    ImmutableArray<ImmutableArray<int>> PrerequisiteCycles, ImmutableArray<ImmutableArray<int>> ParentCycles)
{
    public bool DungeonReferencesChecked { get; init; }
    public bool DedicatedPathsChecked { get; init; }
    public bool HopePathsChecked { get; init; }
    public ImmutableArray<int> RegionalAutoDailyIds { get; init; } = [];
}

// 覆盖状态是当前可用合同，不是“脚本存在即支持”，也不把专用入口分类命名为废弃。
internal static class QuestCoverage
{
    static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "grade", "difficulty", "npc index", "complete npc index", "job", "grow type", "target character", "non target character",
        "level", "type", "sub type", "int data", "reward type", "reward int data", "reward selection int data", "depend give item",
        "pre required quest", "pre required quest answer", "collision quest", "account collision quest", "account quest", "main quest",
        "dungeon info", "job change quest", "cant giveup", "exposed by npc", "event", "creature kind", "creature level", "expertjob level",
        "first exposed by npc", "relation quest", "exception quest", "exception filter", "multi condition min clear", "hardcore mode only", "condition quest hide",
        "gold multiple", "ignore quest level 4 exp", "attribute", "imagine level", "grow number", "disable quest clear", "npc visibility", "visible npc",
        "reward item trade instance", "go area guide", "auto accept", "monster reward item", "enemy reward item", "clear reward item", "question info", "casecade clear", "show click on complete npc", "dialog show time", "male dialog", "alternative npc index",
        // 下列文字/演出描述由匹配客户端读取；不据此签发服务器进度或奖励。
        "name", "name depend on job", "depend message", "condition message", "solve message", "male depend message", "male solve message", "substitutive names", "dialog", "dialog depend on job",
        "depend message depend on job", "condition message depend on job", "solve message depend on job", "quest item noti", "get quest item noti", "go guide",
        "quest title noti", "quest clear noti", "solve video",
        // Full03 04194292只写Quest文字区的职业描述选择，不承担接取、进度或奖励条件。
        "change depend on grow job"
    };

    public static ImmutableArray<string> Unconfirmed(StoryQuest quest) => quest.Definition?.Fields
        .Where(field => !KnownFields.Contains(field.Name) && !QuestTargetSchema.IsTargetField(quest, field) &&
            !(field.Name is "episode town use" or "npc visivility not revert" && QuestEpisodeEntry.Supports(quest)) &&
            !(field.Name == "npc dialog" && QuestHiddenEntry.NpcDialogueShape(quest)) &&
            !(field.Name == "show quest on event" && QuestEpicRoad2019Entry.Supports(quest)) &&
            !(field.Name is "stand by area" or "hidden set" && QuestStandbyEntry.Supports(quest)) &&
            !(field.Name == "cleared quest" && QuestHiddenEntry.ClearedNpcDialogueShape(quest)) &&
            !(field.Name == "pvp private expose" && DfoServer.Game.Pvp.PvpTrainingExposure.TryRead(quest,out _)) &&
            !(field.Name == "sub reward" && DfoServer.Game.Pvp.PvpTrainingProgress.Manual(quest)) &&
            !(field.Name == "synchronization account quest" && QuestAccountPolicy.ExplicitSynchronization(quest)) &&
            !(field.Name == "appear map" && QuestMarriageProgress.Supports(quest) && field is
                { Children.Length: 0, Values: [{ Type: 0, Value: -1 }, { Type: 0, Value: -1 }, { Type: 0, Value: -1 }, { Type: 0, Value: -1 }] }) &&
            !(QuestMailPlan.IsField(field.Name) && (QuestMailPlan.CompletionDeclaration(quest) || QuestEligibilityMailPlan.Declaration(quest))) &&
            !(field.Name == "searching information" && ExplicitMeetWithoutAutoCheck(quest)) &&
            !(field.Name is "daily random set" or "next relation quest" && HarlemExpRelationGroup(quest)))
        .Select(field => field.Name).Distinct().Order().ToImmutableArray() ?? [];

    // #256：黑市EXP 6051–6060是一组互为next relation的[side]任务。daily random set / next relation quest
    // 只描述客户端分组，不选取、不互斥、不每日重置；各任务完成一次后不再出现。
    // 仅精确形状（空daily random set＋其余九个组员的单个next relation quest）视为已确认，其它任何形状仍保留限制。
    internal const int HarlemExpFirst = 6051, HarlemExpLast = 6060;
    static bool HarlemExpRelationGroup(StoryQuest quest) =>
        quest is { Grade: "[side]", Id: >= HarlemExpFirst and <= HarlemExpLast } && quest.Definition is { } definition &&
        definition.Fields.Where(field => field.Name == "daily random set").ToArray() is [{ Values.Length: 0, Children.Length: 0 }] &&
        definition.Fields.Where(field => field.Name == "next relation quest").ToArray() is [{ Children.Length: 0 } relation] &&
        relation.Values.All(value => value is { Type: 0, Text: null }) &&
        relation.Values.Select(value => value.Value).SequenceEqual(
            Enumerable.Range(HarlemExpFirst,HarlemExpLast - HarlemExpFirst + 1).Where(id => id != quest.Id));
    // OR前置只需一组完整引用；废弃冲突任务和隐藏条件仍报告，但不阻断有效入口。
    internal static bool HasRequiredReferences(StoryQuest quest, QuestCatalog catalog)
    {
        bool Exists(int id) => id <= 0 || catalog.TryGet(id,out _);
        var definition = quest.Definition;
        if (definition is null) return false;
        try { _ = QuestRelationProgress.CascadeMarker(quest,catalog); }
        catch (InvalidDataException) { return false; }
        return
            (definition.PrerequisiteGroups.Length == 0 || definition.PrerequisiteGroups.Any(group => group.All(Exists))) &&
            definition.RequiredAnswers.All(answer => Exists(answer.QuestId)) &&
            (quest.MainQuest is not { } parent || Exists(parent)) &&
            (quest.Condition is not ("[quest clear]" or "[clear quest]" or "[clear multi quest]") || quest.IntData.All(Exists));
    }

    // Full03 02917540→0434CB20把character映射为任务分类3，不读取角色装备槽资格。
    public static bool HasOrdinaryFilter(StoryQuest quest) => quest.Definition is { } definition &&
        definition.Fields.Where(field => field.Name == "exception filter").All(field =>
            field.Children.Length == 0 && field.Values is [{ Type: 6, Text: "character" }]);

    // Full03 04199CDB将urgent写入QST+648例外枚举0；020130C0仅在任务脚本谓词中消费，不改变普通接取资格。
    public static bool HasOrdinaryExceptions(StoryQuest quest) => quest.Definition is { } definition &&
        (definition.Exceptions.Length == 0 && !definition.Has("exception quest") ||
            definition.Exceptions is [{ Type: 6, Text: "[urgent]" }] && definition.Has("exception quest") &&
            definition.Fields.Where(field => field.Name == "exception quest").All(field =>
                field.Children.Length == 0 && field.Values is [{ Type: 6, Text: "[urgent]" }]));

    // Full03 04181940的magic stone/slot expansion为属性28/29，02A36D52复制到客户端任务属性区。
    public static bool HasOrdinaryAttributes(StoryQuest quest) => quest.Definition is { } definition &&
        (ExplicitMeetWithoutAutoCheck(quest) || quest.Attributes.All(SupportedAttribute) &&
            definition.Fields.Where(field => field.Name == "attribute").All(field =>
                field.Children.Length == 0 && field.Values.All(value => value.Type == 6 && SupportedAttribute(value.Text))));

    // Full03的+540=0跳过批量检查；02016AA0仍以真实NPC事件推进sub-1，ACK31保留pending1。
    static bool ExplicitMeetWithoutAutoCheck(StoryQuest quest) =>
        quest is { Condition: "[meet npc]", SubType: -1, IntData: [> 0], Attributes: ["not check auto clear"] } &&
        quest.Definition is { } definition &&
        definition.Fields.Where(field => field.Name == "searching information").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 0, Value: 0, Text: null }] }] &&
        definition.Fields.Where(field => field.Name == "attribute").ToArray() is
            [{ Children.Length: 0, Values: [{ Type: 6, Text: "not check auto clear" }] }];

    // 原生属性25只控制副本交付对话；进度、清图凭据及原接取的领奖校验保持不变。
    static bool SupportedAttribute(string? value) => value is "not give exp quest" or "slot expansion" or "magic stone" or "complete quest in dungeon";

    // 觉醒成就（如5154）省略 [reward type]，奖励数组是 0 0，金币只来自 [gold multiple]。
    internal static bool GoldOnlyReward(StoryQuest quest) =>
        quest.RewardType is null && quest.DeclaredGoldMultiple is > 0 &&
        quest.Rewards.Count > 0 && quest.Rewards.All(row => row.Type == 0 && row.Value == 0);

    public static bool HasFirstNpcExposureDeclaration(StoryQuest quest) => quest.Definition is { } definition &&
        (definition.Fields.Where(field => field.Name == "first exposed by npc").ToArray() switch
        {
            [] => definition.FirstExposedByNpc is null,
            [{ Children.Length: 0, Values: [{ Type: 0, Value: 0 or 1, Text: null }] } field] =>
                definition.FirstExposedByNpc == field.Values[0].Value,
            _ => false
        });

    public static bool HasOrdinaryDisplay(StoryQuest quest) => quest.Definition is { } definition &&
        // 匹配版04199563只写解析局部标志，未复制至QST或签发进度；严格保留原0/1声明。
        HasFirstNpcExposureDeclaration(quest) &&
        // Full03 04195F2F读取可见性；clear/delete同样由真实完成投影触发客户端显示，不签发额外进度或奖励。
        definition.NpcVisibility.All(row => row.Condition is "[accept]" or "[clear]" && row.Visibility is "[show]" or "[hide]" ||
            row.Condition == "[clear]" && row.Visibility == "[delete]") &&
        definition.Fields.Where(field => field.Name is "quest title noti" or "quest clear noti" or "solve video" or "male depend message" or "male solve message" or "visible npc" or "show click on complete npc" or "dialog show time" or "male dialog" or "alternative npc index").All(field => field.Name switch
        {
            "quest title noti" => IsTitleNotification(field),
            "quest clear noti" => IsClearNotification(field),
            "solve video" => field.Children.Length == 0 && field.Values is [{ Type: 6, Text: not null }],
            // Full03 0419432B/04194789只读入QST+F8/+138文本，未改变接取、进度或奖励。
            "male depend message" or "male solve message" => field.Children.Length == 0 && field.Values is [{ Type: 6, Text: not null }],
            // Full03 04193E9A保留QST+7A4有符号NPC id；负值在接受/完成消费者中跳过显示集合处理。
            "visible npc" => field.Children.Length == 0 && field.Values is [{ Type: 0, Text: null }],
            // Full03 0419A095写QST+6A0布尔；02025DD1据此更新已映射完成NPC的本地点击显示。
            "show click on complete npc" => field.Children.Length == 0 && field.Values is [{ Type: 0, Text: null }],
            // Full03 0419C3DB保留QST+87C；02022531复制到本地对话记录的显示时长。
            "dialog show time" => field.Children.Length == 0 && field.Values is [{ Type: 0, Text: null }],
            // Full03 04199388复用八整数/单文本对话reader，仅改变客户端性别对话选择。
            "male dialog" => field.Children.Length == 0 && field.Values.Length == 9 &&
                field.Values.Take(8).All(value => value is { Type: 0, Text: null }) && field.Values[8] is { Type: 6, Text: not null },
            "alternative npc index" => field.Children.Length == 0 && field.Values is
                [{ Type: 0, Text: null }, { Type: 0, Text: null }, { Type: 6, Text: "[cleared quest]" }, { Type: 0, Text: null }],
            _ => false
        });

    public static bool IsClearNotification(QuestField field)
    {
        // Full03 0419C261将图标/文字读入QST+84C/+850/+82C；ACK34以本地QST打开UI0xC4。
        if (field.Values.Length != 0 || field.Children.Length != 2 || field.Children.Any(child => child.Children.Length != 0))
            return false;
        var image = field.Children.FirstOrDefault(child => child.Name == "image info");
        var message = field.Children.FirstOrDefault(child => child.Name == "noti message");
        return image is { Values: [{ Type: 6, Text: not null }, { Type: 0, Value: >= 0, Text: null }] } &&
            message is { Values: [{ Type: 6, Text: not null }] };
    }

    public static bool IsTitleNotification(QuestField field)
    {
        // 已核真实标题由动画路径和五整数fade参数组成；只声明显示，保留原序和值。
        if (field.Values.Length != 0 || field.Children.Length != 2 || field.Children.Any(child => child.Children.Length != 0))
            return false;
        var path = field.Children.FirstOrDefault(child => child.Name is "accept ani path" or "clear ani path");
        var fade = field.Children.FirstOrDefault(child => child.Name is "accept ani fade option" or "clear ani fade option");
        return path is not null && fade is not null &&
            (path.Name,fade.Name) is ("accept ani path","accept ani fade option") or ("clear ani path","clear ani fade option") &&
            path.Values is [{ Type: 6, Text: not null }] && fade.Values.Length == 5 &&
            fade.Values.All(value => value.Type == 0 && value.Text is null);
    }

    public static bool HasOrdinaryCascade(StoryQuest quest)
    {
        if (quest.Definition is not { } definition) return false;
        var fields = definition.Fields.Where(field => field.Name == "casecade clear").ToArray();
        // Full03 0419C64D将真实marker ID读入QST+918；不将它解释成自动完成任意依赖链。
        return fields.Length == 0 || fields is [{ Children.Length: 0, Values: [{ Type: 0, Text: null, Value: >= 1 and < 30000 }] }];
    }

    public static bool HasOrdinaryDeclarations(StoryQuest quest) => Unconfirmed(quest).Length == 0 &&
        quest.Grade != "[daily random]" &&
        quest.Definition is { Diagnostics.Length: 0, GradeModifiers.Length: 0 } definition &&
        (definition.MinimumClearCount is null || QuestRelationProgress.ThresholdShape(quest)) &&
        (!quest.Definition.Has("auto accept") || QuestClientSceneProgress.Cinematic(quest,out _,out _)) &&
        HasOrdinaryFilter(quest) && HasOrdinaryExceptions(quest) && HasOrdinaryAttributes(quest) &&
        HasOrdinaryDisplay(quest) && HasOrdinaryCascade(quest);

    public static void RequireOrdinaryDeclarations(StoryQuest quest)
    {
        var pending = Unconfirmed(quest);
        if (pending.Length != 0) throw new NotSupportedException("任务声明尚未确认: " + string.Join(",", pending));
        if (quest.Grade == "[daily random]")
            throw new NotSupportedException("任务需要对应随机每日列表入口");
        if (!HasOrdinaryFilter(quest))
            throw new NotSupportedException("任务exception filter需要已确认的单字符串character声明");
        if (!HasOrdinaryExceptions(quest))
            throw new NotSupportedException("任务exception quest需要已确认的单字符串[urgent]声明");
        if (!HasOrdinaryAttributes(quest))
            throw new NotSupportedException("任务attribute包含未确认的值或声明形状");
        if (!HasOrdinaryDisplay(quest))
            throw new NotSupportedException("任务NPC可见性、标题、视频或男性对话文本的显示声明形状尚未确认");
        if (!HasOrdinaryCascade(quest))
            throw new NotSupportedException("任务casecade clear需要1..29999的单整数marker声明");
        if (!HasOrdinaryDeclarations(quest))
            throw new NotSupportedException("任务需要尚未确认的分类修饰、例外、首次NPC、阈值或特殊属性");
    }

    internal static bool HasSupportedTarget(StoryQuest quest, QuestCatalog catalog) => QuestTargetRules.Supports(quest, catalog);

    public static QuestCoverageReport Scan(QuestCatalog catalog, ItemCatalog items, EpicPieceCatalog pieces, QuestRewardParameterCatalog parameters, DfoServer.GameWorld.Dungeons.DungeonCatalog? dungeons = null,
        QuestConnectionCatalog? connections = null, DfoServer.GameWorld.Dungeons.OperationHopeCatalog? hope = null)
    {
        var dedicated = QuestDedicatedCoverage.Inspect(catalog,connections,hope);
        var prerequisite = catalog.All.ToDictionary(quest => quest.Id, quest => (quest.Definition?.PrerequisiteGroups.SelectMany(group => group) ?? [])
            .Concat(quest.Definition?.RequiredAnswers.Select(answer => answer.QuestId) ?? []).Where(id => id > 0).Distinct().ToArray());
        var parents = catalog.All.ToDictionary(quest => quest.Id, quest => quest.MainQuest is { } id ? new[] { id } : []);
        var rows = ImmutableArray.CreateBuilder<QuestCoverageRow>();
        foreach (var quest in catalog.All.OrderBy(quest => quest.Id))
        {
            var access = QuestEligibility.InspectResource(quest);
            var reasons = ImmutableArray.CreateBuilder<string>();
            var pending = Unconfirmed(quest);
            var definition = quest.Definition;
            var references = prerequisite[quest.Id].Concat(parents[quest.Id])
                .Concat(definition?.Collisions ?? []).Concat(definition?.AccountCollisions ?? [])
                .Concat(definition?.RelationQuests ?? []).Concat(definition?.HideOnCompletedQuests ?? [])
                .Concat(definition?.AlternativeNpcs.Select(row => row.CompletedQuest) ?? [])
                .Concat(definition?.Values("casecade clear").Where(value => value.Type == 0).Select(value => value.Value) ?? []);
            if (quest.Condition is "[quest clear]" or "[clear quest]" or "[clear multi quest]") references = references.Concat(quest.IntData);
            var missing = references.Where(id => id > 0 && !catalog.TryGet(id, out _)).Distinct().Order().ToImmutableArray();
            var special = dedicated.GetValueOrDefault(quest.Id);
            // 账号标记和希望实例任务走已核验专用事务，不开放普通接取或套用普通永久完成。
            if (special?.AccountMarker == true || special?.InstanceMission == true)
            {
                rows.Add(new(quest.Id,quest.Path,quest.Grade,quest.Condition,quest.SubType,access.Entry,true,[],[],missing)
                {
                    Name = definition?.Values("name").FirstOrDefault().Text,
                    MinimumLevel = quest.MinimumLevel, MaximumLevel = quest.MaximumLevel,
                    SupportedDedicatedPaths = special.Paths
                });
                continue;
            }
            if (access.Entry is not (QuestResourceEntry.Ordinary or QuestResourceEntry.Creature) && !(access.Entry == QuestResourceEntry.Dialogue && QuestRelationProgress.DialogueChild(quest,catalog)) &&
                !(access.Entry == QuestResourceEntry.Hidden && QuestHiddenEntry.Direct(quest,catalog)) &&
                !(access.Entry == QuestResourceEntry.Profession && QuestExpertJobProgress.Tutorial(quest)) && !QuestAutoDailyPlan.Supports(quest))
                reasons.Add(access.Reason);
            if (definition is null || definition.Diagnostics.Length != 0) reasons.AddRange(definition?.Diagnostics ?? ["缺少完整定义"]);
            if (pending.Length != 0) reasons.Add("存在未确认声明");
            if (!HasRequiredReferences(quest,catalog)) reasons.Add("必需任务引用缺失、级联marker不可用或无完整的可选前置组");
            try { _ = QuestRelationProgress.CascadeMarker(quest,catalog); }
            catch (InvalidDataException error) { reasons.Add(error.Message); }
            if (quest.Grade is "[daily random]" or "[training]") reasons.Add("对应每日专用列表未接入");
            if (definition?.GradeModifiers.Length > 0 ||
                definition?.MinimumClearCount is not null && !QuestRelationProgress.ThresholdShape(quest))
                reasons.Add("分类修饰、例外或多条件阈值尚未接入");
            if (!HasOrdinaryFilter(quest)) reasons.Add("exception filter需要已确认的单字符串character声明");
            if (!HasOrdinaryExceptions(quest)) reasons.Add("exception quest需要已确认的单字符串[urgent]声明");
            if (!HasOrdinaryDisplay(quest)) reasons.Add("NPC可见性、标题、视频或男性对话文本的显示声明形状尚未确认");
            if (!HasOrdinaryCascade(quest)) reasons.Add("casecade clear需要1..29999的单整数marker声明");
            {
                bool target = HasSupportedTarget(quest,catalog);
                if (quest.SubType != -1 && !target) reasons.Add("目标子类型尚未接入");
                if (quest.MainQuest is not null && !QuestRelationProgress.DialogueChild(quest,catalog))
                    reasons.Add("隐藏子任务的实际对话及交付入口未接入");
                if (!target) reasons.Add(TargetRestriction(quest));
                if (QuestQuestionProgress.TryCount(quest,out int options) && Enumerable.Range(0,options)
                    .Any(answer => !QuestQuestionProgress.OrdinaryChoice(quest,checked((uint)answer),catalog) && special?.Answers.Contains(answer) != true))
                    reasons.Add("部分回答缺少已核验的专用旅程跳过入口，普通回答分支可用");
                bool profession = QuestProfessionReward.SupportedDeclaration(quest);
                bool characterReward = QuestCharacterReward.TryScalar(quest,out _) || QuestCharacterReward.TryTp(quest,out _) ||
                    QuestCharacterReward.SupportedChallengeReward(quest);
                bool creatureReward = QuestCreatureReward.Evolution(quest);
                bool expertJob = QuestExpertJobReward.Type(quest) is not null;
                bool goldOnly = GoldOnlyReward(quest);
                if (access.Entry == QuestResourceEntry.Creature && catalog.Creatures is null) reasons.Add("宠物目录未装配");
                if (quest.RewardType != "[item]" && !profession && !characterReward && !creatureReward && !expertJob && !QuestRelationProgress.ZeroRewardChild(quest) && !goldOnly)
                    reasons.Add(RewardRestriction(quest));
                if (!quest.Attributes.Contains("not give exp quest") && !parameters.TryGetDifficultyWeight(quest.Difficulty, out _))
                    reasons.Add("奖励显式difficulty未配置合法倍率");
                if (!HasOrdinaryAttributes(quest)) reasons.Add("attribute包含未确认的值或声明形状");
                try
                {
                    if ((QuestMailPlan.CompletionDeclaration(quest) || QuestEligibilityMailPlan.Declaration(quest)))
                        _ = QuestMailPlan.Prepare(quest,items,pieces,DateTimeOffset.UnixEpoch);
                    var rewards = QuestFixedRewardDefinition.Parse(profession || characterReward || creatureReward || expertJob ? quest with { Rewards = [] } : quest, allowGold: true);
                    var selection = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition?.SelectRewards ?? [] });
                    var gifts = QuestFixedRewardDefinition.Parse(quest with { Rewards = definition?.AcceptanceRewards ?? [] });
                    foreach (int id in Enumerable.Range(0, 16).SelectMany(job => Enumerable.Range(0,6).SelectMany(grow =>
                        rewards.ForJob((byte)job,(byte)grow).Concat(selection.ForJob((byte)job,(byte)grow))))
                        .Select(row => row.ItemId).Distinct()) CheckItem(id, false);
                    foreach (int id in Enumerable.Range(0,16).SelectMany(job => Enumerable.Range(0,6)
                        .SelectMany(grow => gifts.ForJob((byte)job,(byte)grow))).Select(row => row.ItemId).Distinct()) CheckItem(id,false,true);
                    if (quest.Condition is "[seeking]" or "[seek n meet npc]" && QuestLifecyclePlan.ValidSeeking(quest))
                        foreach (int id in quest.IntData.Take(QuestLifecyclePlan.MaterialLength(quest)).Where((_, at) => at % 2 == 0).Distinct()) CheckItem(id, true);
                    void CheckItem(int id, bool consume, bool gift = false)
                    {
                        if (CurrencyWallet.RewardKind(id) is { } kind)
                        {
                            if (gift || (consume ? ItemSlotBoundService.CurrencySlot(kind) is null : !QuestCurrencyReward.CanProject(kind)))
                                reasons.Add(gift ? "钱包接取赠品投影尚未接入" : "此钱包没有已确认的普通背包持有域");
                            return;
                        }
                        if (pieces.ByPiece.ContainsKey(id))
                        { reasons.Add(consume ? "碎片材料的交付消费尚未确认" : "特殊持有域奖励投影尚未接入"); return; }
                        if (ItemSlotBoundService.List(items.Get(id).Kind) != InventoryListType.Main &&
                            (consume || gift || items.Get(id).Kind != ItemKind.Creature))
                            reasons.Add(consume ? "材料不属于普通背包持有域" : "特殊持有域奖励投影尚未接入");
                    }
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException or NotSupportedException or FileNotFoundException) { reasons.Add(error.Message); }
            }
            rows.Add(new(quest.Id, quest.Path, quest.Grade, quest.Condition, quest.SubType, access.Entry,
                reasons.Count == 0, reasons.Distinct().ToImmutableArray(), pending, missing)
            {
                Name = quest.Definition?.Values("name").FirstOrDefault().Text,
                MinimumLevel = quest.MinimumLevel, MaximumLevel = quest.MaximumLevel,
                RegionalAutoDaily = QuestAutoDailyPlan.Hunt(quest),
                SupportedDedicatedPaths = special?.Paths ?? [],
                MissingDungeonReferences = dungeons is null ? [] : (quest.Definition?.Dungeons ?? [])
                    .Select(row => row.DungeonId).Where(id => id > 0 && !dungeons.ContainsDungeon(id)).Distinct().Order().ToImmutableArray()
            });
        }
        var result = rows.Select(row =>
        {
            var quest = catalog.Get(row.Id);
            var parameters = QuestTargetSchema.Read(quest);
            return row with { TargetSupport = QuestTargetRules.Inspect(quest, catalog), TargetKind = quest.TargetKind.ToString(),
                DeclaredSubType = quest.DeclaredSubType, ClientSubType = quest.SubType, NativeType = quest.TargetRegistration?.NativeType,
                ParameterState = parameters.State, ParameterReason = parameters.Reason,
                ProgressSources = (quest.TargetRegistration?.Sources ?? QuestProgressSource.None).ToString() };
        }).ToImmutableArray();
        return new(result.Length, result.Count(row => row.ImplementedPath), result, Cycles(prerequisite), Cycles(parents))
        {
            DungeonReferencesChecked = dungeons is not null,
            DedicatedPathsChecked = connections is not null,
            HopePathsChecked = hope is not null,
            RegionalAutoDailyIds = result.Where(row => row.RegionalAutoDaily).Select(row => row.Id).Order().ToImmutableArray()
        };
    }

    // 说明实际依赖；报告不解锁入口，也不把普通客户端通知充当这些玩法的完成事实。
    static string TargetRestriction(StoryQuest quest) => quest.Condition switch
    {
        "[pvp match]" or "[pvp quest]" => "此决斗任务子型尚未具备完整结算事实，不能以普通胜负代替",
        "[pvp rank]" => "段位目标需要单一非负段位及已确认子型",
        "[raid phase clear]" => "团本已有独立领域；此任务仍缺少本版阶段与参与身份的进度映射",
        "[custom multi condition]" when quest.SubType == 1 => "目标依赖特殊塔场景及多条件事件；对应塔流程尚未接入",
        "[custom multi condition]" when quest.Path.Contains("/pvp/",StringComparison.Ordinal) => "目标依赖决斗训练场多条件事件；对应场景及条件通道尚未接入",
        "[custom multi condition]" => "本版多条件类型60已确认；实际场景、条件通道和进度保存尚未接入",
        "[clear multi quest]" => "类型70需要已确认的sub4完整子任务引用与有效最小完成数；其他阈值形状尚未接入",
        "[clear a dungeon]" => "此地下城对或独立通关辅助参数尚未接入已确认的条件算法",
        "[condition under clear]" => "此通关子型、区域筛选或所需统计来源尚未闭合",
        "[play only]" => "接取型目标仅支持原生默认0或显式-1且无附加整数",
        "[get score]" => "此指标或观察场景尚未接入原生33路径",
        "[use skill]" => "技能类别仅确认0/1的原生33，其他类别不能猜测事件",
        "[use item]" or "[use fortune coin]" => "目标依赖实际道具使用或幸运币结果；对应消费事件尚未接入任务进度",
        "[disjoint item]" or "[self disjoint item]" => "目标依赖分解结果及分解者身份；对应领域事务尚未接入任务进度",
        "[equipped item]" => "目标缺少显式物品事实目录或已确认的部位、品级及等级形状",
        "[get item]" => "此获取变体尚无同一次取得的完整物品及队伍事实，不能以库存替代历史",
        "[amplify item]" => "目标依赖增幅结果；增幅领域尚未接入任务进度",
        "[epic production]" => "资源不符合已接入的史诗制作任务形状，不能借用其他制作结果",
        "[send chatting]" => "目标依赖聊天场景及内容事件；对应任务条件尚未接入",
        "[ui click]" => "目标依赖本版界面事件；对应33通道及场景校验尚未接入",
        "[check time]" => "目标依赖时间与场景记录；本版计时起止及进度编码尚未接入",
        "[custom quest]" => "目标依赖自定义场景事件；本版子类型及场景流程尚未接入",
        _ => "目标需要尚未确认的事件或编码: " + quest.Condition + "/" + quest.SubType
    };

    static string RewardRestriction(StoryQuest quest) => quest.RewardType switch
    {
        "[tp]" => "TP奖励需要本版空奖励数组与固定2点声明形状；对应目标仍按实际玩法限制处理",
        "[creature evolution]" => "奖励依赖宠物进化状态；宠物领域尚未接入",
        "[expert job]" => "副职业奖励需要恰好一个 1–4 的类型整数，且不能带选择奖励",
        "[ridable]" => "奖励依赖坐骑状态；坐骑领域尚未接入",
        "[hell legend]" => "奖励依赖对应挑战或深渊资格；专用领域状态及通知尚未接入",
        "[operation hope]" => "奖励依赖代号希望玩法状态；对应领域尚未接入",
        "[grow type]" or "[awakening type]" => "职业奖励声明不属于已接入的合法正常转职或觉醒组合",
        "[slot expansion]" => "装备槽奖励需要本版0/1/2合法索引及无选择奖励的声明",
        _ => "特殊奖励尚未接入: " + quest.RewardType
    };

    // 两种关系独立检查，避免把“父任务等子任务完成、子任务需父任务接取”当作完成前置循环。
    // OR组存在环也只报告结构，不能据此推断所有入口不可达。
    static ImmutableArray<ImmutableArray<int>> Cycles(Dictionary<int, int[]> graph)
    {
        int next = 0;
        var ids = new Dictionary<int, int>(); var low = new Dictionary<int, int>();
        var stack = new Stack<int>(); var active = new HashSet<int>();
        var cycles = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
        foreach (int id in graph.Keys.Order()) if (!ids.ContainsKey(id)) Visit(id);
        return cycles.ToImmutable();
        void Visit(int id)
        {
            ids[id] = low[id] = next++; stack.Push(id); active.Add(id);
            foreach (int target in graph[id].Where(graph.ContainsKey))
            {
                if (!ids.ContainsKey(target)) { Visit(target); low[id] = Math.Min(low[id], low[target]); }
                else if (active.Contains(target)) low[id] = Math.Min(low[id], ids[target]);
            }
            if (low[id] != ids[id]) return;
            var component = ImmutableArray.CreateBuilder<int>(); int member;
            do { member = stack.Pop(); active.Remove(member); component.Add(member); } while (member != id);
            if (component.Count > 1 || graph[id].Contains(id)) cycles.Add(component.Order().ToImmutableArray());
        }
    }
}
