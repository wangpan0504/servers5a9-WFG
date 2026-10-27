using DfoServer.Network.Management;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;

namespace DfoServer.Development;

// 移植自独立 GM 工具的扩展命令：全部经 Game 层服务规划、Repository 原子提交；在线角色经原 owner 路由。
internal static partial class DevelopmentGm
{
    static readonly (string Key, CurrencyKind Kind, string Label)[] Currencies =
    [
        ("revive", CurrencyKind.ReviveCoin, "复活币"), ("win", CurrencyKind.WinPoint, "胜点"),
        ("cera", CurrencyKind.Cera, "点券"), ("token", CurrencyKind.TokenCera, "代币券"), ("happy", CurrencyKind.HappyTokenCera, "欢乐代币券"),
        ("rental", CurrencyKind.RentalPoint, "幸运星/租赁点"),
        ("black-cube", CurrencyKind.BlackCube, "黑色晶块"), ("white-cube", CurrencyKind.WhiteCube, "白色晶块"), ("red-cube", CurrencyKind.RedCube, "红色晶块"),
        ("blue-cube", CurrencyKind.BlueCube, "蓝色晶块"), ("clear-cube", CurrencyKind.ClearCube, "无色晶块"), ("gold-cube", CurrencyKind.GoldCube, "金色晶块"),
        ("common-soul", CurrencyKind.CommonSoul, "普通灵魂"), ("uncommon-soul", CurrencyKind.UncommonSoul, "高级灵魂"), ("rare-soul", CurrencyKind.RareSoul, "稀有灵魂"),
        ("unique-soul", CurrencyKind.UniqueSoul, "神器灵魂"), ("legendary-soul", CurrencyKind.LegendarySoul, "传说灵魂"), ("epic-soul", CurrencyKind.EpicSoul, "史诗灵魂")
    ];
    static readonly InventoryListType[] ClearableLists = [InventoryListType.Main, InventoryListType.Avatar, InventoryListType.Pet, InventoryListType.PersonalCargo, InventoryListType.AccountCargo];

