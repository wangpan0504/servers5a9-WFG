using DfoServer.Game.ImperialArena;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class ImperialArenaProjection(IImperialArenaStore store, IImperialArenaRunStore runs,
    IImperialArenaChallengeStore challenges, ImperialArenaCatalog catalog,
    DfoServer.Game.Characters.CharacterStatComputer stats, DfoServer.Infrastructure.ServerLog log,
    DfoServer.GameWorld.Characters.SkillLearningCatalog learning, DfoServer.GameWorld.Items.ItemCatalog items,
    TownProjection town, TimeProvider? clock = null)
{
    public IImperialArenaStore Store { get; } = store;
    public ImperialArenaCatalog Catalog { get; } = catalog;
    public DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    public async Task RefreshPeriodAsync(GameConnectionContext context)
    {
        if (context.Session.ReadImperialArena(Store, Now) is { } snapshot && context.Session.CanPublishImperialArena(snapshot))
            await DeliverAsync(context, snapshot);
    }
    public ImperialArenaChallenge ReadChallenge(GameConnectionContext context) =>
        challenges.ReadImperialArenaChallenge(context.Owner, Catalog, ImperialArenaPeriod.At(Now));

    public ImperialArenaChallengeCommit? ClaimChallenge(GameConnectionContext context)
    {
        var now = Now;
        if (context.Session.PrepareImperialArenaChallenge(challenges, Catalog, now) is not { } prepared) return null;
        var template = items.Get(Catalog.ChallengeRewardItemId);
        if (template.Kind != DfoServer.GameWorld.Items.ItemKind.Consumable || template.StackLimit < 1 ||
            !template.AttachType.Contains("[account]", StringComparison.Ordinal) || !template.Expiration.SupportsCreation)
            throw new InvalidDataException("帝国荣誉道具配置无效");
        var core = Game.Inventory.Services.InventoryRewardGrantService.Create(template, 1, now);
        var item = Game.Inventory.Services.InventoryRewardGrantService.CreateItem(template, new(Game.Inventory.Models.InventoryListType.Main, 9), core);
        return context.Session.CommitImperialArenaChallenge(challenges, Catalog,
            new(prepared.Character, prepared.Generation, prepared.Epoch, prepared.Challenge, item, now.ToUnixTimeSeconds()));
    }

    public Game.Dungeons.FormalDungeonDeathResult? Commit(GameConnectionContext context, Game.Session.ImperialArenaDeathPreparation pending) =>
        context.Session.CommitFormalImperialArenaDeath(pending, Catalog, items, runs);

    public async Task DeathAsync(GameConnectionContext context, ImperialArenaDeathCommit commit, Func<bool> guard)
    {
        await context.Notify(NotiPacketType.TAG_TOURNAMENT_NORMAL_REWARD, ImperialArenaPackets.Reward(commit.Plan.Reward),
            "imperial_arena_round_reward", guard);
        if (commit.MailId != 0)
            await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "imperial_arena_reward_mail", guard);
        // 逐轮奖励窗不锁存副本通关；仅已提交的最后一轮发31，专用场景不会据此请求普通翻牌。
        if (commit.Plan.Round == commit.Plan.Admission.Route.Opponents(commit.Plan.Admission.Mode).Length)
        {
            await context.Notify(NotiPacketType.ENABLE_CLEAR_DUNGEON, [], "imperial_arena_clear_enabled", guard);
            await context.Notify(NotiPacketType.TAG_TOURNAMENT_RANKING_RESULT,
                ImperialArenaPackets.Result(commit.Plan.ElapsedMilliseconds), "imperial_arena_result", guard);
        }
        log.Write("imperial_arena_round_committed", new { Connection = context.Id, commit.Plan.Round, commit.MailId,
            commit.Duplicate, commit.Plan.ElapsedMilliseconds });
    }

    public async Task ReturnAsync(GameConnectionContext context, Game.Town.TownTravelReceipt receipt)
    {
        if (!context.Session.CanPublishTravel(receipt)) return;
        var mails = Store.CompleteImperialArenaRuns(context.Owner);
        if (context.Session.ReadImperialArena(Store, Now) is not { } snapshot) return;
        bool Guard() => context.Session.CanPublishTravel(receipt) && context.Session.CanPublishImperialArena(snapshot);
        if (!mails.IsEmpty)
            await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "imperial_arena_recovered_mail", Guard);
        if (Guard()) await DeliverAsync(context, snapshot, activeCharacters: false);
    }

    public async Task<bool> InitializeAsync(GameConnectionContext context, TownCharacterProjection primary)
    {
        if (context.Session.ChannelEntry?.Type != 39) return true;
        // 仅在城镇初始化恢复已结束/断线实例，不在挑战进行中提前结算。
        var mails = Store.CompleteImperialArenaRuns(context.Owner);
        if (context.Session.ReadImperialArena(Store, Now) is not { } snapshot) return true;
        // 首次空编队在发 1393 前提交：客户端没有 runtimeActor(+4D8) 时，队伍设置、头像与入场按钮全部失效。
        if (context.Session.EnsureImperialArenaTeam(Store, Now) is { } seeded && context.Session.CanPublishImperialArena(seeded))
        {
            log.Write("imperial_arena_team_seeded",
                new { Connection = context.Id, First = seeded.State.Configuration.First, Second = seeded.State.Configuration.Second });
            snapshot = seeded;
        }
        if (!mails.IsEmpty)
            await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "imperial_arena_recovered_mail",
                () => context.Session.CanPublishImperialArena(snapshot));
        return await DeliverAsync(context, snapshot, primary);
    }

    public async Task<bool> DeliverAsync(GameConnectionContext context, Game.ImperialArena.ImperialArenaProjection snapshot,
        TownCharacterProjection? primary = null, bool activeCharacters = true)
    {
        bool Guard() => context.Session.CanPublishImperialArena(snapshot);
        await context.Notify(NotiPacketType.TAG_TOURNAMENT_ENTERABLE, ImperialArenaPackets.Enterable(snapshot.State), "imperial_arena_enterable", Guard);
        var route = Store.ReadImperialArenaRoute(context.Owner, Catalog, snapshot.State.Period);
        await context.Notify(NotiPacketType.TAG_TOURNAMENT_MONSTER_INFO, ImperialArenaPackets.Opponents(route), "imperial_arena_opponents", Guard);
        await context.Notify(NotiPacketType.TAG_TOURNAMENT_HARD_MODE_INFO, ImperialArenaPackets.Challenge(ReadChallenge(context)), "imperial_arena_challenge", Guard);
        if (!activeCharacters || snapshot.State.Configuration.IsEmpty) return Guard();
        primary ??= context.Owner.Execute(() => context.Session.Selected is { } selected ? new TownCharacterProjection(selected, Now) : null);
        if (primary is null) return false;
        var (installed, metadataSent) = await InstallPairAsync(context, snapshot, primary);
        if (installed) return true;
        if (!Guard()) return false;
        log.Write("imperial_arena_pair_unavailable", new { Connection = context.Id, MetadataSent = metadataSent });
        await context.Notify(NotiPacketType.AAS_TIPS, ServerNoticePackets.Build("帝国竞技场",
            "双角色资料需要更新。请先分别登录两名90级角色，再以编队第一名登录。"), "imperial_arena_pair_notice", Guard);
        if (metadataSent) { await town.ReturnToSelectionAsync(context, Guard); return false; }
        return Guard();
    }

    // 购票与进图前就地重建一次双Actor：主角修理、金币或体力恢复让已装资料失效时不再要求重登。
    // 只发 1393→1394，不推提示、不触发小退，失败时保持原状交给购票/入场准入拒绝。
    public async Task<bool> RefreshPairAsync(GameConnectionContext context)
    {
        if (context.Session.ReadImperialArena(Store, Now) is not { } snapshot) return false;
        if (snapshot.State.Configuration.IsEmpty) return false;
        if (context.Session.HasCurrentImperialArenaPair(Store, Now)) return true;
        var primary = context.Owner.Execute(() => context.Session.Selected is { } selected ? new TownCharacterProjection(selected, Now) : null);
        if (primary is null) return false;
        var (installed, _) = await InstallPairAsync(context, snapshot, primary);
        if (!installed) log.Write("imperial_arena_pair_refresh_failed", new { Connection = context.Id });
        return installed;
    }

    // 1393→1394 的双Actor安装循环。只负责安装与回执，提示与小退由调用方按场景决定。
    async Task<(bool Installed, bool MetadataSent)> InstallPairAsync(GameConnectionContext context,
        Game.ImperialArena.ImperialArenaProjection snapshot, TownCharacterProjection? primary)
    {
        bool Guard() => context.Session.CanPublishImperialArena(snapshot);
        if (primary is null) return (false, false);
        var appearance = TagEvildomAppearance.Capture(primary.Character, primary.AppearanceTime);
        bool metadataSent = false;
        for (int attempt = 0; attempt < 2 && Guard(); attempt++)
        {
            var pair = context.Session.CaptureImperialArenaCharacters(snapshot);
            var now = Now;
            if (pair is null || !ReferenceEquals(pair.Primary, primary.Character) || !appearance.Matches(pair.Primary, now) ||
                pair.Standby.Inventory.AccountWallet.Revision != appearance.WalletRevision ||
                !TagEvildomInventory.IsCurrent(pair.Primary.Inventory, items, now.ToUnixTimeSeconds()) ||
                !TagEvildomInventory.IsCurrent(pair.Standby.Inventory, items, now.ToUnixTimeSeconds()) ||
                !TagEvildomLearning.IsCurrent(pair.Primary, learning, now.ToUnixTimeSeconds()) ||
                !TagEvildomLearning.IsCurrent(pair.Standby, learning, now.ToUnixTimeSeconds())) break;
            var standby = TagEvildomAppearance.Capture(pair.Standby, now);
            bool PairGuard() => appearance.Matches(pair.Primary, Now) && standby.Matches(pair.Standby, Now) &&
                context.Session.CanPublishImperialArenaCharacters(Store, pair);
            var body = TagEvildomPackets.CharacterInfo(snapshot.CharacterId, pair.Standby, stats.Compute(pair.Standby), now);
            await context.Notify(NotiPacketType.TAG_TOURNAMENT_TEAM_INFO, ImperialArenaPackets.ActiveTeam(snapshot), "imperial_arena_active_team",
                () => { bool accepted = PairGuard(); metadataSent |= accepted; return accepted; });
            bool sent = false;
            await context.Notify(NotiPacketType.TAG_TOURNAMENT_CHARACTER_INFO, body, "imperial_arena_character_info", () => sent = PairGuard());
            if (sent && context.Session.MarkImperialArenaPairPublished(Store, pair, appearance, standby, Now)) return (true, metadataSent);
        }
        return (false, metadataSent);
    }
}
