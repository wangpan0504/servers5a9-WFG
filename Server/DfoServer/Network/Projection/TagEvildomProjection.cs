using DfoServer.Game.TagEvildom;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class TagEvildomProjection(ITagEvildomConfigurationStore store,
    DfoServer.Game.Characters.CharacterStatComputer stats, DfoServer.Infrastructure.ServerLog log, TimeProvider? clock = null,
    DfoServer.GameWorld.Characters.SkillLearningCatalog? learning = null, TownProjection? town = null,
    DfoServer.GameWorld.Items.ItemCatalog? items = null)
{
    public ITagEvildomConfigurationStore Store { get; } = store;
    public DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public bool CharactersCurrent(TagEvildomCharacters characters) =>
        TagEvildomInventory.IsCurrent(characters.Primary.Inventory, items, Now.ToUnixTimeSeconds()) &&
        TagEvildomInventory.IsCurrent(characters.Standby.Inventory, items, Now.ToUnixTimeSeconds()) && (learning is null ||
        TagEvildomLearning.IsCurrent(characters.Primary, learning, Now.ToUnixTimeSeconds()) &&
        TagEvildomLearning.IsCurrent(characters.Standby, learning, Now.ToUnixTimeSeconds()));

    public async Task<bool> InitializeAsync(GameConnectionContext context, TownCharacterProjection primarySource)
    {
        // 共享TAG包依赖真实再战频道；普通频道、帝国竞技场不能冒充type53。
        if (context.Session.ChannelEntry?.Type != 53 || context.Session.RestoreTagEvildomTeamSelection(Store, Now) is not { } snapshot) return true;
        // 首次空编队在发 1677 前提交。已有队、不合格或备用技能未初始化时保持原快照。
        if (context.Session.EnsureTagEvildomTeam(Store, Now) is { } seeded &&
            context.Session.CanPublishTagEvildom(seeded)) snapshot = seeded;
        log.Write("tag_evildom_team_selection", new { Connection = context.Id, snapshot.CharacterId,
            snapshot.State.Configuration.SelectedTeam, snapshot.State.Configuration.Revision });
        return await DeliverAsync(context, snapshot, reportUnavailable: true, primarySource: primarySource);
    }

    public async Task<bool> DeliverAsync(GameConnectionContext context, Game.TagEvildom.TagEvildomProjection snapshot,
        bool reportUnavailable = false, bool activeCharacters = true, TownCharacterProjection? primarySource = null)
    {
        primarySource ??= context.Owner.Execute(() => context.Session.Selected is { } selected ? new TownCharacterProjection(selected, Now) : null);
        bool Guard() => context.Session.CanPublishTagEvildom(snapshot);
        await context.Notify(NotiPacketType.TAG_EVILDOM_TEAM_INFO, TagEvildomPackets.Teams(snapshot), "tag_evildom_teams", Guard);
        await context.Notify(NotiPacketType.EVILDOM_GATHERING, TagEvildomPackets.Gathering(snapshot), "tag_evildom_gathering", Guard);
        if (!activeCharacters || !Guard()) return Guard();
        var (installed, metadataSent, primaryChanged) = await InstallPairAsync(context, snapshot, primarySource);
        if (installed) return true;
        if (!Guard()) return false;
        if ((reportUnavailable || metadataSent || primaryChanged) && !snapshot.State.Configuration.Teams[snapshot.State.Configuration.SelectedTeam].Empty)
        {
            var team = snapshot.State.Configuration.Teams[snapshot.State.Configuration.SelectedTeam];
            bool primaryMismatch = team.First != snapshot.CharacterId;
            string? primaryName = snapshot.Roster.Entries.FirstOrDefault(row => row.Id == team.First)?.Name;
            log.Write("tag_evildom_character_info_unavailable", new { Connection = context.Id,
                Reason = primaryMismatch ? "selected-character-not-team-primary" : "primary-or-standby-not-ready" });
            await context.Notify(NotiPacketType.AAS_TIPS,
                ServerNoticePackets.Build("再战魔界", primaryMismatch
                    ? $"当前选中编队的第一名角色是{primaryName ?? "其他角色"}，请使用该角色出战。新增或修改编队请点击城镇的队伍设置，分别登录角色不会新增编队。"
                    : "当前编队已保留，但技能或穿戴期限资料需要更新。请分别登录当前编队的两名角色，再以第一名角色登录；新增或修改编队请使用队伍设置。"),
                "tag_evildom_character_info_notice", Guard);
        }
        if (metadataSent || primaryChanged)
        {
            // 只在仍属于原owner/epoch的入城投影恢复。配置ACK路径等待客户端CMD7，不在这里双重小退。
            if (town is null) throw new InvalidOperationException("TAG双角色投影恢复缺少真实小退服务");
            await town.ReturnToSelectionAsync(context, Guard);
            return false;
        }
        return Guard();
    }

    // 客户端进图前就地重建一次双Actor：已装的1393/1394因主角走动、修理、金币或体力恢复变化而判死时，
    // 不再要求玩家重登。只发 1393→1394，不推提示、不触发小退，失败时保持原状交给入场准入拒绝。
    public async Task<bool> RefreshPairAsync(GameConnectionContext context)
    {
        if (context.Session.ReadTagEvildom(Store, Now) is not { } snapshot) return false;
        if (snapshot.State.Configuration.Teams[snapshot.State.Configuration.SelectedTeam].Empty) return false;
        if (context.Session.HasCurrentTagEvildomPair(Store, Now)) return true;
        var (installed, _, _) = await InstallPairAsync(context, snapshot,
            context.Owner.Execute(() => context.Session.Selected is { } selected ? new TownCharacterProjection(selected, Now) : null));
        if (!installed) log.Write("tag_evildom_pair_refresh_failed", new { Connection = context.Id });
        return installed;
    }

    // 1393→1394 的双Actor安装循环。只负责安装与回执，提示与小退由调用方按场景决定。
    async Task<(bool Installed, bool MetadataSent, bool PrimaryChanged)> InstallPairAsync(GameConnectionContext context,
        Game.TagEvildom.TagEvildomProjection snapshot, TownCharacterProjection? primarySource)
    {
        bool Guard() => context.Session.CanPublishTagEvildom(snapshot);
        // 没有主角来源时无法判断可否安装；沿用入城旧口径，按「主角已变化」交给调用方恢复。
        if (primarySource is null) return (false, false, true);
        var primaryAppearance = TagEvildomAppearance.Capture(primarySource.Character, primarySource.AppearanceTime);
        bool metadataSent = false;
        // 1393会清旧active Actor；1394因真实状态变化被跳过时必须完整重捕获，不能只留下空HUD。
        for (int attempt = 0; attempt < 2 && Guard(); attempt++)
        {
            // 1394主角没有body；重新发送pair不能刷新客户端仍持有的普通主角Data。
            if (!primaryAppearance.Matches(primarySource.Character, Now) ||
                !context.Owner.Execute(() => ReferenceEquals(context.Session.Selected, primarySource.Character)))
                return (false, metadataSent, true);
            var characters = context.Session.CaptureTagEvildomCharacters(snapshot);
            var capturedTime = Now;
            if (characters is null) break;
            bool PrimaryCurrent(DateTimeOffset at) => ReferenceEquals(characters.Primary, primarySource.Character) &&
                primaryAppearance.Matches(primarySource.Character, at) &&
                // 钱包是双角色共享源。普通主角Data没有重发，不能把新钱包当已安装状态；保守要求原revision。
                characters.Standby.Inventory.AccountWallet.Revision == primaryAppearance.WalletRevision;
            if (!PrimaryCurrent(capturedTime)) return (false, metadataSent, true);
            if (!CharactersCurrent(characters) || !context.Session.CanPublishTagEvildomCharacters(Store, characters)) break;
            var standbyAppearance = TagEvildomAppearance.Capture(characters.Standby, capturedTime);
            bool CharactersGuard()
            {
                var currentTime = Now;
                return PrimaryCurrent(currentTime) && standbyAppearance.Matches(characters.Standby, currentTime) &&
                    context.Session.CanPublishTagEvildomCharacters(Store, characters);
            }
            // 属性虚弱、名称装饰与装备到期均绑定同一构包时点；不为普通相对秒倒计时每秒重新加载全资产。
            byte[] characterInfo = TagEvildomPackets.CharacterInfo(snapshot.CharacterId, characters.Standby, stats.Compute(characters.Standby), capturedTime,
                town?.KnightShieldDeck(characters.Standby));
            // 1393写player+1C的运行metadata，1677仅写player+8配置map，不能替代；1393必须在1394之前。
            await context.Notify(NotiPacketType.TAG_TOURNAMENT_TEAM_INFO,
                TagEvildomPackets.ActiveTeam(snapshot), "tag_evildom_active_team", () =>
                { bool allowed = CharactersGuard(); metadataSent |= allowed; return allowed; });
            bool informationSent = false;
            await context.Notify(NotiPacketType.TAG_TOURNAMENT_CHARACTER_INFO,
                characterInfo, "tag_evildom_character_info", () => informationSent = CharactersGuard());
            if (informationSent && context.Session.MarkTagEvildomPairPublished(Store, characters, primaryAppearance, standbyAppearance, Now))
                return (true, metadataSent, false);
            log.Write("tag_evildom_character_info_retry", new { Connection = context.Id, Attempt = attempt + 1, MetadataSent = metadataSent });
        }
        return (false, metadataSent, false);
    }
}
