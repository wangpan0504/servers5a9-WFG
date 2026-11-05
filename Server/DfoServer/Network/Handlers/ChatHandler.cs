using DfoServer.Game.Chat;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

/// <summary>
/// 聊天发送 <c>CMD 0x0011 SEND_MESSAGE</c> 的正式处理（普通/公会/悄悄话 + 服务器/频道喇叭），
/// 以及物品链接 <c>CMD 0x01A8 ITEM_HYPERLINK_MESSAGE</c> 的转发。
/// <para>
/// 协议事实（反汇编 + 实机双向确认）：聊天行 <c>NOTI 0x000C</c> 的正文是
/// <c>[u8 显示 type][u16 层 id][u8 v][u32 CP936 字节数][CP936 文本]</c>，客户端按长度读文本
/// （<c>0 &lt; len &lt; 0x100</c>，否则显示「信息长度有误」）。显示 type 不是入站 kind 的原值：
/// 队伍 = <c>2</c>、普通/悄悄话 = <c>3</c>、公会 = <c>6</c>，见 <see cref="ChatWire"/>。
/// 两种喇叭走已实测可见的 <c>NOTI 0x0076</c> 喇叭页签
/// （发言人、正文及来源身份，服务器喇叭type11、频道喇叭type13），
/// 由 <see cref="MegaphonePackets"/> 构造、<c>ChatProjection</c> 投递。
/// 物品链接的正文是客户端已格式化的 CP936 链接文本（<c>0x7F 0x7F [物品名] 0x7F</c>）与
/// <c>cnt × 148</c> 字节物品记录；文本与记录都在 <see cref="ChatNotice"/> 里走到投递侧，
/// 由 <see cref="ItemLinkPackets"/> 按370/371/372组帧，记录缺失只退化原通道文字，
/// 见 <see cref="ItemHyperlinkAsync"/>。
/// 字节合同见 <c>Docs/协议/聊天.md</c>，投递资格见 <c>Docs/模块/聊天.md</c>。
/// </para>
/// <para>
/// 本服普通频道精确全文“#战力”与明确的客户端机器命令是自助评分入口；其余合法 <c>0x0011</c> 走正式投递
/// （解析 → 每连接节流 → 静音/封禁预留钩子 → CP936 截断到 200 字节 → 喇叭先扣物品 → 投递计划 → 记录实际投递结果）。
/// GM <c>chat-inject</c> 是独立的诊断入口，只向指定在线连接注入测试帧，不经过这里。
/// </para>
/// </summary>
internal sealed class ChatHandler(ChatService chat, InventoryProjection? inventory, ServerLog log,
    NamedWhisperService? named = null, GuildContributionProjection? guildContribution = null,
    SelfCharacterScoreProjection? selfScore = null, DfoServer.GameWorld.Characters.ChatEmoticonCatalog? emoticons = null,
    PromotionPageProjection? promotionPage = null,
    DfoServer.GameWorld.Characters.SkillLearningCatalog? learning = null,
    Func<GameSession, bool>? isChatBlocked = null) : IGameCommandModule
{
#if DEBUG
    internal Func<GameConnectionContext, Task>? DebugDungeonKill { get; init; }
#endif

    // 日志里的文本上限：单条聊天不能把热点日志撑大。
    const int LogTextLimit = 64;

    // 物品链接摘要只取前 32 字节 hex：既能看出 0x7F 标记/记录开头，又不把整包复制进热点日志。
    const int LogHexLimit = 32;

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.SEND_MESSAGE, SendAsync, nameof(ChatHandler), UserStateRequirement.CharacterSelected);
        commands.Register(CmdPacketType.ITEM_HYPERLINK_MESSAGE, ItemHyperlinkAsync, nameof(ChatHandler), UserStateRequirement.CharacterSelected);
    }

    async Task<CommandResult> SendAsync(GameConnectionContext context, Packet packet)
    {
        // 助手机器命名空间在解码/具名私聊/正文日志之前隔离，畸形或跨频道请求也不广播。
        if (HelperRequests.Reserved(packet.Body))
        {
            if (!context.Session.AllowChatSend() || (isChatBlocked ?? IsChatBlocked)(context.Session)) return CommandResult.Handled;
            var helper = HelperRequests.Parse(packet.Body);
            if (helper is null) return CommandResult.Handled;
            if (helper.DirectPickup is { } pickupEnabled)
            {
                var pickup = context.Session.SetHelperDirectPickup(helper.CharacterId, pickupEnabled);
                if (pickup is null) return CommandResult.Handled;
                string pickupText = string.Join('|', "1", helper.Nonce, pickup.CharacterId, pickup.Epoch,
                    pickup.Accepted ? "ok" : "rejected", pickup.Enabled ? "1" : "0");
                var pickupBody = ServerNoticePackets.Build("95_HELPER_PICKUP/1", pickupText, out bool pickupTruncated);
                if (pickupTruncated) throw new InvalidDataException("助手直入包回执不得截断");
                await context.Notify(NotiPacketType.AAS_TIPS, pickupBody, "helper_direct_pickup",
                    () => context.Session.CanPublishHelperPickup(pickup));
                log.Write("helper_settings", new { Connection = context.Id, pickup.Accepted, DirectPickup = pickup.Enabled });
                return CommandResult.Handled;
            }
            if (helper.Safety)
            {
                var safety = context.Session.ReadHelperSafety(helper.CharacterId, learning);
                if (safety is null) return CommandResult.Handled;
                bool CanPublish() => context.Session.CanPublishHelperSafety(safety);
                // 同一快照的完整背包/账号仓库先于资格回执投递，避免换频道沿用旧仓库格。
                await context.Notify(NotiPacketType.ITEM_LIST, InventoryPackets.Full(safety.Character.Inventory), "helper_safety_inventory", CanPublish);
                await InventoryProjection.RestoreLocksAsync(context, safety.Character.Inventory,
                    safety.Character.Inventory.Items.Where(item => item.Position.List == InventoryListType.Main).Select(item => item.Position), canSend: CanPublish);
                await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(safety.Character.Inventory, InventoryListType.AccountCargo), "helper_safety_cargo", CanPublish);
                await InventoryProjection.RestoreLocksAsync(context, safety.Character.Inventory,
                    safety.Character.Inventory.Items.Where(item => item.Position.List == InventoryListType.AccountCargo).Select(item => item.Position), canSend: CanPublish);
                var entries = safety.Candidates.Select(row => helper.SafetyVersion == 2
                    ? HelperSafetyPackets.EntryWithWearability(row) : HelperSafetyPackets.Entry(row)).ToArray();
                int total = Math.Max(1, (entries.Length + 11) / 12);
                for (int index = 0; index < total; index++)
                {
                    string safetyText = string.Join('|', "1", helper.Nonce, safety.Character.Id, safety.Epoch, index, total,
                        string.Join(',', entries.Skip(index * 12).Take(12)));
                    var response = ServerNoticePackets.Build(helper.SafetyVersion == 2 ? "95_HELPER_SAFE/2" : "95_HELPER_SAFE/1",
                        safetyText, out bool safetyTruncated);
                    if (safetyTruncated) throw new InvalidDataException("助手资格回执不得截断");
                    await context.Notify(NotiPacketType.AAS_TIPS, response, "helper_safety", CanPublish);
                }
                return CommandResult.Handled;
            }
            if (helper.Cargo != 0)
            {
                var cargo = context.Session.SetHelperPortableCargo(helper.CharacterId,
                    helper.Cargo == 1 ? InventoryListType.PersonalCargo : InventoryListType.AccountCargo);
                if (cargo is null) return CommandResult.Handled;
                bool CanPublish() => context.Session.CanPublishHelperCargo(cargo);
                if (cargo.Accepted)
                {
                    // 完整清单携真实容量，在成功回执和客户端开窗前先建立原生仓库快照。
                    await context.Notify(NotiPacketType.ITEM_LIST, CargoPackets.Full(cargo.Character.Inventory, cargo.List),
                        "helper_cargo_list", CanPublish);
                    if (cargo.List == InventoryListType.PersonalCargo)
                        await context.Notify(NotiPacketType.CERA_SPECIALITEM, CargoPackets.PersonalCapacity(cargo.Character.Inventory.CargoCapacity),
                            "helper_cargo_capacity", CanPublish);
                    await InventoryProjection.RestoreLocksAsync(context, cargo.Character.Inventory,
                        cargo.Character.Inventory.Items.Where(item => item.Position.List == cargo.List).Select(item => item.Position), canSend: CanPublish);
                }
                string cargoText = string.Join('|', "1", helper.Nonce,
                    cargo.Character.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    cargo.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), cargo.Accepted ? "ok" : "rejected",
                    helper.Cargo.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var cargoBody = ServerNoticePackets.Build("95_HELPER_CARGO/1", cargoText, out bool cargoTruncated);
                if (cargoTruncated) throw new InvalidDataException("助手仓库回执不得截断");
                await context.Notify(NotiPacketType.AAS_TIPS, cargoBody, "helper_cargo", CanPublish);
                log.Write("helper_cargo", new { Connection = context.Id, cargo.Accepted, List = (byte)cargo.List });
                return CommandResult.Handled;
            }
            var result = context.Session.SetHelperHellRotation(helper.CharacterId, helper.HellRotation);
            if (result is null) return CommandResult.Handled;
            string helperText = string.Join('|', "1", helper.Nonce,
                result.CharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Accepted ? "ok" : "rejected",
                result.HellRotation ? "1" : "0");
            var body = ServerNoticePackets.Build("95_HELPER/1", helperText, out bool helperTruncated);
            if (helperTruncated) throw new InvalidDataException("助手状态回执不得截断");
            await context.Notify(NotiPacketType.AAS_TIPS, body, "helper_settings",
                () => context.Session.CanPublishHelperSettings(result));
            log.Write("helper_settings", new { Connection = context.Id, result.Accepted, result.HellRotation });
            return CommandResult.Handled;
        }
        // 预留scene机器包在宽松解码、具名私聊、正文日志和任何广播之前隔离。
        // 即使未安装/未启用scene模块也不能把机器回执投递给玩家；不响应、不协商、不执行领域操作。
        if (LukeSceneReservedRequests.TryRead(packet.Body, out var sceneStatus, out _))
        {
            if (context.Session.AllowChatSend())
                log.Write("luke_scene_reserved", new { Connection = context.Id, Status = sceneStatus.ToString() });
            return CommandResult.Handled;
        }

        if (!ChatRequests.TryParse(packet.Body, out var send))
            return CommandResult.Unhandled;

        // GM 聊天帧注入需要与客户端当前层一致；移动包没有层 id 字段，因此只记聊天上报值。
        context.Session.RememberChatLayer(send.Layer);

        // 超限只丢弃并记 chat_throttled：聊天是被动输入，不能成为断线触发器。
        if (!context.Session.AllowChatSend())
        {
            log.Write("chat_throttled", new { Connection = context.Id, send.Type });
            return CommandResult.Handled;
        }

        // 静音/封禁预留钩子：玩家管理模块尚未接入，默认恒不拦截；接入后在这里读取真实状态并记 chat_blocked（覆盖具名私聊与常规广播）。
        if ((isChatBlocked ?? IsChatBlocked)(context.Session))
        {
            log.Write("chat_blocked", new { Connection = context.Id, send.Type });
            return CommandResult.Handled;
        }

        if (send.TargetName is not null && named is not null)
        {
            if (emoticons is not null && send.Text.Contains('\x1e'))
                send = send with { Text = context.Session.PrepareChatEmoticons(emoticons, send.Text) };
            var result = named.Send(context.Session, send);
            log.Write(result.Accepted ? "chat_delivered" : "chat_rejected", new
            {
                Connection = context.Id, Kind = (ChatRequestType)send.Type is ChatRequestType.OneToOne or ChatRequestType.OneToOneReply ? "OneToOne" : "Whisper",
                result.Recipients, Reason = result.TargetOffline ? "target-offline" : result.Rejection
            });
            return CommandResult.Handled;
        }

        // 页面机器请求沿用限流和封禁；能力关闭仍吞掉其保留空间，不广播或泄漏机器文本。