    static string ExecuteExtended(ICharacterRepository repository, AccountOwners owners, GameSession? live, DevelopmentRequest request,
        MailboxService? mailbox, DevelopmentCatalog catalog)
    {
        var args = request.Args;
        var resources = catalog.Resources;
        switch (request.Action)
        {
            case DevelopmentAction.ItemCategories:
                return Ok(new
                {
                    maxRefinement = resources.Refinement.MaximumLevel, jobs = DevelopmentCatalog.JobList,
                    categories = catalog.Items.GroupBy(row => (row.Equipment, row.Tag)).OrderBy(group => !group.Key.Equipment).ThenBy(group => group.Key.Tag)
                        .Select(group => new { equipment = group.Key.Equipment, tag = group.Key.Tag, count = group.Count(), real = group.Count(row => !row.Placeholder) }),
                    // 武器子类（PVF 路径目录名）与数量；real 为去掉占位模板后的数量。
                    weaponSubs = catalog.Items.Where(row => row.Equipment && row.Sub.Length > 0).GroupBy(row => row.Sub).OrderBy(group => group.Key, StringComparer.Ordinal)
                        .Select(group => new { sub = group.Key, count = group.Count(), real = group.Count(row => !row.Placeholder) }),
                    // item-search / item-filter 支持的扩展选项，客户端据此决定是否在服务端做多分类、子类与排序。
                    searchOptions = new[] { "tags", "kind", "sub", "real", "sort" }
                });
            case DevelopmentAction.IndexStatus:
            {
                var state = catalog.IndexState;
                return Ok(new { items = state.Items, quests = state.Quests, attributes = state.Attributes });
            }
            case DevelopmentAction.ItemStatCounts:
            {
                // 词条命中数（用于词条筛选下拉）：词条索引未就绪时只触发预热并返回 ready=false，不阻塞。
                if (!catalog.AttributesReady) { catalog.StartWarmup(); return Ok(new { ready = false }); }
                var counts = new Dictionary<string, int>();
                foreach (var attrs in catalog.Attributes.Values)
                    foreach (string key in attrs.Stats) counts[key.Replace(' ', '_')] = counts.GetValueOrDefault(key.Replace(' ', '_')) + 1;
                return Ok(new { ready = true, counts, period = new { any = catalog.Attributes.Values.Count(a => a.Period > 0) }, indexed = catalog.Attributes.Count });
            }
            case DevelopmentAction.ItemSearch:
                return SearchItems(catalog, args, false);
            case DevelopmentAction.ItemFilter:
                return SearchItems(catalog, args, true);
            case DevelopmentAction.Wallet:
                return DescribeWallet(Target(live, owners, repository, request, false, (_, character) => character));
            case DevelopmentAction.ExpertJob:
            {
                // 副职业等级由客户端按 PVF [expertness exp] 阈值与累计熟练度换算，服务端只存累计熟练度；
                // 写入某等级时落该等级的最低阈值，读回时用同一 Level 算法给出当前等级供核对。
                var scripts = resources.ExpertJobs;
                if (args.Length == 1) throw new InvalidDataException("用法：expert-job <角色> [类型1-4 等级]");
                if (args.Length == 0)
                {
                    var character = Target(live, owners, repository, request, false, (_, value) => value);
                    int type = character.ExpertJobType;
                    return Ok(new
                    {
                        name = character.Name, rev = character.Revision, type, typeLabel = ExpertJobTypeLabel(type),
                        expertness = character.ExpertJobExperience, level = type == 0 ? 0 : scripts.Level((byte)type, character.ExpertJobExperience),
                        maxLevel = scripts.MaxLevel((byte)type)
                    });
                }
                byte job = (byte)Number(args[0], 4);
                if (job == 0) throw new InvalidDataException("副职业类型须为 1–4");
                int level = (int)Number(args[1], 100);
                int experience = scripts.ExperienceForLevel(job, level);
                Func<AccountOwners.Lease, Character, Character> write = (lease, character) =>
                {
                    var setter = repository as IExpertJobSetter ?? throw new InvalidOperationException("当前存档不支持修改副职业");
                    return setter.SetExpertJob(lease, character, job, experience);
                };
                var updated = live is not null ? live.SetExpertJob(request.Name, job, experience) : Target(null, owners, repository, request, false, write);
                return Ok(new
                {
                    name = updated.Name, rev = updated.Revision, type = (int)job, typeLabel = ExpertJobTypeLabel(job),
                    expertness = experience, level, maxLevel = scripts.MaxLevel(job),
                    message = "副职业已写入；客户端回到选角并再次进入后刷新等级显示。"
                });
            }
            case DevelopmentAction.Currency when args[0] == "vault-gold":
            {
                // 账号金库金币：账号共享，走与普通货币相同的“在线 owner 内规划→原子提交→同步在线快照”路径；上限取当前金库容量档位。
                if (args[1] is not ("set" or "add" or "sub")) throw new InvalidDataException("操作须为 set、add 或 sub");
                long amount = Number(args[2], int.MaxValue);
                var updated = Commit(live, owners, repository, request, false, character =>
                {
                    var before = character.Inventory;
                    if (before.AccountCargo.Capacity == 0) return InventoryResult.Reject();
                    long current = before.AccountCargo.Gold;
                    long next = args[1] switch { "set" => amount, "add" => current + amount, _ => current - amount };
                    if (next < 0 || next > resources.Cargo.GoldLimit(before.AccountCargo.Capacity)) return InventoryResult.Reject();
                    return new(true, 0, new InventoryPlan(before, next == current ? before : before with { AccountCargo = before.AccountCargo with { Gold = (int)next } }, "gm_vault_gold"));
                }, "账号金库未开通，或超过当前容量档位的金币上限");
                return DescribeWallet(updated, "账号金库金币已提交");
            }
            case DevelopmentAction.Currency:
            {
                var kind = Currencies.FirstOrDefault(row => row.Key == args[0]);
                if (kind.Key is null) throw new InvalidDataException("未知货币种类：" + args[0]);
                if (args[1] is not ("set" or "add" or "sub")) throw new InvalidDataException("操作须为 set、add 或 sub");
                long amount = Number(args[2], int.MaxValue);
                var updated = Commit(live, owners, repository, request, false, character =>
                {
                    var before = character.Inventory;
                    long current = CurrencyWallet.Get(before, kind.Kind);
                    long next = args[1] switch { "set" => amount, "add" => current + amount, _ => current - amount };
                    if (next is < 0 or > int.MaxValue) return InventoryResult.Reject();
                    return new(true, 0, new InventoryPlan(before, next == current ? before : CurrencyWallet.Set(before, kind.Kind, next), "gm_currency"));
                }, "余额不足或超过 0–2147483647");
                return DescribeWallet(updated, kind.Label + " 已提交；客户端回到选角并再次进入后刷新。");
            }
            case DevelopmentAction.Grant:
            {
                var template = resources.Items.Get((int)Number(args[0], int.MaxValue));
                int count = (int)Number(args[1], int.MaxValue), upgrade = (int)Number(args[2], 31), refinement = (int)Number(args[3], resources.Refinement.MaximumLevel);
                if (count <= 0) throw new InvalidDataException("数量至少为1");
                RequireEquipmentOptions(template, upgrade, refinement);
                string[] extra = [.. args.Skip(5)];
                if (extra.Count(token => token.StartsWith("amp:", StringComparison.Ordinal)) > 1 || extra.Count(token => token == "seal:open") > 1 || extra.Count(token => token == "period:off") > 1 || extra.Count(token => token.StartsWith("ability:", StringComparison.Ordinal)) > 1 || extra.Count(token => token.StartsWith("expire:", StringComparison.Ordinal)) > 1 || extra.Count(token => token.StartsWith("quality:", StringComparison.Ordinal)) > 1)
                    throw new InvalidDataException("可选参数只能各出现一次");
                int ability = -1;
                bool clearPeriod = false;
                int? customExpire = null;
                int? qualityPercent = null;
                foreach (string token in extra)
                {
                    if (token.StartsWith("amp:", StringComparison.Ordinal) || token == "seal:open") continue;
                    if (token == "period:off") { clearPeriod = true; continue; }
                    if (token.StartsWith("expire:", StringComparison.Ordinal)) { customExpire = ParseExpire(token); continue; }
                    if (token.StartsWith("ability:", StringComparison.Ordinal)) { ability = (int)Number(token[8..], 65535); continue; }
                    if (token.StartsWith("quality:", StringComparison.Ordinal)) { qualityPercent = (int)Number(token[8..], 100); continue; }
                    throw new InvalidDataException("可选参数只能是 amp:<类型>:<值>、seal:open、ability:<选项>、period:off、expire:<Unix秒>、quality:<1-100>");
                }
                // 品级只对普通装备有意义；百分比 1-100 换算成客户端实例品级种子（100 = 最上级）。
                if (qualityPercent is not null && template.Kind != ItemKind.Equipment)
                    throw new InvalidDataException("只有普通装备可以指定品级 quality:<1-100>");
                int? qualitySeed = qualityPercent is { } qualityValue ? EquipmentQualityBox.SeedForPercent(qualityValue) : null;
                if (clearPeriod && customExpire is not null) throw new InvalidDataException("期限不能同时改为不过期和指定时间");
                var amplify = ParseAmplify(extra.FirstOrDefault(token => token.StartsWith("amp:", StringComparison.Ordinal)), false);
                if (amplify.Type != 0 && template.Kind != ItemKind.Equipment) throw new InvalidDataException("只有普通装备可以增幅");
                if (ability >= 0 && template.Kind != ItemKind.Avatar) throw new InvalidDataException("只有装扮可以指定属性选项");
                bool unseal = extra.Contains("seal:open");
                // 封印：模板 [attach type] 为 [sealing] 的普通装备创建时带封装标记；GM 可直接发为已解封（与称号簿穿戴消封同一语义）。其他物品没有封装语义，拒绝。
                if (unseal && !(template.Kind == ItemKind.Equipment && template.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("只有 [sealing] 封装装备才能指定 seal:open");
                if (args[4] == "mail")
                {
                    var now = DateTimeOffset.UtcNow;
                    var prototype = InventoryRewardGrantService.Create(template, utcNow: now, upgrade: (byte)upgrade, refinement: (byte)refinement, quality: qualitySeed);
                    if (prototype.IsStackable ? count > template.StackLimit : count > 10)
                        throw new InvalidDataException(prototype.IsStackable ? "数量超过该道具堆叠上限 " + template.StackLimit : "非堆叠道具每封邮件最多发放10件");
                    if (prototype.IsStackable) prototype = prototype with { Value = count };
                    prototype = WithAmplify(prototype, amplify);
                    if (unseal) prototype = prototype with { SealFlag = 0 };
                    if (ability >= 0) prototype = prototype with { Durability = (ushort)ability };
                    if (clearPeriod || customExpire is not null) prototype = prototype with { ExpireTime = clearPeriod ? ClearedExpire(template) : customExpire!.Value };
                    var attachments = Enumerable.Range(0, prototype.IsStackable ? 1 : count).Select(_ => InventoryRewardGrantService.CreateItem(template, FirstSlot(template), prototype));
                    return DeliverMail(mailbox, request, attachments, "开发GM发放：" + template.Name + " × " + count + Options(upgrade, refinement, amplify), now);
                }
                RequireMode(args[4]);
                if (!InventoryRewardGrantService.Create(template).IsStackable && count > 352)
                    throw new InvalidDataException("单次直接入包非堆叠物品最多352件");
                int? grantedExpire = clearPeriod ? ClearedExpire(template) : customExpire;
                var granted = Commit(live, owners, repository, request, true, character => AmplifyNew(character.Inventory, InventoryRewardGrantService.TryGrantBatch(character,
                    [new InventoryReward(template.Id, count, (byte)upgrade, (byte)refinement, Quality: qualitySeed)], resources.Items, resources.GoldLimits, resources.EpicPieces), amplify, unseal, ability < 0 ? null : ability, grantedExpire),
                    "背包空间、堆叠、期限或余额上限不满足，整批未发放");
                return Ok(new { target = "inventory", name = granted.Name, rev = granted.Revision, item = template.Id, itemName = template.Name, count, upgrade, refinement,
                    amplifyType = amplify.Type, amplifyValue = amplify.Value, unsealed = unseal, quality = qualityPercent });
            }
            case DevelopmentAction.SetGrant:
            {
                int index = (int)Number(args[0], int.MaxValue), upgrade = (int)Number(args[1], 31), refinement = (int)Number(args[2], resources.Refinement.MaximumLevel);
                var role = Target(live, owners, repository, request, false, (_, character) => (character.Job, character.GrowType));
                var templates = SetMembers(catalog, index, role.Job, (byte)(role.GrowType & 15));
                var amplify = ParseAmplify(args.Length > 4 ? args[4] : null, false);
                var rewards = templates.Select(template => new InventoryReward(template.Id, 1,
                    template.Kind == ItemKind.Equipment ? (byte)upgrade : (byte)0, template.Kind == ItemKind.Equipment && template.EquipmentSlot == 12 ? (byte)refinement : (byte)0)).ToArray();
                if (args[3] == "mail")
                {
                    // 每封邮件最多10件；超过时按顺序拆成多封，每封各自原子投递，整套不是全有或全无。
                    var now = DateTimeOffset.UtcNow;
                    var pairs = templates.Zip(rewards).ToArray();
                    var delivered = new List<uint>();
                    string? failure = null;
                    foreach (var chunk in pairs.Chunk(10))
                    {
                        var attachments = chunk.Select(pair => InventoryRewardGrantService.CreateItem(pair.First, FirstSlot(pair.First),
                            pair.First.Kind == ItemKind.Equipment ? WithAmplify(InventoryRewardGrantService.Create(pair.First, utcNow: now, upgrade: pair.Second.Upgrade, refinement: pair.Second.Refinement), amplify) :
                            InventoryRewardGrantService.Create(pair.First, utcNow: now, upgrade: pair.Second.Upgrade, refinement: pair.Second.Refinement)));
                        string part = pairs.Length > 10 ? "（" + (delivered.Count + 1) + "/" + (pairs.Length + 9) / 10 + "）" : "";
                        try
                        {
                            string answer = DeliverMail(mailbox, request, attachments, "开发GM发放套装 #" + index + part + "：" + string.Join("、", chunk.Select(pair => pair.First.Name)) + Options(upgrade, refinement, amplify), now);
                            delivered.Add(JsonDocument.Parse(answer[3..]).RootElement.GetProperty("mail").GetUInt32());
                        }
                        catch (InvalidDataException error) { failure = error.Message; break; }
                    }
                    if (delivered.Count == 0) throw new InvalidDataException(failure ?? "邮件发放失败");
                    return Ok(new { target = "mail", name = request.Name, set = index, mails = delivered, pieces = pairs.Length, partial = failure is not null, error = failure });
                }
                RequireMode(args[3]);
                var granted = Commit(live, owners, repository, request, true, character => AmplifyNew(character.Inventory, InventoryRewardGrantService.TryGrantBatch(character, rewards,
                    resources.Items, resources.GoldLimits, resources.EpicPieces), amplify), "背包空间或期限不满足，整套未发放");
                return Ok(new { target = "inventory", name = granted.Name, rev = granted.Revision, set = index, items = templates.Select(t => new { id = t.Id, name = t.Name }) });
            }
            case DevelopmentAction.SetMembers:
            {
                int index = (int)Number(args[0], int.MaxValue);
                var role = Target(live, owners, repository, request, false, (_, character) => (character.Job, character.GrowType));
                var members = SetMembers(catalog, index, role.Job, (byte)(role.GrowType & 15));
                return Ok(new
                {
                    set = index,
                    items = members.Select(template => new { id = template.Id, name = template.Name, slot = template.EquipmentSlot })
                });
            }
            case DevelopmentAction.ItemEdit:
            {
                var parts = args[0].Split(':');
                if (parts.Length != 3) throw new InvalidDataException("物品格式为 容器:位置:UID");
                var position = new InventorySlot((InventoryListType)Number(parts[0], 255), (short)Number(parts[1], short.MaxValue));
                long uid = Number(parts[2], long.MaxValue);
                int? upgrade = null, refine = null, ability = null, count = null, customExpire = null;
                (byte Type, ushort Value)? amplify = null;
                bool clearPeriod = false;
                foreach (string token in args.Skip(1))
                {
                    if (token == "period:off") { clearPeriod = true; continue; }
                    if (token.StartsWith("expire:", StringComparison.Ordinal)) { customExpire = ParseExpire(token); continue; }
                    if (token.StartsWith("up:", StringComparison.Ordinal)) upgrade = (int)Number(token[3..], 31);
                    else if (token.StartsWith("ref:", StringComparison.Ordinal)) refine = (int)Number(token[4..], resources.Refinement.MaximumLevel);
                    else if (token.StartsWith("amp:", StringComparison.Ordinal)) amplify = ParseAmplify(token, true);
                    else if (token.StartsWith("ability:", StringComparison.Ordinal)) ability = (int)Number(token[8..], 65535);
                    else if (token.StartsWith("count:", StringComparison.Ordinal)) count = (int)Number(token[6..], int.MaxValue);
                    else throw new InvalidDataException("未知修改项：" + token);
                }
                if (clearPeriod && customExpire is not null) throw new InvalidDataException("期限不能同时改为不过期和指定时间");
                var updated = Commit(live, owners, repository, request, true, character =>
                {
                    var before = character.Inventory;
                    var item = before.Find(position);
                    if (item is null || item.Uid != uid || item.Core.EquipmentLockId != 0) return InventoryResult.Reject();
                    var template = resources.Items.Get(item.Core.ItemId);
                    var core = item.Core;
                    if (upgrade is not null || amplify is not null)
                    {
                        if (core.Kind != ItemKind.Equipment) return InventoryResult.Reject();
                        int level = upgrade ?? (core.Attr & 31);
                        var amp = amplify ?? (core.AmplifyType, core.AmplifyValue);
                        core = core with { Attr = (byte)((core.Attr & 0xE0) | level), AmplifyType = amp.Item1, AmplifyValue = amp.Item2 };
                    }
                    if (refine is not null)
                    {
                        if (template.Kind != ItemKind.Equipment || template.EquipmentSlot != 12) return InventoryResult.Reject();
                        core = core with { GenuineUpgrade = (byte)refine.Value };
                    }
                    if (ability is not null)
                    {
                        if (template.Kind != ItemKind.Avatar) return InventoryResult.Reject();
                        core = core with { Durability = (ushort)ability.Value };
                    }
                    if (count is not null)
                    {
                        if (!core.IsStackable || count < 1 || count > template.StackLimit) return InventoryResult.Reject();
                        core = core with { Value = count.Value };
                    }
                    var changed = item with { Core = core };
                    if (clearPeriod || customExpire is not null) changed = ApplyExpire(changed, clearPeriod ? ClearedExpire(template) : customExpire!.Value);
                    var after = before with { Items = before.Items.Replace(item, changed) };
                    after.Validate();
                    return new(true, 0, new InventoryPlan(before, after, "gm_item_edit"));
                }, "物品实例已变化、被锁定，或这项属性该物品不能改");
                return Ok(new { name = updated.Name, rev = updated.Revision });
            }
            case DevelopmentAction.ItemAmplify:
            {
                var parts = args[0].Split(':');
                if (parts.Length != 3) throw new InvalidDataException("物品格式为 容器:位置:UID");
                var position = new InventorySlot((InventoryListType)Number(parts[0], 255), (short)Number(parts[1], short.MaxValue));
                long uid = Number(parts[2], long.MaxValue);
                var amplify = ParseAmplify(args[1], true);
                int level = (int)Number(args[2], 31);
                var updated = Commit(live, owners, repository, request, true, character =>
                {
                    var before = character.Inventory;
                    var item = before.Find(position);
                    if (item is null || item.Uid != uid || item.Core.Kind != ItemKind.Equipment || item.Core.EquipmentLockId != 0) return InventoryResult.Reject();
                    // Attr低五位是强化或增幅等级，二者由 AmplifyType 是否为0区分；高三位与其余完整核心原样保留。
                    var changed = item with { Core = item.Core with { Attr = (byte)((item.Core.Attr & 0xE0) | level), AmplifyType = amplify.Type, AmplifyValue = amplify.Value } };
                    var after = before with { Items = before.Items.Replace(item, changed) };
                    after.Validate();
                    return new(true, 0, new InventoryPlan(before, after, "gm_amplify"));
                }, "物品实例已变化、不是普通装备或装备被锁定");
                return Ok(new { name = updated.Name, rev = updated.Revision, mode = amplify.Type == 0 ? "reinforce" : "amplify", level, amplifyType = amplify.Type, amplifyValue = amplify.Value });
            }
            case DevelopmentAction.ItemDelete:
            {
                var references = args[0].Split(',').Select(text =>
                {
                    var parts = text.Split(':');
                    if (parts.Length != 3) throw new InvalidDataException("删除项格式为 容器:位置:UID");
                    return (List: (InventoryListType)Number(parts[0], 255), Slot: (short)Number(parts[1], short.MaxValue), Uid: Number(parts[2], long.MaxValue));
                }).ToArray();
                if (references.Length is < 1 or > 64 || references.Select(row => (row.List, row.Slot)).Distinct().Count() != references.Length)
                    throw new InvalidDataException("一次删除1–64件且不能重复");
                var updated = Commit(live, owners, repository, request, true, character =>
                    DeletePlan(character.Inventory, references.Select(row => (character.Inventory.Find(new(row.List, row.Slot)), row.Uid))),
                    "物品实例已变化、被锁定或容器不允许删除，整批保留");
                return Ok(new { name = updated.Name, rev = updated.Revision, deleted = references.Length });
            }
            case DevelopmentAction.ItemClear:
            {
                var list = (InventoryListType)Number(args[0], 255);
                if (!ClearableLists.Contains(list)) throw new InvalidDataException("只能清理物品栏、时装栏、宠物栏或个人仓库");
                int deleted = 0;
                var updated = Commit(live, owners, repository, request, true, character =>
                {
                    var targets = character.Inventory.Items.Where(item => item.Position.List == list && item.Core.EquipmentLockId == 0).ToArray();
                    deleted = targets.Length;
                    return DeletePlan(character.Inventory, targets.Select(item => ((InventoryItem?)item, item.Uid)));
                }, "清理被现有删除规则拒绝，整批保留");
                return Ok(new { name = updated.Name, rev = updated.Revision, list = list.ToString(), deleted, lockedKept = updated.Inventory.Items.Count(item => item.Position.List == list) });
            }
            case DevelopmentAction.Mails:
            {
                var service = mailbox ?? throw new InvalidOperationException("邮件服务未装配");
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var page = Target(live, owners, repository, request, false, (lease, character) => service.Inspect(lease, character.Id, DateTimeOffset.UtcNow));
                return Ok(new
                {
                    notLoaded = page.NotLoaded,
                    mails = page.Entries.Select(mail => new
                    {
                        id = mail.Id, sender = mail.SenderName, text = mail.Text, gold = mail.Gold, created = mail.Created, expires = mail.Expires,
                        state = mail.State, system = mail.System, expired = !mail.Available(now),
                        deletable = mail.Gold == 0 && mail.Attachments.IsEmpty && mail.Available(now),
                        attachments = mail.Attachments.Select(attachment => new
                        {
                            itemId = attachment.Item.Core.ItemId, name = TryTemplate(resources.Items, attachment.Item.Core.ItemId)?.Name,
                            count = attachment.Item.Core.IsStackable ? attachment.Item.Core.Value : 1,
                            upgrade = attachment.Item.Core.Kind == ItemKind.Equipment ? attachment.Item.Core.Attr & 31 : 0, amplifyType = attachment.Item.Core.AmplifyType, amplifyValue = attachment.Item.Core.AmplifyValue
                        })
                    })
                });
            }
            case DevelopmentAction.MailDelete:
            {
                var service = mailbox ?? throw new InvalidOperationException("邮件服务未装配");
                // force：GM 强制删除。默认规则只删「无附件、无金币、未过期」的邮件；带 force 时连附件、
                // 金币和过期邮件一起删（附件行保留在库里但已不可达，与过期清理同一语义）。
                bool force = args.Length > 1 && args[1] == "force";
                if (args.Length > 1 && !force) throw new InvalidDataException("mail-delete 的可选参数只有 force");
                int deleted = Target(live, owners, repository, request, true, (lease, character) =>
                {
                    var now = DateTimeOffset.UtcNow;
                    var ids = args[0] == "all"
                        ? service.Inspect(lease, character.Id, now).Entries
                            .Where(mail => force || mail.Gold == 0 && mail.Attachments.IsEmpty && mail.Available(now.ToUnixTimeSeconds()))
                            .Select(mail => mail.Id).ToImmutableArray()
                        : args[0].Split(',').Select(text => (uint)Number(text, uint.MaxValue)).ToImmutableArray();
                    if (ids.IsEmpty) throw new InvalidDataException(force ? "邮箱里没有邮件" : "没有可删除的邮件（带附件/金币或已过期的邮件要加 force）");
                    var result = service.ChangeState(lease, character.Id, ids, 0, now, force);
                    if (!result.Accepted) throw new InvalidDataException("邮件删除被拒绝：" + result.Error + "（未领取资产或已过期的邮件要加 force）");
                    return ids.Length;
                });
                return Ok(new { name = request.Name, deleted, forced = force });
            }
            case DevelopmentAction.Rename:
            {
                string newName = args[0];
                if (repository is not ICharacterRenamer renamer) throw new InvalidOperationException("当前存档不支持改名");
                if (!CharacterName.IsValid(newName)) throw new InvalidDataException("新角色名无效（长度/字符不符合创建规则）");
                Func<AccountOwners.Lease, Character, Character> rename = (lease, character) =>
                {
                    if (string.Equals(character.Name, newName, StringComparison.Ordinal)) throw new InvalidDataException("新名字与原名字相同");
                    return renamer.Rename(lease, character, newName);
                };
                var renamed = live is not null ? live.CommitRosterChange(request.Name, rename) : Target(live, owners, repository, request, true, rename);
                return Ok(new { target = "character", oldName = request.Name, name = renamed.Name, id = renamed.Id, rev = renamed.Revision });
            }
            case DevelopmentAction.CharDelete:
            {
                if (!string.Equals(args[0], request.Name, StringComparison.Ordinal)) throw new InvalidDataException("确认角色名与目标角色名不一致");
                Func<AccountOwners.Lease, Character, CharacterDeletion> delete = (lease, character) =>
                {
                    var target = repository.LoadDeletionTarget(lease, character.Slot);
                    if (target.Id != character.Id || target.Name != character.Name) throw new InvalidOperationException("角色名册已变化，请重试");
                    if (target.AdventureBusy) throw new InvalidDataException("角色正在冒险团远征中，无法删除");
                    return repository.Delete(lease, target);
                };
                var deleted = live is not null ? live.CommitRosterChange(request.Name, delete) : Target(live, owners, repository, request, true, delete);
                return Ok(new { target = "character", deleted = deleted.Name, id = deleted.Id, slot = deleted.Slot });
            }
            case DevelopmentAction.Points:
            {
                var character = Target(live, owners, repository, request, false, (_, value) => value);
                var learning = character.Learning;
                var tree = learning.Initialized ? learning.Trees.FirstOrDefault(value => value.Context == learning.ActiveContext) : null;
                return Ok(new { name = character.Name, rev = character.Revision, level = character.Level, initialized = learning.Initialized,
                    bonusSp = learning.BonusSp, bonusTp = learning.BonusTp, sp = tree?.Sp, tp = tree?.Tp });
            }
            case DevelopmentAction.ExpTable:
            {
                var growth = catalog.Growth ?? throw new InvalidDataException("等级经验表未装配");
                // 累计经验边界在 u64（type9 高/低 DWORD 已由 Catalog 组合），序列化为 JSON 数字时最大约 5.4e9，JS 安全。
                return Ok(new
                {
                    max = DfoServer.GameWorld.Characters.CharacterGrowthCatalog.MaximumSupportedLevel,
                    levels = Enumerable.Range(1, DfoServer.GameWorld.Characters.CharacterGrowthCatalog.MaximumSupportedLevel).Select(level => new
                    { level, start = growth.GetLevelStartExperience(level), span = level < DfoServer.GameWorld.Characters.CharacterGrowthCatalog.MaximumSupportedLevel ? growth.GetLevelExperienceSpan(level + 1) : 0UL })
                });
            }
            case DevelopmentAction.SkillList:
            {
                var character = Target(live, owners, repository, request, false, (_, value) => value);
                var learning = character.Learning;
                var tree = learning.Initialized ? learning.Trees.FirstOrDefault(value => value.Context == learning.ActiveContext) : null;
                var rows = tree is null
                    ? character.Skills.Select(skill => new { id = (int)skill.Id, name = catalog.SkillName(character.Job, skill.Id), level = (int)skill.Level })
                    : tree.Skills.Select(skill => new { id = (int)skill.Id, name = catalog.SkillName(character.Job, skill.Id), level = (int)skill.Level });
                return Ok(new
                {
                    name = character.Name, rev = character.Revision, job = character.Job, level = character.Level, initialized = learning.Initialized,
                    sp = tree?.Sp, tp = tree?.Tp, bonusSp = learning.BonusSp, bonusTp = learning.BonusTp, skills = rows.OrderBy(row => row.id)
                });
            }
            case DevelopmentAction.Stats:
            {
                var computer = catalog.Stats ?? throw new InvalidDataException("服务端属性计算器未装配");
                var character = Target(live, owners, repository, request, false, (_, value) => value);
                var value = computer.Compute(character);
                double D(double raw) => raw / 10.0;
                return Ok(new
                {
                    name = character.Name, level = character.Level, job = character.Job, growType = character.GrowType,
                    // 只含服务端按等级/转职计算的 CHR 基础属性；装备、技能、称号、套装等修正由客户端派生，这里不包含。
                    scope = "base", note = "服务端按等级与转职计算的基础属性，不含装备、技能、称号与套装加成。",
                    stats = new
                    {
                        maxHp = D(value.MaxHp), maxMp = D(value.MaxMp), physicalAttack = D(value.PhysicalAttack), physicalDefense = D(value.PhysicalDefense),
                        magicalAttack = D(value.MagicalAttack), magicalDefense = D(value.MagicalDefense), fireResistance = D(value.FireResistance),
                        waterResistance = D(value.WaterResistance), darkResistance = D(value.DarkResistance), lightResistance = D(value.LightResistance),
                        inventoryLimit = D(value.InventoryLimit), mpRecovery = D(value.MpRecovery), moveSpeed = D(value.MoveSpeed), attackSpeed = D(value.AttackSpeed),
                        castSpeed = D(value.CastSpeed), hitRecovery = D(value.HitRecovery), jumpPower = D(value.JumpPower), weight = D(value.Weight)
                    }
                });
            }
            case DevelopmentAction.TitleList:
                return TitleList(catalog, Target(live, owners, repository, request, false, (_, value) => value), args.Length > 0 ? args[0] : "");
            case DevelopmentAction.TitleComplete:
            {
                var titles = catalog.Titles;
                var ids = TitleSelection(catalog, args[0]);
                // 在线角色也可直接完成：在本会话 owner 内提交并同步内存快照；客户端称号簿画面等下次选角刷新。
                Func<AccountOwners.Lease, Character, (Character After, (string Name, long Revision, int Completed, int Skipped, int Postals, int Sections) Value)> work = (lease, character) =>
                {
                    var (result, skipped) = catalog.TitleService.Complete(character, ids, DateTimeOffset.UtcNow);
                    if (!result.Accepted || result.Plan is not { } plan)
                        throw new InvalidDataException(skipped.Length == 0 ? "没有需要完成的成就（可能都已完成）" : "成就奖励无法发放（背包空间或奖励类型不支持），未写入任何成就");
                    var after = repository.CommitTitleAchievement(lease, plan);
                    return (after, (after.Name, after.Revision, plan.Completed.Length, skipped.Length, plan.Postals.Length, plan.Sections.Count - character.TitleSections.Count));
                };
                var done0 = live is not null ? live.CommitDevelopmentTitle(request.Name, work) : Target(null, owners, repository, request, true, (lease, character) => work(lease, character).Value);
                var done = (Name: done0.Name, Revision: done0.Revision, Item3: done0.Completed, Item4: done0.Skipped, Item5: done0.Postals, Sections: done0.Sections);
                return Ok(new { name = done.Name, rev = done.Revision, completed = done.Item3, skipped = done.Item4, postals = done.Item5, sections = done.Sections });
            }
            case DevelopmentAction.QuestSearch:
                return SearchQuests(catalog, Target(live, owners, repository, request, false, (_, character) => character), args);
            case DevelopmentAction.QuestChain:
            {
                int seed = (int)Number(args[0], 29999);
                if (!catalog.Quests.TryGet(seed, out _)) throw new InvalidDataException("任务目录没有这个任务: " + seed);
                var character = Target(live, owners, repository, request, false, (_, value) => value);
                var closure = new SortedSet<int>();
                var queue = new Queue<int>([seed]);
                while (queue.TryDequeue(out int current))
                    if (closure.Add(current) && catalog.Quests.TryGet(current, out var quest))
                        foreach (int pre in PrerequisiteGroup(catalog, quest, character.Job))
                            if (pre is >= 1 and <= 29999 && catalog.Quests.TryGet(pre, out _)) queue.Enqueue(pre);
                return CompleteQuests(repository, owners, live, request, [.. closure], character.CompletedQuests, catalog);
            }
            case DevelopmentAction.QuestBatch:
            {
                var ids = args[0].Split(',').Select(text => (int)Number(text, 29999)).ToImmutableArray();
                var before = Target(live, owners, repository, request, false, (_, value) => value.CompletedQuests);
                return CompleteQuests(repository, owners, live, request, ids, before, catalog);
            }
            default:
                throw new InvalidDataException(DevelopmentRequest.Usage);
        }
    }

    static ImmutableArray<int> TitleSelection(DevelopmentCatalog catalog, string spec)
    {
        var titles = catalog.Titles;
        if (spec == "all") return [.. titles.Quests.Keys];
        if (spec == "book" || spec.StartsWith("book:", StringComparison.Ordinal))
        {
            int category = spec == "book" ? -1 : Array.IndexOf(DfoServer.GameWorld.Quests.TitleCatalog.Categories, spec[5..]);
            if (spec != "book" && category < 0) throw new InvalidDataException("称号簿分类无效");
            return [.. titles.QuestCategories.Where(pair => titles.Quests.ContainsKey(pair.Key) && (category < 0 || pair.Value.Contains(category))).Select(pair => pair.Key)];
        }
        var ids = spec.Split(',').Select(text => (int)Number(text, int.MaxValue)).Distinct().ToImmutableArray();
        if (ids.Length > 500 || ids.Any(id => !titles.Quests.ContainsKey(id))) throw new InvalidDataException("成就ID无效或超过500个");
        return ids;
    }

    static string TitleList(DevelopmentCatalog catalog, Character character, string filter)
    {
        var titles = catalog.Titles;
        int categoryFilter = Array.IndexOf(DfoServer.GameWorld.Quests.TitleCatalog.Categories, filter);
        var rows = titles.Quests.Values.Select(quest =>
        {
            var categories = titles.QuestCategories.GetValueOrDefault(quest.Quest.Id, []);
            return (Quest: quest, Categories: categories, Name: catalog.DisplayQuestName(quest.Quest.Id) is { Length: > 0 } shown && shown != "任务 " + quest.Quest.Id ? shown : "成就 " + quest.Quest.Id);
        }).Where(row => QuestVisible(row.Quest.Quest, character.Job, character.GrowType, catalog.GrowthOptions(character.Job), catalog.QuestName(row.Quest.Quest.Id)))
            .Where(row => filter.Length == 0 || categoryFilter >= 0 ? categoryFilter < 0 || row.Categories.Contains(categoryFilter) :
            row.Quest.Quest.Id.ToString(CultureInfo.InvariantCulture) == filter || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Quest.Quest.Id).ToArray();
        int completed = titles.Quests.Keys.Count(id => character.TitleProgress.GetValueOrDefault(id)?.Remaining.Complete == true);
        return Ok(new
        {
            name = character.Name, total = titles.Quests.Count, completed, sections = character.TitleSections.Count, sectionTotal = titles.Sections.Count,
            matched = rows.Length,
            quests = rows.Take(300).Select(row => new
            {
                id = row.Quest.Quest.Id, name = row.Name, condition = row.Quest.Quest.Condition,
                categories = row.Categories.Select(index => DfoServer.GameWorld.Quests.TitleCatalog.Categories[index]),
                done = character.TitleProgress.GetValueOrDefault(row.Quest.Quest.Id)?.Remaining.Complete == true
            })
        });
    }

    static string Ok(object value) => "OK " + JsonSerializer.Serialize(value);

    // 副职业类型编号取自 character/expertjob.lst：1 附魔师、2 炼金术师、3 分解师、4 控偶师。
    static string ExpertJobTypeLabel(int type) => type switch
    {
        1 => "附魔师", 2 => "炼金术师", 3 => "分解师", 4 => "控偶师", _ => "未开通"
    };

    static long Number(string text, long maximum) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) && value <= maximum ? value : throw new InvalidDataException("数值无效或超出范围：" + text);

    static void RequireMode(string mode)
    {
        if (mode != "direct") throw new InvalidDataException("发放方式须为 mail 或 direct");
    }

    static void RequireEquipmentOptions(ItemTemplate template, int upgrade, int refinement)
    {
        if (template.Kind != ItemKind.Equipment && (upgrade != 0 || refinement != 0))
            throw new InvalidDataException("只有普通装备可以指定强化或锻造");
        if (refinement != 0 && template.EquipmentSlot != 12)
            throw new InvalidDataException("只有普通武器可以指定锻造");
    }

    static string Options(int upgrade, int refinement, (byte Type, ushort Value) amplify = default) =>
        (upgrade == 0 && amplify.Type == 0 ? "" : (amplify.Type == 0 ? "，强化 +" : "，增幅 +") + upgrade) + (refinement == 0 ? "" : "，锻造 +" + refinement) +
        (amplify.Type == 0 ? "" : "，异次元" + AmplifyNames[amplify.Type] + " " + amplify.Value);

    // 类型编号取自本版 etc/amplifyitem.etc [option mapping table]：1 physical defense(体力)、2 magical defense(精神)、
    // 3 physical attack(力量)、4 magical attack(智力)；基础值随等级/稀有度的生成公式尚未闭合，由 GM 显式给出。
    static readonly string[] AmplifyNames = ["无", "体力", "精神", "力量", "智力"];

    static (byte Type, ushort Value) ParseAmplify(string? token, bool allowNone)
    {
        if (token is null) return (0, 0);
        var parts = token.Split(':');
        if (parts.Length != 3 || parts[0] != "amp") throw new InvalidDataException("增幅参数格式为 amp:<类型1-4>:<异次元值>");
        byte type = (byte)Number(parts[1], 4);
        ushort value = (ushort)Number(parts[2], ushort.MaxValue);
        if (type == 0 ? !allowNone || value != 0 : value == 0) throw new InvalidDataException(allowNone ? "类型0表示改回强化，值须为0；类型1–4的值须为1–65535" : "增幅类型须为1–4且异次元值为1–65535");
        return (type, value);
    }

    static ItemCore WithAmplify(ItemCore core, (byte Type, ushort Value) amplify) =>
        amplify.Type == 0 ? core : core with { AmplifyType = amplify.Type, AmplifyValue = amplify.Value };

    // 脚本带 [usable period] 或 [expiration date] 时，客户端把实例截止 0 减当前时间，剩余秒归零后显示「已超过使用期限」。
    // 协议上限 2147483647（2038-01-19）是这类模板的不过期写法；时装和宠物的 0 在线路上就是永久，不能改成这个上限。
    static int ClearedExpire(ItemTemplate template) =>
        template.Kind is ItemKind.Avatar or ItemKind.Creature ? 0
        : template.Expiration.Days > 0 || template.Expiration.AbsoluteTime > 0 ? int.MaxValue : 0;

    static int ParseExpire(string token)
    {
        if (!token.StartsWith("expire:", StringComparison.Ordinal)) throw new InvalidDataException("到期时间格式为 expire:<Unix秒>");
        int value = (int)Number(token[7..], int.MaxValue);
        if (value <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new InvalidDataException("到期时间必须晚于现在，且不晚于 2038-01-19 11:14:07（UTC+8）");
        return value;
    }

    // 时装和宠物的期限在详情上，核心必须保持 0；普通装备和堆叠物写在核心上。
    static InventoryItem ApplyExpire(InventoryItem item, int expire) => item.Avatar is not null
        ? item with { Core = item.Core with { ExpireTime = 0 }, Avatar = item.Avatar with { ExpireTime = expire } }
        : item.Creature is not null
            ? item with { Core = item.Core with { ExpireTime = 0 }, Creature = item.Creature with { ExpireTime = expire } }
            : item with { Core = item.Core with { ExpireTime = expire } };

    // 直接入包：统一发放规划完成后，只给本次新建的普通装备实例写入增幅类型与值，同一计划提交。
    static InventoryResult AmplifyNew(InventorySnapshot before, InventoryResult result, (byte Type, ushort Value) amplify, bool unseal = false, int? ability = null, int? clearedExpire = null)
    {
        if (amplify.Type == 0 && !unseal && ability is null && clearedExpire is null || !result.Accepted) return result;
        var existing = before.Items.Select(item => item.Uid).ToHashSet();
        var after = result.Plan!.After;
        after = after with { Items = [.. after.Items.Select(item =>
        {
            if (existing.Contains(item.Uid)) return item;
            var core = item.Core;
            if (core.Kind == ItemKind.Equipment) core = WithAmplify(unseal ? core with { SealFlag = 0 } : core, amplify);
            if (ability is not null && core.Kind == ItemKind.Avatar) core = core with { Durability = (ushort)ability };
            return clearedExpire is int expire ? ApplyExpire(item with { Core = core }, expire) : item with { Core = core };
        })] };
        // 新建实例在提交时才分配UID，规划阶段按分配中快照校验。
        after.Validate(allocating: true);
        var plan = result.Plan;
        return result with { Plan = new InventoryPlan(plan.Before, after, plan.Reason, plan.CharacterPrecondition, plan.OverflowItems) { PreserveMaterials = plan.PreserveMaterials } };
    }

    static InventorySlot FirstSlot(ItemTemplate template) => new(ItemSlotBoundService.List(template.Kind), ItemSlotBoundService.Range(template.Kind, 0).Start);

    static T Target<T>(GameSession? live, AccountOwners owners, ICharacterRepository repository, DevelopmentRequest request, bool refuseSelected,
        Func<AccountOwners.Lease, Character, T> action) =>
        live is not null ? live.WithDevelopmentOwner(request.Name, refuseSelected, action) :
            WithLease(owners, request.Account, lease => action(lease, LoadByName(repository, lease, request.Name)));

    static Character Commit(GameSession? live, AccountOwners owners, ICharacterRepository repository, DevelopmentRequest request, bool refuseSelected,
        Func<Character, InventoryResult> plan, string rejected) =>
        live is not null ? live.CommitDevelopmentInventory(request.Name, refuseSelected, plan, rejected) : WithLease(owners, request.Account, lease =>
        {
            var character = LoadByName(repository, lease, request.Name);
            var result = plan(character);
            if (!result.Accepted) throw new InvalidDataException(rejected);
            return InventoryService.Commit(repository, lease, character, result);
        });

    static InventoryResult DeletePlan(InventorySnapshot before, IEnumerable<(InventoryItem? Item, long Uid)> targets)
    {
        var after = before;
        foreach (var (item, uid) in targets)
        {
            if (item is null || item.Uid != uid) return InventoryResult.Reject();
            var result = InventoryDeleteService.Plan(after, item.Position, item.Core.ItemId, item.Core.Count);
            if (!result.Accepted) return result;
            after = result.Plan!.After;
        }
        return new(true, 0, new InventoryPlan(before, after, "gm_delete"));
    }

    static string DeliverMail(MailboxService? mailbox, DevelopmentRequest request, IEnumerable<InventoryItem> items, string text, DateTimeOffset now)
    {
        var service = mailbox ?? throw new InvalidOperationException("邮件服务未装配");
        var recipient = service.FindRecipient(request.Name);
        if (recipient is null || recipient.Account != request.Account)
            throw new InvalidDataException("角色不存在或不属于该账号");
        var attachments = items.ToImmutableArray();
        long created = now.ToUnixTimeSeconds();
        long expires = Math.Min(created + 15 * 86400, attachments.Where(item => item.ExpireTime > 0)
            .Select(item => (long)item.ExpireTime).DefaultIfEmpty(long.MaxValue).Min());
        var result = service.Deliver(new("gm:" + Guid.NewGuid().ToString("N"), recipient, "开发GM", text, 0, attachments, created, expires));
        if (!result.Accepted)
            throw new InvalidDataException("无法通过实体邮件发放：" + result.Error);
        return Ok(new { target = "mail", name = recipient.Name, mail = result.MailId, attachments = attachments.Length });
    }

    static string DescribeWallet(Character character, string? message = null)
    {
        var inventory = character.Inventory;
        return Ok(new
        {
            name = character.Name, rev = character.Revision, walletRevision = inventory.AccountWallet.Revision, gold = inventory.Gold, message,
            // 黑钻到期时间取账号钱包契约 56；GM 网页据此显示开通状态。
            blackDiamondExpiresAt = character.BlackDiamondExpiresAt,
            blackDiamond = character.BlackDiamondExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            vault = new { capacity = (int)inventory.AccountCargo.Capacity, gold = inventory.AccountCargo.Gold },
            currencies = Currencies.Select(row => new
            {
                key = row.Key, label = row.Label, account = CurrencyWallet.IsAccount(row.Kind), amount = CurrencyWallet.Get(inventory, row.Kind)
            })
        });
    }

    static readonly Dictionary<string, HashSet<string>> ItemGroups = new()
    {
        ["equip"] = new(["weapon", "coat", "shoulder", "pants", "shoes", "waist", "amulet", "wrist", "ring", "earring", "support", "magic stone", "support weapon", "title name", "name tag"], StringComparer.OrdinalIgnoreCase),
        ["use"] = new(["waste", "unlimited waste", "waste instant item", "booster", "booster selection", "booster random", "cera booster", "cera package", "usable cera package", "enchant waste", "dye", "throw", "teleport potion", "npc teleport potion", "expert town potion", "contract", "coupon", "disguise", "random disguise", "quiz item", "etc", "unlimited etc", "only effect", "virtual", "town and dungeon", "unlimited town and dungeon"], StringComparer.OrdinalIgnoreCase),
        ["mat"] = new(["material", "material instant item"], StringComparer.OrdinalIgnoreCase),
        ["prof"] = new(["material expert job"], StringComparer.OrdinalIgnoreCase),
        ["quest"] = new(["quest", "quest receive", "stackable"], StringComparer.OrdinalIgnoreCase),
        ["avatar"] = new(["hat avatar", "hair avatar", "face avatar", "coat avatar", "breast avatar", "waist avatar", "pants avatar", "shoes avatar", "skin avatar", "aurora avatar", "weapon avatar"], StringComparer.OrdinalIgnoreCase),
        ["emblem"] = new(["avatar emblem"], StringComparer.OrdinalIgnoreCase),
        ["pet"] = new(["creature"], StringComparer.OrdinalIgnoreCase),
        ["peteq"] = new(["artifact red", "artifact blue", "artifact green"], StringComparer.OrdinalIgnoreCase),
        ["petuse"] = new(["feed", "creature expitem"], StringComparer.OrdinalIgnoreCase),
        ["talisman"] = new(["talisman"], StringComparer.OrdinalIgnoreCase),
        ["rune"] = new(["rune", "charm"], StringComparer.OrdinalIgnoreCase),
        ["medal"] = new(["flag"], StringComparer.OrdinalIgnoreCase),
        ["gem"] = new(["flag gem"], StringComparer.OrdinalIgnoreCase),
    };

    static string SearchItems(DevelopmentCatalog catalog, ImmutableArray<string> args, bool advanced)
    {
        int keywordAt = advanced ? 8 : 5;
        string? stat = null, skillJob = null; string period = "*";
        if (advanced)
        {
            stat = args[5] == "*" ? null : args[5].Replace('_', ' ');
            if (stat is not null && !GmItemStatSpecs.Table.Values.Any(spec => spec.Key == stat)) throw new InvalidDataException("词条键无效: " + args[5]);
            period = args[6];
            if (period is not ("*" or "none" or "rel" or "abs" or "any")) throw new InvalidDataException("期限须为 *、none、rel、abs 或 any");
            skillJob = args[7] == "*" ? null : args[7].Replace('_', ' ').ToLowerInvariant();
            if (skillJob is not (null or "all") && !DevelopmentCatalog.JobKnown(skillJob)) throw new InvalidDataException("技能职业无效: " + args[7]);
        }
        var attributes = advanced && (stat is not null || period != "*" || skillJob is not null) ? catalog.Attributes : null;
        string[] offsetArg = args[0].Split('/');
        int offset = (int)Number(offsetArg[0], 1_000_000), pageSize = offsetArg.Length > 1 ? (int)Math.Clamp(Number(offsetArg[1], 200), 1, 200) : 50, minimum = (int)Number(args[1], 255), maximum = (int)Number(args[2], 255);
        int? rarity = args[3] == "*" ? null : (int)Number(args[3], 99);
        string? tag = args[4] == "*" ? null : args[4].Replace('_', ' ');
        bool listSets = tag == "sets";
        if (listSets) tag = null;
        string? group = null;
        if (tag is not null && tag.StartsWith("g:", StringComparison.Ordinal))
        {
            group = tag[2..];
            if (!ItemGroups.ContainsKey(group)) throw new InvalidDataException("分类无效");
            tag = null;
        }
        int? setIndex = null;
        bool setOnly = false;
        if (tag is not null && tag.StartsWith("set:", StringComparison.Ordinal))
        {
            if (tag == "set:*") setOnly = true;
            else setIndex = (int)Number(tag[4..], 1_000_000);
            tag = null;
        }
        int? wearer = null, branch = null;
        var words = new List<string>();
        var options = new SearchOptions();
        for (int i = keywordAt; i < args.Length; i++)
        {
            if (args[i].StartsWith("job:", StringComparison.Ordinal))
                wearer = args[i][4..] == "*" ? null : (int)Number(args[i][4..], 15);
            else if (args[i].StartsWith("grow:", StringComparison.Ordinal))
                branch = args[i][5..] == "*" ? null : (int)Number(args[i][5..], 15);
            else if (!options.TryParse(args[i])) words.Add(args[i]);
        }
        if (options.Tags is not null && (tag is not null || group is not null || setIndex is not null || setOnly || listSets))
            throw new InvalidDataException("tags: 不能与单个分类同时使用");
        string keyword = string.Join(' ', words);
        // 关键字是正整数时才按 ID 匹配；否则 TryParse 失败留下的 0 会把 ID 0（金币）混进任何中文关键字的结果。
        bool byId = int.TryParse(keyword, NumberStyles.None, CultureInfo.InvariantCulture, out int id) && id > 0;
        // 没选角色时不套职业或分支。通用职业位和没写分支的物品始终保留。
        bool Fits(DevelopmentCatalog.ItemEntry row) =>
            (wearer is not { } job || (row.Jobs & (1 << job)) != 0) &&
            (branch is not { } value || (row.Grows & (1 << value)) != 0);
        var rows = catalog.Items.Where(row => row.MinLevel >= minimum && (maximum == 0 || row.MinLevel <= maximum) &&
            (rarity is null || row.Rarity == rarity) && (tag is null || row.Tag == tag) &&
            (group is null || ItemGroups[group].Contains(row.Tag)) &&
            (setIndex is null || row.SetIndex == setIndex) && (!setOnly || row.SetIndex > 0) && (!listSets || row.SetIndex > 0) &&
            Fits(row) && options.Matches(row) &&
            (keyword.Length == 0 || (byId && row.Id == id) || row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) || (listSets && row.SetIndex.ToString(CultureInfo.InvariantCulture) == keyword)) &&
            (attributes is null || AttributeMatches(attributes.GetValueOrDefault(row.Id), stat, period, skillJob))).ToArray();
        rows = options.Sort(rows);
        if (listSets)
        {
            var groups = catalog.Items.Where(row => row.SetIndex > 0 && Fits(row)).GroupBy(row => row.SetIndex);
            if (keyword.Length > 0)
                groups = groups.Where(group => group.Key.ToString(CultureInfo.InvariantCulture) == keyword || group.Any(row => (byId && row.Id == id) || row.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            var pageGroups = groups.OrderBy(group => group.Key).ToArray();
            return Ok(new
            {
                total = pageGroups.Length, offset, pageSize, page = offset / pageSize + 1,
                items = pageGroups.Skip(offset).Take(pageSize).Select(group =>
                {
                    var sample = group.Where(Fits).OrderBy(row => row.Id).First();
                    return new { id = sample.Id, name = sample.Name, equipment = sample.Equipment, tag = sample.Tag, minLevel = sample.MinLevel, rarity = sample.Rarity, set = group.Key, pieces = group.Where(Fits).Select(row => row.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() };
                })
            });
        }
        return Ok(new
        {
            total = rows.Length, offset, pageSize, page = offset / pageSize + 1,
            // 回显本次生效的扩展筛选；旧服务端没有这个字段，客户端据此判断能否用 tags/kind/sub/real/sort。
            options = options.Echo(),
            items = rows.Skip(offset).Take(pageSize).Select(row => new
            {
                id = row.Id, name = row.Name, equipment = row.Equipment, tag = row.Tag, minLevel = row.MinLevel, rarity = row.Rarity, set = row.SetIndex, sub = row.Sub
            })
        });
    }

    // item-search / item-filter 关键字区的扩展选项（与 job:/grow: 相同写法，空格写成 _）；不写时行为与旧版完全一致。
    //   tags:<标签>[@e|@s],…  多个分类取并集；@e 只要装备、@s 只要道具；也可写 g:<分组>。
    //   kind:e|s               只要装备 / 只要道具。
    //   sub:<武器子类>,…       武器子类（PVF 路径 weapon/ 后的目录名），多个取并集。
    //   real:1                 隐藏没有名字或名字为 name_数字 的占位模板。
    //   sort:id|-id|level|-level|rarity|-rarity|name   排序；不写时按目录顺序（equipment.lst 后接 stackable.lst）。
    internal sealed class SearchOptions
    {
        public List<(string Tag, bool? Equipment, string? Group)>? Tags { get; private set; }
        public bool? Equipment { get; private set; }
        public HashSet<string>? Subs { get; private set; }
        public bool Real { get; private set; }
        public string? Order { get; private set; }

        public bool TryParse(string token)
        {
            int colon = token.IndexOf(':');
            if (colon <= 0) return false;
            string key = token[..colon], value = token[(colon + 1)..];
            switch (key)
            {
                case "tags":
                    if (Tags is not null) throw new InvalidDataException("tags: 重复");
                    var list = new List<(string, bool?, string?)>();
                    foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string name = part; bool? equipment = null;
                        if (name.EndsWith("@e", StringComparison.Ordinal)) { equipment = true; name = name[..^2]; }
                        else if (name.EndsWith("@s", StringComparison.Ordinal)) { equipment = false; name = name[..^2]; }
                        name = name.Replace('_', ' ').Trim();
                        if (name.Length is 0 or > 80) throw new InvalidDataException("tags: 分类无效");
                        if (name.StartsWith("g:", StringComparison.Ordinal))
                        {
                            if (!ItemGroups.ContainsKey(name[2..])) throw new InvalidDataException("tags: 分组无效: " + name);
                            list.Add(("", equipment, name[2..]));
                        }
                        else list.Add((name.ToLowerInvariant(), equipment, null));
                    }
                    if (list.Count is 0 or > 64) throw new InvalidDataException("tags: 须为 1–64 个分类");
                    Tags = list;
                    return true;
                case "kind":
                    if (Equipment is not null) throw new InvalidDataException("kind: 重复");
                    Equipment = value switch { "e" or "equipment" => true, "s" or "stackable" => false, _ => throw new InvalidDataException("kind: 须为 e 或 s") };
                    return true;
                case "sub":
                    if (Subs is not null) throw new InvalidDataException("sub: 重复");
                    var subs = value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Replace('_', ' ').Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
                    if (subs.Count is 0 or > 64 || subs.Any(x => x.Length > 40)) throw new InvalidDataException("sub: 武器子类无效");
                    Subs = subs;
                    return true;
                case "real":
                    Real = value switch { "1" or "true" => true, "0" or "false" => false, _ => throw new InvalidDataException("real: 须为 1 或 0") };
                    return true;
                case "sort":
                    if (value is not ("id" or "-id" or "level" or "-level" or "rarity" or "-rarity" or "name")) throw new InvalidDataException("sort: 须为 id、-id、level、-level、rarity、-rarity 或 name");
                    Order = value;
                    return true;
                default:
                    return false;
            }
        }

        public bool Matches(DevelopmentCatalog.ItemEntry row)
        {
            if (Equipment is { } equipment && row.Equipment != equipment) return false;
            if (Real && row.Placeholder) return false;
            if (Subs is not null && !Subs.Contains(row.Sub)) return false;
            if (Tags is null) return true;
            foreach (var (tag, wanted, group) in Tags)
                if ((wanted is null || wanted == row.Equipment) && (group is null ? row.Tag == tag : ItemGroups[group].Contains(row.Tag)))
                    return true;
            return false;
        }

        public DevelopmentCatalog.ItemEntry[] Sort(DevelopmentCatalog.ItemEntry[] rows) => Order switch
        {
            "id" => [.. rows.OrderBy(row => row.Id)],
            "-id" => [.. rows.OrderByDescending(row => row.Id)],
            "level" => [.. rows.OrderBy(row => row.MinLevel).ThenBy(row => row.Id)],
            "-level" => [.. rows.OrderByDescending(row => row.MinLevel).ThenBy(row => row.Id)],
            "rarity" => [.. rows.OrderBy(row => row.Rarity).ThenBy(row => row.Id)],
            "-rarity" => [.. rows.OrderByDescending(row => row.Rarity).ThenBy(row => row.Id)],
            "name" => [.. rows.OrderBy(row => row.Name, StringComparer.Ordinal).ThenBy(row => row.Id)],
            _ => rows
        };

        public object Echo() => new
        {
            tags = Tags?.Select(t => (t.Group is null ? t.Tag : "g:" + t.Group) + (t.Equipment switch { true => "@e", false => "@s", _ => "" })).ToArray(),
            kind = Equipment switch { true => "e", false => "s", _ => null },
            sub = Subs?.Order(StringComparer.Ordinal).ToArray(),
            real = Real,
            sort = Order
        };
    }

    static bool AttributeMatches(GmItemAttributes.Attrs? attrs, string? stat, string period, string? skillJob)
    {
        bool periodOk = period switch
        {
            "*" => true, "none" => attrs is not { Period: > 0 }, "rel" => attrs?.Period == 1, "abs" => attrs?.Period == 2, "any" => attrs is { Period: > 0 }, _ => false
        };
        if (!periodOk) return false;
        if (stat is not null && attrs?.Stats.Contains(stat) != true) return false;
        if (skillJob is null) return true;
        return attrs is not null && (skillJob == "all" ? attrs.AllSkill : attrs.AllSkill || attrs.SkillJobs.Contains(skillJob));
    }

    // 套装按 part set index 汇总当前职业可用的装备；同部位多件时取编号最小者，保证一套每部位一件。
    static ItemTemplate[] SetMembers(DevelopmentCatalog catalog, int index, byte job, byte branch)
    {
        if (index <= 0) throw new InvalidDataException("套装编号无效");
        var members = catalog.Items.Where(row => row.Equipment && row.SetIndex == index && (row.Jobs & (1 << job)) != 0 && (row.Grows & (1 << branch)) != 0)
            .Select(row => catalog.Resources.Items.Get(row.Id)).GroupBy(template => template.EquipmentSlot)
            .Select(group => group.OrderBy(template => template.Id).First()).OrderBy(template => template.EquipmentSlot).ToArray();
        if (members.Length is < 2 or > 20) throw new InvalidDataException("该套装对当前职业没有2–20件可发放的部位");
        return members;
    }

    static IReadOnlyList<int> PrerequisiteGroup(DevelopmentCatalog catalog, StoryQuest quest, byte job)
    {
        var groups = quest.Definition?.PrerequisiteGroups ?? [];
        if (groups.IsDefaultOrEmpty) return quest.Prerequisites;
        // 前置任务分职业声明时，选全部成员都适用当前职业的组；没有完全匹配时退回第一组。
        var match = groups.FirstOrDefault(group => group.All(id => !catalog.Quests.TryGet(id, out var pre) || MatchesJob(pre, job)));
        return match.IsDefault ? groups[0] : match;
    }

    static bool MatchesJob(StoryQuest quest, byte job) => quest.Definition?.Job is not { Length: > 0 } tag ||
        tag.Equals("[all]", StringComparison.OrdinalIgnoreCase) || tag.Equals(DevelopmentCatalog.JobTag(job), StringComparison.OrdinalIgnoreCase);

    // 库列表用的身份条件。脚本 target 的觉醒 0/1 对应角色阶段 1/2；转职任务不写分支，名字里的分支名才是锁。
    internal static bool QuestVisible(StoryQuest quest, byte job, byte growType,
        DfoServer.GameWorld.Characters.CharacterGrowthOptions? options = null, string? name = null)
    {
        if (job > 15) return false;
        var definition = quest.Definition;
        if (definition is null) return true;
        if (!MatchesJob(quest, job)) return false;
        int branch = growType & 15, awakening = growType >> 4;
        if (definition.TargetCharacters.Length != 0 && !definition.TargetCharacters.Any(target => TargetFits(target, job, branch, awakening)))
            return false;
        if (definition.NonTargetCharacters.Any(target => TargetFits(target, job, branch, awakening)))
            return false;
        if (definition.GrowType is >= 1 and <= 15 && definition.JobChange is not (10 or 20) && definition.GrowType != branch)
            return false;
        if (definition.JobChange == 2 && awakening != 1 || definition.JobChange == 3 && awakening != 2)
            return false;
        return options is null || name is not { Length: > 0 } || GrowthNameFits(options, name, branch, awakening);
    }

    static bool TargetFits(QuestTargetCharacter target, byte job, int branch, int awakening) =>
        (target.Job.Equals("[all]", StringComparison.OrdinalIgnoreCase) || target.Job.Equals(DevelopmentCatalog.JobTag(job), StringComparison.OrdinalIgnoreCase)) &&
        (target.GrowType < 1 || target.GrowType == branch) && (target.Awakening < 0 || target.Awakening + 1 == awakening);

    static bool GrowthNameFits(DfoServer.GameWorld.Characters.CharacterGrowthOptions options, string name, int branch, int awakening)
    {
        foreach (var option in options.GrowTypes)
        {
            if (option.Value == branch) continue;
            if (GrowthNameIs(name, "转职 - " + option.Label)) return false;
            if (option.Awakenings.Any(label => GrowthNameIs(name, "觉醒 - " + label))) return false;
        }
        var current = options.GrowTypes.SingleOrDefault(option => option.Value == branch);
        if (current is null) return true;
        if (awakening != 1 && current.Awakenings.Count >= 1 && GrowthNameIs(name, "觉醒 - " + current.Awakenings[0])) return false;
        if (awakening != 2 && current.Awakenings.Count >= 2 && GrowthNameIs(name, "觉醒 - " + current.Awakenings[1])) return false;
        return true;
    }

    static bool GrowthNameIs(string name, string expected) => name.Equals(expected, StringComparison.Ordinal) ||
        name.StartsWith(expected, StringComparison.Ordinal) && (name.Length == expected.Length || !char.IsLetterOrDigit(name[expected.Length]));

    // 任务分类依据 PVF 任务脚本的 [grade] 字段（原样查证：epic/side/sub/common unique/daily…）；客户端任务界面的分类名称未在资源里找到映射表，
    // 因此按 grade 归并为下列分类，无法归类的统一放“其他”。成就与称号类任务由称号簿处理，这里只读。
    internal static readonly (string Key, string Label)[] QuestCategories =
        [("main", "主线"), ("side", "支线"), ("normal", "普通"), ("daily", "每日"), ("repeat", "重复"), ("achievement", "成就"), ("title", "称号"), ("other", "其他")];
    internal static string QuestCategory(string? grade) => (grade ?? "").Trim('[', ']').Trim().ToLowerInvariant() switch
    {
        "epic" => "main",
        "side" or "sub" => "side",
        "common unique" => "normal",
        "daily" or "daily mission" or "auto daily mission" or "special daily" => "daily",
        "normaly repeat" => "repeat",
        "achievement" => "achievement",
        "title" => "title",
        _ => "other"
    };
    // 每日与 [normaly repeat] 完成后会挡住再接；全部完成不能把它们写成 daily_period=0 的永久完成。
    internal static bool CyclicQuestGrade(string? grade) => QuestCategory(grade) is "daily" or "repeat";

    static string SearchQuests(DevelopmentCatalog catalog, Character character, ImmutableArray<string> args)
    {
        string category = "*", status = "*", jobMode = "mine"; int page = 1, size = 50;
        var words = new List<string>();
        foreach (string arg in args)
        {
            if (arg.StartsWith("cat:", StringComparison.Ordinal)) category = arg[4..];
            else if (arg.StartsWith("st:", StringComparison.Ordinal)) status = arg[3..];
            else if (arg.StartsWith("job:", StringComparison.Ordinal)) jobMode = arg[4..];
            else if (arg.StartsWith("page:", StringComparison.Ordinal)) page = (int)Math.Clamp(Number(arg[5..], 100000), 1, 100000);
            else if (arg.StartsWith("size:", StringComparison.Ordinal)) size = (int)Math.Clamp(Number(arg[5..], 500), 1, 500);
            else words.Add(arg);
        }
        if (category != "*" && !QuestCategories.Any(row => row.Key == category)) throw new InvalidDataException("任务分类无效: " + category);
        if (status is not ("*" or "todo" or "active" or "done")) throw new InvalidDataException("任务状态无效: " + status);
        string keyword = string.Join(' ', words);
        var names = catalog.QuestNames;
        int.TryParse(keyword, NumberStyles.None, CultureInfo.InvariantCulture, out int id);
        var completed = character.CompletedQuests.ToHashSet();
        var active = character.ActiveMainQuests.Select(quest => (int)quest.QuestId).ToHashSet();
        string StatusOf(int questId) => completed.Contains(questId) ? "done" : active.Contains(questId) ? "active" : "todo";
        var growth = jobMode == "any" ? null : catalog.GrowthOptions(character.Job);
        var matched = catalog.Quests.All.Where(quest => (jobMode == "any" || QuestVisible(quest, character.Job, character.GrowType, growth, names.GetValueOrDefault(quest.Id))) &&
                (keyword.Length == 0 || quest.Id == id || catalog.DisplayQuestName(quest.Id).Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(quest => quest.MinimumLevel).ThenBy(quest => quest.Id).ToArray();
        var byCategory = QuestCategories.Select(row => new { key = row.Key, label = row.Label, count = matched.Count(quest => QuestCategory(quest.Grade) == row.Key) }).ToArray();
        var scoped = matched.Where(quest => category == "*" || QuestCategory(quest.Grade) == category).ToArray();
        var statusCounts = new { todo = scoped.Count(quest => StatusOf(quest.Id) == "todo"), active = scoped.Count(quest => StatusOf(quest.Id) == "active"), done = scoped.Count(quest => StatusOf(quest.Id) == "done") };
        var rows = scoped.Where(quest => status == "*" || StatusOf(quest.Id) == status).ToArray();
        int pages = Math.Max(1, (rows.Length + size - 1) / size);
        page = Math.Min(page, pages);
        return Ok(new
        {
            total = rows.Length, matched = matched.Length, page, size, pages, categories = byCategory, statusCounts,
            // 当前筛选范围内可由任务写入的编号（不含成就/称号/每日/重复），供前端“全选当前筛选/全部完成”跨页使用。
            ids = rows.Where(quest => QuestCategory(quest.Grade) is not ("achievement" or "title" or "daily" or "repeat")).Select(quest => quest.Id).Take(5000),
            quests = rows.Skip((page - 1) * size).Take(size).Select(quest => new
            {
                id = quest.Id, name = catalog.DisplayQuestName(quest.Id), grade = quest.Grade?.Trim('[', ']') ?? "",
                category = QuestCategory(quest.Grade), minLevel = quest.MinimumLevel, status = StatusOf(quest.Id),
                pre = PrerequisiteGroup(catalog, quest, character.Job).Select(pre => new
                    { id = pre, name = names.GetValueOrDefault(pre, "任务 " + pre), done = completed.Contains(pre) })
            })
        });
    }

    // 转职或觉醒成功之后补当前分支应完成的任务。只加记录，不取消旧分支已完成的任务，也不动主线。
    static (Character Character, string Note) FinishGrowthQuests(ICharacterRepository repository, AccountOwners owners, GameSession? live,
        DevelopmentRequest request, CharacterProgressReset progress, DevelopmentCatalog catalog, Character character)
    {
        ImmutableDictionary<int, string> names;
        try { names = catalog.QuestNames; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("正在后台构建", StringComparison.Ordinal))
        {
            return (character, " 任务名称索引还在构建，转职已写入，对应任务未补，请稍后再执行一次相同转职。");
        }
        try
        {
            var option = progress.GetOptions(character.Job).GrowTypes.SingleOrDefault(value => value.Value == (character.GrowType & 15));
            if (option is null) return (character, "");
            var ids = GrowthQuestIds(catalog.Quests.All, names, character.Job, character.GrowType, option.Label, option.Awakenings);
            var missing = ids.Where(id => !character.CompletedQuests.Contains(id)).ToArray();
            if (ids.Length == 0) return (character, " 未在任务脚本中找到当前分支的转职或觉醒任务。");
            if (missing.Length == 0) return (character, " 对应转职与觉醒任务已在完成列表。");
            var edit = new CharacterProgressEdit(CharacterProgressField.QuestAdd, request.Account, request.Name, missing[0], 0) { Quests = [.. missing] };
            edit.Validate();
            var updated = live is not null ? live.UpdateProgress(edit) : WithLease(owners, request.Account, lease => repository.UpdateProgress(lease, edit));
            return (updated, " 已补转职/觉醒任务 " + missing.Length + " 个。");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return (character, " 转职已写入，补任务失败：" + ex.Message);
        }
    }

    internal static int[] GrowthQuestIds(IEnumerable<StoryQuest> quests, IReadOnlyDictionary<int, string> names,
        byte job, byte growType, string branchLabel, IReadOnlyList<string> awakenings)
    {
        int branch = growType & 15, stage = growType >> 4;
        var wanted = new List<string> { "转职 - " + branchLabel };
        if (stage >= 1 && awakenings.Count >= 1) wanted.Add("觉醒 - " + awakenings[0]);
        if (stage >= 2 && awakenings.Count >= 2) wanted.Add("觉醒 - " + awakenings[1]);
        bool Named(string name, string expected) => name.Equals(expected, StringComparison.Ordinal) ||
            name.StartsWith(expected, StringComparison.Ordinal) && (name.Length == expected.Length || !char.IsLetterOrDigit(name[expected.Length]));
        bool Later(string name) =>
            stage < 1 && awakenings.Count >= 1 && Named(name, "觉醒 - " + awakenings[0]) ||
            stage < 2 && awakenings.Count >= 2 && Named(name, "觉醒 - " + awakenings[1]);
        var byId = quests.ToDictionary(quest => quest.Id);
        var chosen = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var quest in byId.Values)
        {
            if (!names.TryGetValue(quest.Id, out var name) || !wanted.Any(prefix => Named(name, prefix)) || !GrowthQuestBelongs(quest, job, branch)) continue;
            if (chosen.Add(quest.Id)) queue.Enqueue(quest.Id);
        }
        while (queue.TryDequeue(out int id))
        {
            if (!byId.TryGetValue(id, out var quest)) continue;
            var groups = quest.Definition?.PrerequisiteGroups ?? [];
            IEnumerable<int> pres = groups.IsDefaultOrEmpty ? quest.Prerequisites : groups.SelectMany(group => group);
            foreach (int pre in pres.Distinct())
            {
                if (!byId.TryGetValue(pre, out var prior) || !names.TryGetValue(pre, out var pname)) continue;
                if (!pname.StartsWith("转职 - ", StringComparison.Ordinal) && !pname.StartsWith("觉醒 - ", StringComparison.Ordinal) || Later(pname)) continue;
                if (!GrowthQuestBelongs(prior, job, branch) || !chosen.Add(pre)) continue;
                queue.Enqueue(pre);
            }
        }
        return [.. chosen.Order()];
    }

    static bool GrowthQuestBelongs(StoryQuest quest, byte job, int branch)
    {
        var definition = quest.Definition;
        if (definition is null) return true;
        if (definition.Job is { Length: > 0 } tag && !tag.Equals("[all]", StringComparison.OrdinalIgnoreCase) &&
            !tag.Equals(DevelopmentCatalog.JobTag(job), StringComparison.OrdinalIgnoreCase))
            return false;
        if (definition.TargetCharacters.Length != 0 && !definition.TargetCharacters.Any(target =>
            target.Job.Equals("[all]", StringComparison.OrdinalIgnoreCase) || target.Job.Equals(DevelopmentCatalog.JobTag(job), StringComparison.OrdinalIgnoreCase)))
            return false;
        return definition.GrowType is not (>= 1 and <= 15) || definition.JobChange is 10 or 20 || definition.GrowType == branch;
    }

    static string CompleteQuests(ICharacterRepository repository, AccountOwners owners, GameSession? live, DevelopmentRequest request,
        ImmutableArray<int> ids, int[] before, DevelopmentCatalog catalog)
    {
        var kept = ids.Where(id => !catalog.Quests.TryGet(id, out var quest) || !CyclicQuestGrade(quest.Grade)).ToImmutableArray();
        int skipped = ids.Length - kept.Length;
        if (kept.Length == 0)
            throw new InvalidDataException("这批任务都是每日或重复任务，批量完成不会永久标记它们");
        var edit = new CharacterProgressEdit(CharacterProgressField.QuestAdd, request.Account, request.Name, kept[0], 0) { Quests = kept };
        edit.Validate();
        var updated = live is not null ? live.UpdateProgress(edit) : WithLease(owners, request.Account, lease => repository.UpdateProgress(lease, edit));
        return Ok(new { name = updated.Name, rev = updated.Revision, chain = kept.Length, added = kept.Except(before).Count(),
            completed = updated.CompletedQuests.Length, skipped });
    }
}
