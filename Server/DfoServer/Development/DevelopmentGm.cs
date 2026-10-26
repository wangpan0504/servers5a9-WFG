using DfoServer.Game.Session;
using DfoServer.Game.Accounts;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Characters;
using DfoServer.Game.Mailbox;
using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Settings;
using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    public const string Written = " 已写入。服务端通行校验立即使用新等级和任务。客户端回到选角并再次进入后刷新画面。属性块仍是匹配职业的初始值。";
    public static string Answer(string line, string defaultAccount, ICharacterRepository repository, AccountOwners owners, Func<string, GameSession?> find,
        MailboxService? mailbox = null, ItemCatalog? items = null, CharacterProgressReset? progress = null,
        InventoryCashShopService? cashShop = null, GameDebugSettings? debugSettings = null,
        Game.Events.ServerEventService? events = null, DevelopmentCatalog? catalog = null,
        Func<int, ChatProbeConnection?>? findConnection = null, ServerLog? log = null, GmNoticeSender? noticeSender = null)
    {
        try
        {
            return Execute(repository, owners, find, DevelopmentRequest.Parse(line, defaultAccount), mailbox, items, progress, cashShop, debugSettings, events, catalog, findConnection, log, noticeSender);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or SqliteException)
        {
            return "ERR " + ex.Message.Replace('\n', ' ').Replace('\r', ' ');
        }
    }

    public static string Execute(ICharacterRepository repository, AccountOwners owners, Func<string, GameSession?> find, DevelopmentRequest request,
        MailboxService? mailbox = null, ItemCatalog? items = null, CharacterProgressReset? progress = null,
        InventoryCashShopService? cashShop = null, GameDebugSettings? debugSettings = null,
        Game.Events.ServerEventService? events = null, DevelopmentCatalog? catalog = null,
        Func<int, ChatProbeConnection?>? findConnection = null, ServerLog? log = null, GmNoticeSender? noticeSender = null)
    {
        try
        {
            request.Validate();
            if (request.Action == DevelopmentAction.ChatInject)
                return InjectChatProbe(request, findConnection, log);
            if (request.Action == DevelopmentAction.InjectRaw)
                return InjectRaw(request, findConnection, log);
            if (request.Action == DevelopmentAction.Notice)
                return SendNotice(request, noticeSender, log);
            if (request.Action == DevelopmentAction.EquipSlots)
                return ExecuteEquipmentSlots(repository, owners, find(request.Account), request);
            if (request.Action == DevelopmentAction.Appearance)
                return DescribeAppearance(repository, owners, find(request.Account), request);
            if (request.Action >= DevelopmentAction.Wallet)
                return ExecuteExtended(repository, owners, find(request.Account), request, mailbox,
                    catalog ?? throw new InvalidOperationException("开发GM资源目录未装配"));
            if (request.Action == DevelopmentAction.Event)
            {
                var state = request.Event!.Execute(events ?? throw new InvalidOperationException("活动服务未装配"));
                return "OK " + JsonSerializer.Serialize(new
                {
                    eventId = state.Schedule.EventId, active = state.Active,
                    mode = state.Schedule.OverrideActive is { } active ? active ? "on" : "off" : "auto",
                    state.Schedule.Revision, state.Schedule.StartsAt, state.Schedule.EndsAt
                });
            }
            if (request.Action == DevelopmentAction.InfiniteFatigue)
            {
                if (debugSettings is null) throw new InvalidOperationException("无限疲劳调试开关仅支持在线服务");
                if (request.Primary >= 0) debugSettings.InfiniteFatigue = request.Primary == 1;
                return "OK " + JsonSerializer.Serialize(new { infiniteFatigue = debugSettings.InfiniteFatigue });
            }
            if (request.Action == DevelopmentAction.PcRoom)
            {
                if (debugSettings is null) throw new InvalidOperationException("网吧身份开关仅支持在线服务");
                if (request.Primary >= 0) debugSettings.PremiumPcRoom = request.Primary == 1;
                return "OK " + JsonSerializer.Serialize(new { premiumPcRoom = debugSettings.PremiumPcRoom });
            }
            if (request.Action == DevelopmentAction.SummonWangFugui)
            {
                if (debugSettings is null) throw new InvalidOperationException("王富贵开关仅支持在线服务");
                if (request.Primary >= 0) debugSettings.SummonWangFugui = request.Primary == 1;
                return "OK " + JsonSerializer.Serialize(new { summonWangFugui = debugSettings.SummonWangFugui });
            }
            if (request.Action == DevelopmentAction.SummonGuangbing)
            {
                if (debugSettings is null) throw new InvalidOperationException("光兵开关仅支持在线服务");
                if (request.Primary >= 0) debugSettings.SummonGuangbing = request.Primary == 1;
                return "OK " + JsonSerializer.Serialize(new { summonGuangbing = debugSettings.SummonGuangbing });
            }
            if (request.Action == DevelopmentAction.CashShopExpiration)
            {
                if (cashShop is null) throw new InvalidOperationException("商城期限调试开关仅支持在线服务");
                if (request.Primary >= 0) cashShop.IgnoreAbsoluteExpiration = request.Primary == 1;
                return "OK " + JsonSerializer.Serialize(new { ignoreAbsoluteExpiration = cashShop.IgnoreAbsoluteExpiration });
            }
            if (request.Action == DevelopmentAction.CashShopUsablePeriod)
            {
                if (cashShop is null) throw new InvalidOperationException("商城相对期限调试开关仅支持在线服务");
                if (request.Primary >= 0) cashShop.IgnoreUsablePeriod = request.Primary == 1;
                return "OK " + JsonSerializer.Serialize(new { ignoreUsablePeriod = cashShop.IgnoreUsablePeriod });
            }
            if (request.Action == DevelopmentAction.Item)
                return SendItem(request, mailbox ?? throw new InvalidOperationException("邮件服务未装配"),
                    items ?? throw new InvalidOperationException("物品资源未装配"));
            if (request.Action == DevelopmentAction.ItemInfo)
                return DescribeItem(request.Primary, items ?? throw new InvalidOperationException("物品资源未装配"));
            var live = find(request.Account);
            if (request.Action == DevelopmentAction.Items)
            {
                // 只读：在线时经原 owner 读取（当前角色取已提交的 Selected），离线时拒绝接管取得租约。
                var character = live is not null ? live.InspectCharacter(request.Name) :
                    WithLease(owners, request.Account, lease => LoadByName(repository, lease, request.Name));
                return DescribeInventory(character, items);
            }
            if (request.Action is DevelopmentAction.BlackDiamond or DevelopmentAction.GoldCard)
            {
                var kind = request.Action switch
                {
                    DevelopmentAction.BlackDiamond => CardMembershipKind.BlackDiamond,
                    _ => CardMembershipKind.GoldCard
                };
                long expires = request.Primary == 0 ? 0 : DateTimeOffset.UtcNow.AddDays(request.Primary).ToUnixTimeSeconds();
                var membership = live is not null ? live.SetCardMembership(request.Name,kind,expires) :
                    WithLease(owners,request.Account,lease => repository.SetCardMembership(lease,request.Name,kind,expires));
                return "OK "+JsonSerializer.Serialize(new {request.Account,kind,expires,
                    ContractType = kind == CardMembershipKind.BlackDiamond ? CardMembership.BlackDiamondType : CardMembership.DevilContractType,
                    Service0Enabled = CardMembership.DevilContractService0Enabled,
                    membership.GoldCardUsed,DailyLimit=CardMembership.DevilContractService0Enabled ? CardMembership.GoldCardDailyLimit : 0})+
                    " 已保存账号会员资格；新通关结算使用新资格。";
            }
            if (request.Action == DevelopmentAction.GrowOptions)
            {
                var roster = live is null ? WithLease(owners, request.Account, repository.ListRoster) : live.Roster();
                var role = roster.SingleOrDefault(value => string.Equals(value.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("角色不存在");
                return FormatGrowthOptions(role.Job, role.GrowType, progress ?? throw new InvalidOperationException("职业资源未装配"));
            }
            if (!request.Writes)
            {
                var rows = live is null ? WithLease(owners, request.Account, repository.List) : live.QueryCharacters();
                return FormatQuery(request, rows);
            }

            if (request.Action == DevelopmentAction.Gold)
            {
                var character = live is not null ? live.SetCharacterGold(request.Name, (uint)request.Primary) :
                    WithLease(owners, request.Account, lease => SetGold(repository, lease, request));
                return "OK " + Summary(character) + " 该角色金币已设为 " + character.Inventory.Gold + "。客户端回到选角并再次进入后刷新。";
            }
            if (request.Action is DevelopmentAction.Level or DevelopmentAction.Grow)
            {
                var reset = progress ?? throw new InvalidOperationException("等级和职业技能资源未装配");
                var edit = request.ToProgressEdit();
                var character = live is not null ? live.ResetProgress(edit, reset) : WithLease(owners, request.Account, lease =>
                {
                    var target = repository.ListRoster(lease).SingleOrDefault(value => string.Equals(value.Name, request.Name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("角色不存在");
                    var before = repository.LoadCharacter(lease, target.Slot);
                    return repository.ResetProgress(lease, reset.Prepare(before, edit));
                });
                string note = "";
                if (request.Action == DevelopmentAction.Grow && catalog is not null)
                    (character, note) = FinishGrowthQuests(repository, owners, live, request, reset, catalog, character);
                return "OK " + Summary(character) + (request.Action == DevelopmentAction.Level
                    ? " 已写入。保留已学技能、快捷栏、指令与连招，SP/TP按新等级总额（含额外点数）减已学费用重算。客户端回到选角并再次进入后刷新。"
                    : " 已写入。双技能树已重建，SP/TP恢复为当前等级的完整额度，保留已登记的额外点数。客户端回到选角并再次进入后刷新。") + note;
            }
            var progressEdit = request.ToProgressEdit();
            if (request.Action == DevelopmentAction.QuestAdd && catalog is not null &&
                catalog.Quests.TryGet((int)request.Primary, out var quest) && CyclicQuestGrade(quest.Grade))
                progressEdit = progressEdit with { DailyPeriod = QuestCycle.Day(DateTimeOffset.UtcNow) };
            var updated = live is null ? WithLease(owners, request.Account, lease => repository.UpdateProgress(lease, progressEdit)) : live.UpdateProgress(progressEdit);
            return "OK " + Summary(updated) + Written;
        }
        catch (InvalidOperationException ex) when (ex.Message == "Stale account generation")
        {
            throw new InvalidOperationException("账号世代已变化，请重试");
        }
    }

    static T WithLease<T>(AccountOwners owners, string account, Func<AccountOwners.Lease, T> action)
    {
        // 拒绝接管，避免开发命令断开正在游戏的连接。
        if (!owners.TryAcquire(account, static () =>
        {
        }, DuplicateLoginPolicy.Reject, out var lease, out var reason) || lease is null)
            throw new InvalidOperationException(reason == OwnerRejectReason.DuplicateAccountOwner ? "账号正在使用，请重试" : "无法取得账号");
        using (lease)
            return action(lease);
    }

    static string FormatGrowthOptions(byte job, byte grow, CharacterProgressReset progress)
    {
        var options = progress.GetOptions(job);
        return "OK " + JsonSerializer.Serialize(new
        {
            job, first = grow & 15, second = grow >> 4,
            options = new { baseName = options.BaseName, growTypes = options.GrowTypes.Select(value =>
                new { value = value.Value, label = value.Label, awakenings = value.Awakenings }) }
        });
    }

    static string FormatQuery(DevelopmentRequest request, Character[] rows)
    {
        if (request.Action == DevelopmentAction.List)
            return "OK count=" + rows.Length + (rows.Length == 0 ? "" : " | " + string.Join(" | ", rows.Select(Summary)));
        var match = rows.Where(c => string.Equals(c.Name, request.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (match.Length != 1)
            throw new InvalidDataException("角色不存在");
        var character = match[0];
        return request.Action switch
        {
            DevelopmentAction.Show => "OK " + Summary(character),
            DevelopmentAction.Quests => "OK " + character.Name + " quests=" + string.Join(',', character.CompletedQuests),
            DevelopmentAction.Skills => "OK " + character.Name + " skills=" + string.Join(',', character.Skills.Select(s => s.Id + ":" + s.Level)),
            _ => throw new InvalidDataException("不是查询命令")
        };
    }

    static string Summary(Character character)
    {
        string returnPoint = character.Return is { } back ? back.Town + "/" + back.Area + "/" + back.X + "/" + back.Y : "-";
        return character.Name + " id=" + character.Id + " slot=" + character.Slot +
            " job=" + character.Job + " grow=" + character.GrowType + " level=" + character.Level +
            " story=" + character.AcceptedStoryLevel + " rev=" + character.Revision +
            " pos=" + character.Position.Town + "/" + character.Position.Area + "/" + character.Position.X + "/" + character.Position.Y +
            " ret=" + returnPoint + " quests=" + character.CompletedQuests.Length + " skills=" + character.Skills.Length + " gold=" + character.Inventory.Gold +
            (character.Learning.Initialized ? " sp=" + character.Learning.Trees.Single(tree => tree.Context == character.Learning.ActiveContext).Sp + " tp=" + character.Learning.Trees.Single(tree => tree.Context == character.Learning.ActiveContext).Tp : "");
    }
}
