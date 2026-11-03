using DfoServer.Game.Chat;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 每连接一份，随会话创建/销毁；延迟创建才能拿到主构造函数注入的 TimeProvider（测试用假时钟）。
    ChatRateLimiter? chatRate;

    public string PrepareChatEmoticons(DfoServer.GameWorld.Characters.ChatEmoticonCatalog catalog, string text) => owner.Execute(() =>
    {
        HashSet<ushort> allowed = Selected is { } character
            ? catalog.Eligible(ChatEmoticonRules.Eligibility(character, (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()))
                .Select(row => row.Id).ToHashSet() : [];
        return ChatEmoticonRules.Prepare(text, allowed, ChatText.MaxBytes, out _);
    });

    public byte[] PrepareChatEmoticonBytes(DfoServer.GameWorld.Characters.ChatEmoticonCatalog catalog, byte[] text) => owner.Execute(() =>
    {
        HashSet<ushort> allowed = Selected is { } character
            ? catalog.Eligible(ChatEmoticonRules.Eligibility(character, (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds()))
                .Select(row => row.Id).ToHashSet() : [];
        return ChatEmoticonRules.FilterBytes(text, allowed);
    });

    // Runtime 绑定本连接的聊天投递入口（与 GuildPermitNoticeArrived 同一形态）：目录只保存这一份 Session。
    public Action<ChatNotice>? ChatNoticeArrived { private get; set; }

    // 最近一次 CMD 0x0011 上报的层 id；-1 表示本连接还没发过聊天。只作 GM 诊断提示，正式投递不读。
    int lastChatLayer = -1;

    /// <summary>记录该连接最近一次聊天上报的层 id；移动包在当前协议里没有层 id 字段，不作为来源。</summary>
    public void RememberChatLayer(ushort layer) => Volatile.Write(ref lastChatLayer, layer);

    /// <summary>该连接最近一次聊天上报的层 id；<c>null</c> 表示尚无可用的已知值。</summary>
    public ushort? LastChatLayer
    {
        get
        {
            int value = Volatile.Read(ref lastChatLayer);
            return value < 0 ? null : (ushort)value;
        }
    }

    /// <summary>本连接是否还能发送聊天：每连接 5 条/秒、30 条/10 秒，超限只丢弃，不断线。</summary>
    public bool AllowChatSend() => (chatRate ??= new(timeProvider ?? TimeProvider.System)).Allow();

    // 最近一次 CMD 0x01A8 的完整解析结果（链接文本 + 物品记录）。只保留最新一条，随会话销毁；
    // 正式投递以 ChatNotice.ItemRecords 里携带的那一份为准（避免并发/连续两条链接时读到后一条的记录），
    // 这里保留的是诊断/人工复核用的最近样本。
    ChatHyperlink? lastItemHyperlink;

    /// <summary>记录本连接最近一次物品链接的文本与记录字节；投递时另走 <see cref="ChatNotice.ItemRecords"/>。</summary>
    public void RememberItemHyperlink(ChatHyperlink link) => Volatile.Write(ref lastItemHyperlink, link);

    /// <summary>本连接最近一次物品链接；<c>null</c> 表示还没发过（或解析失败被丢弃）。</summary>
    public ChatHyperlink? LastItemHyperlink => Volatile.Read(ref lastItemHyperlink);

    /// <summary>
    /// 发送者快照：角色、角色名、公会与当前场景。锁内取值，供聊天服务在锁外做投递选择；
    /// 角色名就是喇叭的发言人（<c>NOTI 0x0076</c> 的 <c>str1</c>），场景键与 <c>TownPresence.SceneOf</c> 同一口径
    /// （频道 + town/area 的 PVF 地图 + 房间账号），副本/选图/结算阶段再带本角色的副本实例身份，
    /// 避免同坐标但不同实例的角色互相收到普通聊天。
    /// </summary>
    public ChatOrigin? ChatOrigin(bool includeAdventureName = false) => owner.Execute(() =>
    {
        if (Selected is not { } character) return null;
        long epoch = SelectionEpoch;
        var channel = ChannelEntry?.Identity ?? default;
        // 文字消息只依赖频道及角色身份，不把加载、结算或场景切换当作禁言条件。
        bool Current() => owner.IsCurrent && SelectionEpoch == epoch && Selected?.Id == character.Id &&
            (ChannelEntry?.Identity ?? default) == channel;
        return new ChatOrigin(character.Id, character.Name, character.Guild is { } guild ? (long)guild.Id : null,
            ChatSceneOf(character)) { AdventureName = includeAdventureName ? ReadAdventureName() : "", IsCurrent = Current };
    });

    ChatScene ChatSceneOf(Character character)
    {
        var town = towns.Scene(character.Position, completed);
        Guid instance = Phase is SessionPhase.Loading or SessionPhase.Dungeon or SessionPhase.Settlement
            ? formalDungeon?.Run.Scene.Identity.InstanceId ?? Guid.Empty
            : Guid.Empty;
        return new(ChannelEntry?.Identity ?? default, town.Town, town.Area, town.Map, town.OwnerKey(owner.Account,character.Id,
            character.Guild?.Id ?? 0,
            character.Marriage is { State: 4 } marriage && marriage.IsMember(character.Id) ? marriage.Id : null,
            character.WeddingHall?.CeremonyId), instance);
    }

    /// <summary>
    /// 投递一条聊天通知：锁内只复核"仍是当前在线角色"与 <paramref name="filter"/> 并取回调，回调在锁外调用。
    /// 这样接收账号锁内不会重入发送/日志，也不会为了投递去取另一账号锁（AccountOwners 直接拒绝跨账号嵌套）。
    /// <paramref name="filter"/> 因此在目标账号锁内执行，只能比较快照字段。
    /// </summary>
    public bool TryDeliverChat(Func<ChatRecipient, bool> filter, ChatNotice notice)
    {
        Action<ChatNotice>? delivery;
        ChatNotice outgoing = notice;
        try
        {
            delivery = owner.Execute(() =>
            {
                if (!Ready || Selected is not { } character)
                    return null;
                if (notice.Megaphone is { } megaphone)
                {
                    long epoch = SelectionEpoch;
                    ushort id = character.Id;
                    outgoing = notice with { Guard = () => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == id &&
                        (ChannelEntry?.Identity ?? default).ServerIndex == megaphone.Channel.ServerIndex &&
                        (notice.Kind != ChatKind.ChannelMegaphone || (ChannelEntry?.Identity ?? default) == megaphone.Channel) &&
                        (notice.Guard?.Invoke() ?? true) };
                }
                else
                {
                    long epoch = SelectionEpoch;
                    ushort id = character.Id;
                    bool ScopeCurrent()
                    {
                        if (notice.Origin is not { } origin || Selected is not { } current) return notice.Origin is null;
                        return notice.Kind switch
                        {
                            ChatKind.Normal => ChatSceneOf(current) == origin.Scene,
                            ChatKind.Guild => current.Guild?.Id == origin.GuildId &&
                                (ChannelEntry?.Identity ?? default).ServerIndex == origin.Scene.Channel.ServerIndex,
                            ChatKind.Whisper => (ChannelEntry?.Identity ?? default).ServerIndex == origin.Scene.Channel.ServerIndex,
                            _ => true
                        };
                    }
                    outgoing = notice with { Guard = () => owner.IsCurrent && Ready && SelectionEpoch == epoch && Selected?.Id == id &&
                        ScopeCurrent() && (notice.Guard?.Invoke() ?? true) };
                }
                return filter(new ChatRecipient(owner.Account, character.Id,
                    character.Guild is { } guild ? (long)guild.Id : null, ChatSceneOf(character))) ? ChatNoticeArrived : null;
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent)
        {
            return false; // 刚被接管/替换的原连接不接收
        }

        if (delivery is null)
            return false;
        delivery(outgoing);
        return true;
    }

    /// <summary>
    /// 喇叭扣费：从主背包指定槽位扣 1 个，并校验该槽物品就是对应喇叭（36 服务器 / 37 频道）。
    /// 扣减走既有 <see cref="PlanAndCommitInventory"/> 提交路径（<see cref="InventoryDeleteService"/> 规划 + 同一写事务），
    /// 地点由公共使用门禁读取源STK，不按当前资源的城镇声明另写阶段限制。
    /// 槽位空、物品不符、库存不足、阶段不符或提交失败都返回拒绝，调用方据此不广播。
    /// </summary>
    public InventoryResult UseChatMegaphone(ushort slot, int expectedItemId) => slot > short.MaxValue ? InventoryResult.Reject() :
        PlanAndCommitInventory(character =>
    {
        var position = new InventorySlot(InventoryListType.Main, (short)slot);
        if (character.Inventory.Find(position) is not { } item || item.Core.ItemId != expectedItemId)
            return InventoryResult.Reject();
        return InventoryDeleteService.Plan(character.Inventory, position, expectedItemId, 1);
    }, requireTown: false, effectItem: new(InventoryListType.Main, (short)slot));
}