#if DEBUG
        if (DebugDungeonKill is not null && DungeonHandler.IsDebugKillCommand(send))
        {
            await DebugDungeonKill(context);
            return CommandResult.Handled;
        }
#endif

        if (PromotionPageProjection.MachineNamespace(send))
        {
            string? nonce = PromotionPageProjection.MachineNonce(send);
            string status = nonce is null ? "invalid_request" : promotionPage?.Query(context, nonce) ?? "unavailable";
            log.Write("cash_shop_promotion_page_query", new { Connection = context.Id, Status = status });
            return CommandResult.Handled;
        }

        // 限流和封禁后只识别本服明确的普通频道评分命令，不trim、不接收目标或任意GM参数。
        if (SelfCharacterScoreProjection.MachineNamespace(send))
        {
            string? nonce = SelfCharacterScoreProjection.MachineNonce(send);
            string status = nonce is null ? "invalid_request" : selfScore?.Query(context, nonce, SelfCharacterScoreProjection.MachineVersion(send)) ?? "unavailable";
            log.Write("character_score_machine_query", new { Connection = context.Id, Version = SelfCharacterScoreProjection.MachineVersion(send), Status = status });
            return CommandResult.Handled;
        }

        if (SelfCharacterScoreProjection.Matches(send))
        {
            string status = selfScore?.Query(context) ?? "unavailable";
            log.Write("character_score_query", new { Connection = context.Id, Status = status });
            return CommandResult.Handled;
        }

        // 其余普通/公会/悄悄话与喇叭完全保留原投递与扣减路径。
        if (emoticons is not null && send.Text.Contains('\x1e'))
            send = send with { Text = context.Session.PrepareChatEmoticons(emoticons, send.Text) };
        string text = ChatText.Truncate(send.Text, out bool truncated);
        bool known = ChatService.TryKind(send.Type, out var kind);
        uint target = kind is ChatKind.Whisper or ChatKind.ServerMegaphone or ChatKind.ChannelMegaphone ? send.Arg : 0;
        log.Write("chat_send", new
        {
            Connection = context.Id, send.Type, Target = target, send.Layer,
            TextLength = ChatText.ByteCount(text), Text = Clip(text), Truncated = truncated
        });
        if (!known)
        {
            log.Write("chat_rejected", new { Connection = context.Id, Reason = "unsupported-type", send.Type });
            return CommandResult.Handled;
        }

        var origin = context.Session.ChatOrigin();
        if (origin is null)
        {
            log.Write("chat_rejected", new { Connection = context.Id, Reason = "no-character", send.Type });
            return CommandResult.Handled;
        }

        if (kind == ChatKind.Guild) origin = origin with { AdventureName = context.Session.ReadAdventureName() };

        // 两种喇叭：先校验槽位/道具并走既有背包提交路径扣 1 个；扣减失败不广播，避免"没扣钱也喊话"。
        if (!TryConsumeMegaphone(context, send, kind, out var deduction)) return CommandResult.Handled;

        var dispatch = chat.Broadcast(send, origin, context.Session);
        if (dispatch.TargetOffline)
            log.Write("chat_whisper_offline", new { Connection = context.Id, Target = send.Arg });
        else if (dispatch.Rejection is { } reason)
            log.Write("chat_rejected", new { Connection = context.Id, Reason = reason, send.Type });
        else
        {
            log.Write("chat_delivered", new
            {
                Connection = context.Id, Kind = kind.ToString(), dispatch.Recipients, dispatch.LayerFallback
            });
            if (kind == ChatKind.Guild && guildContribution is not null) await guildContribution.ChatAsync(context);
            // 喇叭：记种类、类型字节与编码后的段落长度，便于定位"客户端的喇叭页签为什么没这一行"；
            // 不写正文（正文可能很长，且热点日志不应复制玩家文本）。
            if (kind is ChatKind.ServerMegaphone or ChatKind.ChannelMegaphone)
            {
                var megaphone = MegaphonePackets.Measure(kind, origin.Name, send.Text);
                log.Write("chat_megaphone", new
                {
                    Connection = context.Id,
                    Variant = kind == ChatKind.ServerMegaphone ? "server" : "channel",
                    SourceServer = origin.Scene.Channel.ServerIndex, SourceChannel = origin.ChannelNumber, Actor = origin.CharacterId,
                    megaphone.Type, megaphone.SpeakerBytes, megaphone.TextBytes, dispatch.Recipients
                });
            }
        }

        // 喇叭扣减已提交：把实际库存差量发布给本人连接（增量按既有背包投影编码）。
        if (deduction is { Accepted: true } && inventory is not null)
            await inventory.RefreshAsync(context, deduction);
        return CommandResult.Handled;
    }

    /// <summary>
    /// 物品引用沿用文字的身份、路由和喇叭扣费；完整解析头部、具名目标及148字节记录。
    /// 普通/队伍使用370，具名私聊/公会使用371，喇叭使用372；记录不足时只退化原通道文字。
    /// 畸形请求不消费、不断线；实际文本和记录随同一通知投递，最近样本仅用于诊断。
    /// </summary>
    async Task<CommandResult> ItemHyperlinkAsync(GameConnectionContext context, Packet packet)
    {
        if (!ChatRequests.TryParseHyperlink(packet.Body, out var link, out var error))
        {
            log.Write("item_hyperlink_rejected", new
            {
                Connection = context.Id, Reason = error.ToString(), Length = packet.Body.Length,
                Head = HexHead(packet.Body)
            });
            return CommandResult.Handled;
        }

        // 与普通聊天共用每连接节流：物品链接同样是会向同场景/公会广播的聊天输入，不能绕过限速。
        if (!context.Session.AllowChatSend())
        {
            log.Write("chat_throttled", new { Connection = context.Id, link.Kind, Opcode = (int)CmdPacketType.ITEM_HYPERLINK_MESSAGE });
            return CommandResult.Handled;
        }

        if ((isChatBlocked ?? IsChatBlocked)(context.Session))
        {
            log.Write("chat_blocked", new { Connection = context.Id, link.Kind });
            return CommandResult.Handled;
        }

        var namedType = NamedWhisperService.ResponseType(link.Kind);
        bool isNamed = link.Request.TargetName is not null;
        ChatKind kind = ChatKind.Whisper;
        if (isNamed ? named is null || namedType is null : !ChatService.TryKind(link.Kind, out kind))
        {
            log.Write("item_hyperlink_rejected", new { Connection = context.Id, Reason = "unsupported-type", link.Kind });
            return CommandResult.Handled;
        }

        var origin = context.Session.ChatOrigin();
        if (origin is null)
        {
            log.Write("chat_rejected", new { Connection = context.Id, Reason = "no-character", link.Kind });
            return CommandResult.Handled;
        }

        if (kind == ChatKind.Guild) origin = origin with { AdventureName = context.Session.ReadAdventureName() };
        if (emoticons is not null && link.Text.Contains((byte)0x1e))
            link = link with { Text = context.Session.PrepareChatEmoticonBytes(emoticons, link.Text) };
        if (!TryConsumeMegaphone(context, link.Request, kind, out var deduction)) return CommandResult.Handled;
        var dispatch = isNamed
            ? named!.Send(context.Session, link.Request, link.Text, link.Records)
            : chat.Broadcast(link.Request, origin, context.Session, link.Text, link.Records);
        if (!dispatch.Accepted)
        {
            log.Write("item_hyperlink_rejected", new
            {
                Connection = context.Id, Reason = dispatch.TargetOffline ? "target-offline" : dispatch.Rejection,
                link.Kind, TextBytes = link.Text.Length,
                Records = link.Records.Length / ChatRequests.HyperlinkRecordLength
            });
        }
        else
        {
            context.Session.RememberItemHyperlink(link);
            if (kind == ChatKind.Guild && guildContribution is not null) await guildContribution.ChatAsync(context);
            var plan = isNamed || kind == ChatKind.Guild
                ? ItemLinkPackets.DesignNamed(new(namedType ?? ChatMessageType.Guild, 0, origin.Name,
                    origin.Scene.Channel.ServerIndex, ""), link.Text, link.Records)
                : kind is ChatKind.ServerMegaphone or ChatKind.ChannelMegaphone
                    ? ItemLinkPackets.DesignMegaphone(kind, new(origin.Name, "")
                        { Channel = origin.Scene.Channel, Actor = origin.CharacterId }, link.Text, link.Records)
                    : ItemLinkPackets.Design(kind, origin.CharacterId, origin.Name, link.Text, link.Records,
                        origin.Scene.Channel.ServerIndex);
            log.Write("item_hyperlink", new
            {
                Connection = context.Id, Kind = kind.ToString(), Opcode = (int)plan.Opcode,
                SourceServer = origin.Scene.Channel.ServerIndex, SourceChannel = origin.ChannelNumber,
                TextBytes = link.Text.Length, Records = link.Records.Length / ChatRequests.HyperlinkRecordLength,
                plan.MarkerPairs, plan.Degraded, plan.MarkersStripped, plan.Truncated,
                RecordHex = HexHead(link.Records), Head = HexHead(link.Text), dispatch.Recipients
            });
        }
        if (deduction is { Accepted: true } && inventory is not null)
            await inventory.RefreshAsync(context, deduction);
        return CommandResult.Handled;
    }

    bool TryConsumeMegaphone(GameConnectionContext context, ChatSend request, ChatKind kind, out InventoryResult? deduction)
    {
        deduction = null;
        if (kind is not (ChatKind.ServerMegaphone or ChatKind.ChannelMegaphone)) return true;
        int itemId = ChatService.MegaphoneItemId(kind);
        if (request.Arg > short.MaxValue)
        {
            log.Write("chat_megaphone_rejected", new { Connection = context.Id, Slot = request.Arg, Item = itemId, Reason = "slot-range" });
            return false;
        }
        deduction = context.Session.UseChatMegaphone((ushort)request.Arg, itemId);
        if (deduction.Accepted) return true;
        log.Write("chat_megaphone_rejected", new { Connection = context.Id, Slot = request.Arg, Item = itemId, Reason = "deduct-failed" });
        return false;
    }

    /// <summary>日志摘要：前 <see cref="LogHexLimit"/> 字节的十六进制；空数组返回 <c>"-"</c>。</summary>
    static string HexHead(byte[] bytes) =>
        bytes.Length == 0 ? "-" : Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, LogHexLimit)));

    /// <summary>
    /// 静音/封禁预留：玩家管理、举报与屏蔽列表尚未实现，这里恒不拦截。
    /// 接入时读取真实状态（而不是在聊天 handler 里另建一套状态），并保持"拒绝也要记录原因"的约定。
    /// </summary>
    static bool IsChatBlocked(GameSession session) => false;

    static string Clip(string text) => text.Length <= LogTextLimit ? text : text[..LogTextLimit] + "...";
}
