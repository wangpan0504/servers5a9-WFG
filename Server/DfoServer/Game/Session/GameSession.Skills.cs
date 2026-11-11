using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public SkillLearningFailure InitializeLearning(SkillLearningCatalog catalog, ExpertJobScriptCatalog? expertJobs = null)
    {
        arenaSkillCatalog=catalog;
        var failure=InitializeLearning(character =>
    {
        var result = SkillLearning.Initialize(character, catalog.Get);
        if (!result.Success) return result;
        try
        {
            int contractLevels = catalog.ActiveExpertContractLevels(result.State!.ExpertContractExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            var state = SkillPointLedger.Synchronize(character.Job, character.GrowType, character.Level, result.State!, catalog, contractLevels);
            if (expertJobs is not null && character.ExpertJobType is >= 1 and <= 4)
                state = ExpertJobSkills.Grant(character.Job, state, expertJobs.Skills(character.ExpertJobType), catalog);
            return result with { State = state, TotalSp = state.BonusSp is int bonus ? checked(catalog.TotalSp(character.Level) + bonus) : null,
                TotalTp = state.BonusTp is int tpBonus ? checked(catalog.TotalTp(character.Level) + tpBonus) : null };
        }
        catch (NotSupportedException) { return result; }
        catch (KeyNotFoundException) { return result; }
        });
        if(failure==SkillLearningFailure.None && UsesArenaSkills) InitializeArenaLearning(catalog);
        return failure;
    }

    internal SkillLearningFailure InitializeLearning(Func<byte, ushort, SkillLearningDefinition> definition) =>
        InitializeLearning(character => SkillLearning.Initialize(character, definition));

    private SkillLearningFailure InitializeLearning(Func<Character, SkillLearningResult> plan)
    {
        var captured = owner.Execute(() => (Character: Selected ?? throw new InvalidOperationException("No selected character"),
            SelectionEpoch, Ready, Phase));
        var result = plan(captured.Character);
        return owner.Execute(() =>
        {
            // 初始化发生在入城投影前，不能借用只接受 Town 的选角检查。
            if (pendingRental is not null || HasPendingTownQuestCommit || SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, captured.Character) ||
                Ready != captured.Ready || Phase != captured.Phase)
                throw new InvalidOperationException("Stale skill initialization state");
            if (result.Success && !ReferenceEquals(result.State, captured.Character.Learning))
                Selected = repository.CommitLearning(owner, captured.Character, result.State!, result.TotalSp, result.TotalTp);
            return result.Failure;
        });
    }

    public SkillLearningResult LearnSkills(SkillLearningCommand request, SkillLearningCatalog catalog) => ApplySkillLearning(character =>
        SkillLearning.Plan(character.Job, character.GrowType, SkillLevel(character, catalog), character.Learning, request, catalog,
            DfoServer.Game.Quests.QuestEligibility.Completed(character).Contains(355)));

    public SkillLearningResult CustomizeSkills(IReadOnlyList<SkillCommandChange> changes, bool reset, SkillLearningCatalog catalog) =>
        ApplySkillLearning(character => SkillCustomization.Commands(character.Job, character.Learning, changes, reset, catalog));

    public SkillLearningResult ChangeSkillTree(byte current) => ApplySkillLearning(character =>
        !character.Learning.Initialized || current > 1 || character.Learning.SelectedTree != current
            ? new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>())
            : new(SkillLearningFailure.None, character.Learning with { SelectedTree = 1 - current }, Array.Empty<LearnedSkill>()));

    // 自动组合排列由客户端本地生成：510只确认开始，随后的第一次508才是客户端提交的排列。
    // 窗口绑定选择代次，避免之后的手工保存被误判为自动组合。
    private (byte Context, long ExpiresAt, long SelectionEpoch)? autoComboPending;

    // 完整NOTI19回放后客户端会延迟重建技能界面，重建出的默认连招可能回传C2S508；
    // 该回传不是玩家编辑，窗口内只确认不提交，避免默认排列覆盖服务端存档。
    private (long ExpiresAt, long SelectionEpoch)? comboRebuildPending;

    public void MarkComboRebuild()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        owner.Execute(() => comboRebuildPending = (now + 2, SelectionEpoch));
    }

    public SkillLearningResult CustomizeCombo(byte context, IReadOnlyList<SkillCombo> combos, SkillLearningCatalog catalog) =>
        ApplySkillLearning((character, autoCombo) => SkillCustomization.Combo(character.Job, character.Learning, context, combos, catalog, autoCombo), context);

    public SkillLearningResult BeginAutoCombo(byte context, SkillLearningCatalog catalog)
    {
        // 返回选角后的迟到自动组合请求沿已有拒绝路径处理，不创建排列窗口或关闭连接。
        var captured = owner.Execute(() => (Character: Selected,
            SelectionEpoch, Phase, Allowed: !AntonRejoinPresentationPending && pendingRental is null && !HasPendingTownQuestCommit && Ready && !HasClassExperienceContext && !IsImperialArenaCombat && Selected?.Position is not {Town:10,Area:4}));
        if (!captured.Allowed || captured.Character is not { } character ||
            character.Job != 9 || !ReadSkillLearning(character).Initialized || context > 1)
            return new(SkillLearningFailure.Unsupported, null, Array.AsReadOnly(Array.Empty<LearnedSkill>()));
        // 目录查询只读，规划放锁外；回锁后按选择代次重新校验。
        SkillLearningResult planned;
        try { planned = SkillCustomization.PrepareAutoCombo(character.Job, ReadSkillLearning(character), context, catalog); }
        catch (KeyNotFoundException) { planned = new(SkillLearningFailure.Unsupported, null, Array.AsReadOnly(Array.Empty<LearnedSkill>())); }
        return owner.Execute(() =>
        {
            if (AntonRejoinPresentationPending || pendingRental is not null || HasPendingTownQuestCommit || !Ready || HasClassExperienceContext || IsImperialArenaCombat ||
                Phase != captured.Phase || SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, character))
                throw new InvalidOperationException("Stale auto combo state");
            if (!planned.Success)
                return planned;
            if (!ReferenceEquals(planned.State, ReadSkillLearning(character)))
                Selected = CommitActiveSkills(character, planned.State!,layout:true);
            // 实测客户端在收到510回执后立即回传508；5秒窗口足够且不会误伤随后的手工编辑。
            autoComboPending = (context, DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeSeconds(), captured.SelectionEpoch);
            return new SkillLearningResult(SkillLearningFailure.None, ReadSkillLearning(Selected!),
                Array.AsReadOnly(Array.Empty<LearnedSkill>()))
            {
                RequiresSynchronization = planned.RequiresSynchronization,
                Receipt = new(Selected, owner.Generation, captured.SelectionEpoch) { Phase = captured.Phase }
            };
        });
    }

    internal SkillLearningResult LearnSkills(SkillLearningCommand request, Func<byte, ushort, SkillLearningDefinition> definition) => ApplySkillLearning(character =>
        SkillLearning.Plan(character.Job, character.GrowType, character.Level, character.Learning, request, definition));

    public SkillLearningResult ResetSkills(byte context, byte mode, SkillLearningCatalog catalog) => ApplySkillLearning(character =>
        mode > 1 ? new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>()) :
            SkillPointLedger.Reset(character.Job, character.GrowType, SkillLevel(character, catalog), character.Learning, context, catalog,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private SkillLearningResult ApplySkillLearning(Func<Character, SkillLearningResult> plan) =>
        ApplySkillLearning((character, _) => plan(character), null);

    private SkillLearningResult ApplySkillLearning(Func<Character, bool, SkillLearningResult> plan, byte? comboContext)
    {
        // 返回选角后客户端可能补发迟到的技能写入包：无选中角色属于可拒绝请求，不能抛出关闭连接。
        var captured = owner.Execute(() =>
        {
            var character = Selected;
            bool allowed = !AntonRejoinPresentationPending && character is not null && pendingRental is null && !HasPendingTownQuestCommit && Ready && !HasClassExperienceContext && !IsImperialArenaCombat && Selected?.Position is not {Town:10,Area:4};
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool autoCombo = false;
            if (allowed && comboContext is byte expected && autoComboPending is { } pending)
            {
                // 510之后的第一次连招保存就是客户端提交的自动排列；窗口一次性消费。
                autoComboPending = null;
                autoCombo = pending.Context == expected && pending.SelectionEpoch == SelectionEpoch && pending.ExpiresAt >= now;
            }
            // 自动组合提交优先于重建窗口，否则紧跟在完整回放后的自动排列会被当成回传吞掉。
            bool echo = allowed && !autoCombo && comboContext is not null &&
                comboRebuildPending is { } rebuild && rebuild.SelectionEpoch == SelectionEpoch && rebuild.ExpiresAt >= now;
            return (Character: character, SelectionEpoch, Phase, Allowed: allowed, AutoCombo: autoCombo, Echo: echo);
        });
        if (!captured.Allowed || captured.Character is not { } character)
            return new(SkillLearningFailure.Unsupported, null, Array.AsReadOnly(Array.Empty<LearnedSkill>()));

        // 完整技能树重建后的回传只回成功回执，不改变服务端已保存的连招。
        if (captured.Echo)
            return owner.Execute(() =>
            {
                if (AntonRejoinPresentationPending || pendingRental is not null || HasPendingTownQuestCommit || !Ready || HasClassExperienceContext || IsImperialArenaCombat || Phase != captured.Phase || SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, character))
                    throw new InvalidOperationException("Stale skill learning state");
                return new SkillLearningResult(SkillLearningFailure.None, ReadSkillLearning(Selected!), Array.AsReadOnly(Array.Empty<LearnedSkill>()))
                {
                    Receipt = new(Selected, owner.Generation, SelectionEpoch) { Phase = Phase }
                };
            });
        SkillLearningResult result;
        try { result = plan(ReadSkillCharacter(character), captured.AutoCombo); }
        catch (NotSupportedException) { return new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>()); }
        catch (KeyNotFoundException) { return new(SkillLearningFailure.Unsupported, null, Array.Empty<LearnedSkill>()); }
        return owner.Execute(() =>
        {
            if (AntonRejoinPresentationPending || pendingRental is not null || HasPendingTownQuestCommit || !Ready || HasClassExperienceContext || IsImperialArenaCombat || Phase != captured.Phase || SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, character))
                throw new InvalidOperationException("Stale skill learning state");
            if (!result.Success)
                return result;
            if (!ReferenceEquals(result.State, ReadSkillLearning(character)))
                Selected = CommitActiveSkills(character, result.State!, result.TotalSp, result.TotalTp);
            if (ReadSkillLearning(Selected!).SelectedTree != ReadSkillLearning(character).SelectedTree)
            {
                // 切页后旧页的自动排列及重建回传窗口失效，不能误处理新页编辑。
                autoComboPending = null;
                comboRebuildPending = null;
            }
            return result with
            {
                State = ReadSkillLearning(Selected!),
                Receipt = new(Selected, owner.Generation, SelectionEpoch)
                {
                    Phase = Phase,
                    PreviousSkillSwitchInventory = character.SkillSwitchInventory
                }
            };
        });
    }

    public bool CanPublishLearning(SkillLearningReceipt receipt) => owner.Execute(() =>
        Ready && receipt.Generation == owner.Generation && Phase == receipt.Phase &&
        SelectionEpoch == receipt.SelectionEpoch && Selected?.Id == receipt.Character.Id &&
        ReferenceEquals(Selected, receipt.Character));

    public SkillLayoutResult MoveSkillSlot(SkillSlotChangeCommand command, SkillLearningCatalog catalog) => MoveSkillSlot(command, catalog.Get);

    internal SkillLayoutResult MoveSkillSlot(SkillSlotChangeCommand command, Func<byte, ushort, SkillLearningDefinition> definition)
    {
        // 与连招保存同因：返回选角后的迟到 C2S28 属于可拒绝请求，不能抛出关闭连接。
        var captured = owner.Execute(() => (Character: Selected, SelectionEpoch, Phase,
            Allowed: !AntonRejoinPresentationPending && Selected is not null && pendingRental is null && !HasPendingTownQuestCommit && Ready && !HasClassExperienceContext && !IsImperialArenaCombat && Selected?.Position is not {Town:10,Area:4}));
        if (!captured.Allowed || captured.Character is not { } character)
            return new(SkillLearningFailure.Unsupported, null);
        var result = SkillLayout.Move(character.Job, ReadSkillLearning(character), command, definition);
        return owner.Execute(() =>
        {
            if (AntonRejoinPresentationPending || pendingRental is not null || HasPendingTownQuestCommit || !Ready || HasClassExperienceContext || IsImperialArenaCombat || Phase != captured.Phase || SelectionEpoch != captured.SelectionEpoch || !ReferenceEquals(Selected, character))
                throw new InvalidOperationException("Stale skill layout state");
            if (!result.Success)
                return result;
            // 同槽或缔造者双空换栏只确认原事实；没有布局变化不申请writer或递增revision。
            if (!ReferenceEquals(result.State, ReadSkillLearning(character)))
                Selected = CommitActiveSkills(character, result.State!,layout:true);
            return result with { State = ReadSkillLearning(Selected!), Receipt = new(Selected, owner.Generation, SelectionEpoch) { Phase = Phase } };
        });
    }
}
