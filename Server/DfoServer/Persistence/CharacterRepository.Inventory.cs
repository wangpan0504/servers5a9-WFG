using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Currency;
using DfoServer.Game.Skills;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    /// <summary>
    /// 挂牌表 SQL。与角色库指向同一个数据库文件，因此能与背包扣减共用同一个写事务。
    /// 与邮箱同一形态：接口由本仓储实现、SQL 委托给同层的专用仓储，避免两个仓储互相构造。
    /// </summary>
    readonly AuctionListingRepository auctionListings = new(path);

    /// <summary>
    /// 上架提交（<see cref="IAuctionListingStore"/> 的写路径）：**背包扣减与新挂牌在同一个外层事务里**。
    ///
    /// 为什么必须在这里（而不是先调 <see cref="CommitInventory"/> 再单独插一行）：SQLite 同一个文件
    /// 只有一个 writer，分两次提交会出现「背包扣了、挂牌没写上」的半成品，而拍卖行上架是**会改背包资产**
    /// 的操作，半成品只能靠人工补偿。参考实现把上架写成「扣道具 + 写挂牌 + 审计」同一事务，这里沿用同一口径：
    /// 本方法拥有事务，<see cref="CommitInventoryInTransaction"/> 只参与、不提交；挂牌插入失败时抛异常，
    /// 事务未提交 ⇒ 背包、审计、revision 与挂牌表一起回滚。
    ///
    /// owner / revision / 背包快照的复核由 <see cref="CommitInventoryInTransaction"/> 在写事务内完成
    /// （它内部重新 `Require` 角色并比对完整背包快照），因此这里只按提交后的角色补挂牌明细，
    /// 不重复维护第二套一致性检查。
    /// </summary>
    public AuctionListingRegisterResult RegisterListing(AccountOwners.Lease owner, Character expected, AuctionRegisterPlan plan) =>
        owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.RegisterListing");
            using var transaction = write.Transaction;
            var character = CommitInventoryInTransaction(db, transaction, owner, expected, plan.Inventory);
            AuctionListingRepository.InsertListing(db, transaction, plan, character);
            transaction.Commit();
            return new AuctionListingRegisterResult(AuctionRegisterError.None, character);
        });

    /// <summary>
    /// 取消上架提交（<see cref="IAuctionListingStore"/> 的另一条写路径）：**道具还回背包 + 挂牌置为已下架 +
    /// 背包审计在同一个外层事务里**。与 <see cref="RegisterListing"/> 反方向、同一口径：
    ///   ① 先生成背包写入（发放实例走既有的 <c>CommitInventoryInTransaction</c>，含 revision 复核与完整背包快照比对）；
    ///   ② 再在**同一事务**内复核「这行仍是当前角色的、且仍在售」并置为已下架；
    ///   ③ 任一步失败都抛异常 ⇒ 事务不提交 ⇒ **既不会丢道具，也不会出现「挂牌没了但道具没回来」**。
    /// 事务由本方法拥有，两步都只参与、不提交。
    /// </summary>
    public AuctionListingCancelResult CancelListing(AccountOwners.Lease owner, Character expected, AuctionCancelPlan plan) =>
        owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.CancelListing");
            using var transaction = write.Transaction;
            var character = CommitInventoryInTransaction(db, transaction, owner, expected, plan.Inventory);
            AuctionListingRepository.MarkCancelled(db, transaction, plan.ListingId, character.Id);
            transaction.Commit();
            return new AuctionListingCancelResult(AuctionCancelError.None, character);
        });

    /// <summary>
    /// 到期结算提交（**到期未售**）：**挂牌置为已过期 + 一封系统退回邮件（道具 + 退还的保证金）
    /// 在同一个外层事务里**，事务由本方法拥有。
    ///
    /// 与 <see cref="BuyListing"/> 同形的两点：① 只写挂牌行与邮件行，**不改卖家角色行** ——
    /// 保证金进托管邮件（卖家在自己的邮箱里领取），因此没有 revision 依赖、也不需要第二把租约；
    /// ② 幂等判据是条件更新 `state=0 → 3`，影响 0 行就跳过这一行并记原因，绝不重复退钱/退物。
    ///
    /// 批量语义：一行失败（投递被拒）会让**整批**回滚 —— 这批里其它行的状态与邮件一起退回，
    /// 下次访问重试。宁可整批重来，也不留下「状态流转了但邮件没发出去」的半成品。
    /// </summary>
    public AuctionExpiryResult SettleExpiredListings(AccountOwners.Lease owner, Character expected, AuctionExpiryPlan plan) =>
        owner.Execute(() =>
        {
            if (plan.IsEmpty && plan.Skipped.IsDefaultOrEmpty)
                return AuctionExpiryResult.None;
            if (expected.Id != plan.SellerCharacterId)
                throw new InvalidOperationException($"到期结算的卖家 {plan.SellerCharacterId} 与计划不一致（当前角色 {expected.Id}）");
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.SettleExpiredListings");
            using var transaction = write.Transaction;
            var receipts = ImmutableArray.CreateBuilder<AuctionExpiryRow>(plan.Rows.Length);
            foreach (var row in plan.Rows)
            {
                // ① 状态条件更新：0 行 = 这一行已被下架/成交/并发结算 ⇒ 跳过，不发行邮件。
                if (!AuctionListingRepository.TryMarkExpired(db, transaction, row.ListingId, plan.SellerCharacterId))
                {
                    receipts.Add(row with { Delivered = false, Note = "条件更新 state=0→3 影响 0 行：已被下架、成交或并发结算" });
                    continue;
                }
                // ② 退回邮件：收件人就是当前角色（本人），角色行必然存在；投不出去整批回滚。
                var recipient = FindMailRecipientById(db, transaction, plan.SellerCharacterId)
                    ?? throw new InvalidOperationException($"到期退回邮件的收件人角色行不存在：{plan.SellerCharacterId}");
                var (error, mailId) = DeliverMailInTransaction(db, transaction, new MailboxDelivery(row.Mail.Key, recipient,
                    row.Mail.SenderName, row.Mail.Text, row.Mail.Gold, row.Mail.Items, row.Mail.Created, row.Mail.Expires));
                if (error != MailboxError.None)
                    throw new InvalidOperationException($"到期退回邮件投递失败（{error}）：key={row.Mail.Key} 收件人={plan.SellerCharacterId}");
                receipts.Add(row with { Delivered = true, Note = $"mail_id={mailId}（保证金 {row.Mail.Gold} 金币 + 道具已退回）" });
            }
            transaction.Commit();
            return new AuctionExpiryResult(receipts.ToImmutable(), plan.Skipped);
        });

    /// <summary>
    /// 一口价购买提交（<see cref="IAuctionListingStore"/> 的第三条写路径，也是本仓库唯一**跨账号**的写入）：
    /// **买方扣款 + 挂牌置为已成交 + 买方道具托管行 + 卖方金币托管行在同一个外层事务里**。
    ///
    /// 只取**买方**一把租约：卖方不取锁、不改其角色行（他可能在线并持有旧快照，改他的行会让对方下一笔写入
    /// 在 revision 复核处被拒）。卖方所得只能进**托管邮件**，卖方在自己的会话里领取时才落进钱包。
    /// 锁序与事务边界见[并发、事务与响应延迟](../../../../Docs/规范/并发事务与响应延迟.md#跨账号转移拍卖行一口价购买)。
    ///
    /// 四步顺序不能变，任何一步失败都抛异常 ⇒ 事务不提交 ⇒ 买卖双方资产、挂牌状态、审计与托管行一起回退：
    ///   ① 事务内重读挂牌并复核**内容**（一口价 / 数量 / 模板 / 卖家）与过期时间；
    ///   ② 挂牌条件更新 `state=0→1` 且**影响行数必须为 1**（为 0 抛 <see cref="AuctionBuyRaceLostException"/>）；
    ///   ③ 买方扣款走既有背包提交器（revision 复核 + `currency_audit_log` 同一路径，**不写裸 SQL 改金币列**）；
    ///   ④ 托管邮件：买方道具（必须投出去）+ 卖方所得（角色行不存在就**跳过**，绝不触发外键回滚整笔成交）。
    /// ② 在 ③ 之前不是随意的：挂牌行是**先于买方角色**取的资源（见
    /// [并发、事务与响应延迟](../../../../Docs/规范/并发事务与响应延迟.md#锁序) 的锁序
    /// 「挂牌行 → 买方角色 → 托管邮件行」），而且这样「这一行已经被买走」在**任何一笔写之前**
    /// 就能判出来：重复点击/并发第二笔直接整体拒绝，不会先扣一次钱再回滚。
    ///
    /// ★ **道具不进买方背包**：进托管邮件，容量判定发生在领取时。购买时直接入包就必须补发含金币槽的
    /// 背包差量，而客户端 185 回调已本地乐观扣过一次金币，补发会被叠加成「再扣一次」。
    /// </summary>
    public AuctionListingBuyResult BuyListing(AccountOwners.Lease owner, Character expected, AuctionBuyPlan plan) =>
        owner.Execute(() =>
        {
            using var db = database.Open();
            using var write = db.BeginWrite("CharacterRepository.BuyListing");
            using var transaction = write.Transaction;

            // ① 事务内重读挂牌：只复核内容与过期；**状态竞态留给 ② 的条件更新**——
            //    在这里先判一次 state 会让「影响行数必须为 1」那条唯一幂等判据永远走不到。
            if (RejectStaleBuy(AuctionListingRepository.LoadListing(db, transaction, plan.ListingId), plan) is { } reject)
                return reject;

            // ② 挂牌置为已成交（条件更新，影响 0 行即整笔回滚并翻成静默码 0x7e）。
            AuctionListingRepository.MarkSold(db, transaction, plan.ListingId, plan.SettledAtUnix);

            // ③ 买方扣款。
            var character = CommitInventoryInTransaction(db, transaction, owner, expected, plan.Inventory);

            // ④ 托管邮件。
            var custody = ImmutableArray.CreateBuilder<AuctionCustodyReceipt>(plan.Custody.Length);
            foreach (var mail in plan.Custody)
            {
                var recipient = FindMailRecipientById(db, transaction, mail.RecipientCharacterId);
                if (recipient is null)
                {
                    // ⚠ 邮件表有指向 characters(id,account) 的外键：**不能先插再等约束报错**。
                    //    机器人卖家（seller_character_id IS NULL）与「卖方角色行已删除」都是**合法数据**，
                    //    金币不入账 = 系统回收，由日志与「是否生成托管行」本身作为可核对证据。
                    if (mail.Required)
                        throw new InvalidOperationException($"托管收件人角色行不存在：{mail.RecipientCharacterId}（这封不可跳过）");
                    custody.Add(new(mail.Key, mail.RecipientCharacterId, mail.Gold, Delivered: false,
                        "卖方角色行不存在（机器人卖家或已被删除）：金币不入账，成交照常"));
                    continue;
                }
                var (error, mailId) = DeliverMailInTransaction(db, transaction, new MailboxDelivery(mail.Key, recipient,
                    mail.SenderName, mail.Text, mail.Gold, mail.Items, mail.Created, mail.Expires));
                if (error != MailboxError.None)
                    throw new InvalidOperationException($"托管邮件投递失败（{error}）：key={mail.Key} 收件人={mail.RecipientCharacterId}");
                custody.Add(new(mail.Key, mail.RecipientCharacterId, mail.Gold, Delivered: true, $"mail_id={mailId}"));
            }

            transaction.Commit();
            return new AuctionListingBuyResult(AuctionBuyError.None, character) { Custody = custody.ToImmutable() };
        });

    /// <summary>
    /// 购买提交前的事务内**内容**复核：返回 null 表示可以继续成交，否则返回要拒绝的结果（事务随
    /// `using` 回滚，不写任何一行）。
    ///
    /// 两条判据：
    ///   - 行不见了 / 已过期 ⇒ <see cref="AuctionBuyError.RaceLost"/>（静默 `0x7e`：操作前提已经没了）；
    ///   - 价格、数量、模板或卖家与规划不一致 ⇒ <see cref="AuctionBuyError.StalePlan"/>（通用 `0xd2`）：
    ///     成交价一律取库里的值，但**绝不在内容变了之后按旧计划结算**。
    /// </summary>
    static AuctionListingBuyResult? RejectStaleBuy(AuctionListing? listing, AuctionBuyPlan plan)
    {
        if (listing is null)
            return AuctionListingBuyResult.Reject(AuctionBuyError.RaceLost, $"挂牌 {plan.ListingId} 在提交前被删除");
        if (listing.ExpiresAtUnix <= plan.SettledAtUnix)
            return AuctionListingBuyResult.Reject(AuctionBuyError.RaceLost,
                $"挂牌 {plan.ListingId} 在提交前过期（{listing.ExpiresAtUnix} ≤ {plan.SettledAtUnix}）");
        if (listing.BuyoutPrice != plan.UnitPrice || listing.ItemCount != plan.ItemCount ||
            listing.ItemTemplateId != plan.ItemTemplateId || listing.SellerCharacterId != plan.SellerCharacterId ||
            listing.ItemKind != plan.ItemKind || !listing.ItemEntry.AsSpan().SequenceEqual(plan.SourceItemEntry.AsSpan()))
            return AuctionListingBuyResult.Reject(AuctionBuyError.StalePlan,
                $"挂牌 {plan.ListingId} 的内容在规划后变化：单价 {listing.BuyoutPrice}/{plan.UnitPrice}、" +
                $"数量 {listing.ItemCount}/{plan.ItemCount}、模板 {listing.ItemTemplateId}/{plan.ItemTemplateId}、" +
                $"卖家 {listing.SellerCharacterId?.ToString() ?? "NULL"}/{plan.SellerCharacterId?.ToString() ?? "NULL"}");
        return null;
    }

    // —— 只读查询直接委托同层仓储：分类过滤、分页与在售口径由那边一份实现负责 ——

    public AuctionListing? LoadListing(int listingId) => auctionListings.LoadListing(listingId);

    public AuctionSearchPage Search(AuctionSearchFilter filter, long now, int offset, int limit) =>
        auctionListings.Search(filter, now, offset, limit);

    public ImmutableArray<AuctionListing> LoadMatches(AuctionSearchFilter filter, long now) =>
        auctionListings.LoadMatches(filter, now);

    public int CountListings() => auctionListings.CountListings();

    public ImmutableArray<AuctionListing> LoadSellerListings(long sellerCharacterId, long now, bool includeInactive) =>
        auctionListings.LoadSellerListings(sellerCharacterId, now, includeInactive);

    public int CountSellerListings(long sellerCharacterId, long now) => auctionListings.CountSellerListings(sellerCharacterId, now);

    /// <summary>到期结算的输入（该卖家已到期且仍在售的挂牌）：与两条「我的拍卖品」读路径共用同一份在售口径。</summary>
    public ImmutableArray<AuctionListing> LoadExpiredListings(long sellerCharacterId, long now) =>
        sellerCharacterId <= 0 ? [] : auctionListings.LoadExpiredListings(sellerCharacterId, now);

    public void InsertListings(ImmutableArray<AuctionListingDraft> listings) => auctionListings.InsertListings(listings);

    public Character CommitInventory(AccountOwners.Lease owner, Character expected, InventoryPlan plan) => owner.Execute(() =>
    {
        ValidateInventoryPlan(expected, plan);
        if (plan.CharacterPrecondition?.IsExpired() == true)
        {
            // 时间自然推进不能掩盖已陈旧的库存/角色；到期拒绝前仍读真实当前快照。
            using var expiredDb = database.Open();
            using var read = expiredDb.BeginRead();
            ValidateCurrentInventoryPlan(Require(expiredDb, read, owner.Account, expected.Name), expected, plan);
            throw new InventoryQualificationExpiredException();
        }
        if (!plan.Changed)
        {
            // 免费保留源等零资产变化计划仍需验证真实角色与当前期限，不启动writer。
            using var unchangedDb = database.Open();
            using var read = unchangedDb.BeginRead();
            var current = Require(unchangedDb, read, owner.Account, expected.Name);
            ValidateCurrentInventoryPlan(current, expected, plan);
            if (plan.RequiredEvent is { } required && !required.Matches(ServerEventRepository.Read(unchangedDb, read, required.Schedule.EventId)))
                throw new InventoryEventRejectedException();
            if (plan.CharacterPrecondition?.IsExpired() == true) throw new InventoryQualificationExpiredException();
            return expected;
        }
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitInventory");
        using var transaction = write.Transaction;
        var character = CommitInventoryInTransaction(db, transaction, owner, expected, plan);
        var itemRead = ReadInventoryItemRevision(db, transaction, owner.Account, character.Id);
        transaction.Commit();
        RememberInventoryItems(character.Inventory, itemRead);
        return character;
    });

    static void ValidateInventoryPlan(Character expected, InventoryPlan plan)
    {
        if (!SameInventory(expected.Inventory, plan) || plan.CharacterPrecondition?.MatchesIdentity(expected) == false)
            throw new InvalidOperationException("背包规划不是当前快照");
        plan.After.Validate(allocating: true);
        if (plan.UnlockSkillTree && (plan.Reason != "cash_shop_buy" || plan.CharacterPrecondition is null ||
            !expected.Learning.Initialized || expected.Learning.SelectedTree != -1))
            throw new InvalidOperationException("技能方案只能由商城对未开通角色开通");
        if (plan.EquipmentSlotFlagsAfter is not null && plan.Reason == "gm_equipment_slot_flags")
        {
            // 开发GM开扩展装备栏：只在原标志上追加辅助装备/魔法石/耳环位，背包不变，不清除也不开放幻化位。
            int gmSlots = plan.EquipmentSlotFlagsAfter.Value, added = gmSlots & ~expected.EquipmentSlotFlags;
            if (plan.CharacterPrecondition is null || !ReferenceEquals(plan.Before, plan.After) || added == 0 ||
                (added & ~DfoServer.Game.Inventory.Services.InventoryMoveService.ExpansionSlotFlags) != 0 || (gmSlots & expected.EquipmentSlotFlags) != expected.EquipmentSlotFlags)
                throw new InvalidOperationException("GM开栏只能在原角色标志上追加辅助装备、魔法石或耳环位");
        }
        else if (plan.EquipmentSlotFlagsAfter is { } slots)
        {
            byte flag = plan.Reason switch
            {
                "open_creature_skin_slot" => 32,
                "open_aura_skin_slot" => 8,
                _ => 0
            };
            if (flag == 0 || plan.CharacterPrecondition is null ||
                (expected.EquipmentSlotFlags & flag) != 0 || slots != (expected.EquipmentSlotFlags | flag))
                throw new InvalidOperationException("库存开栏只能在原角色资格上增加对应幻化位");
        }
        if (plan.After.AccountWallet.Revision != plan.Before.AccountWallet.Revision || plan.After.AccountCargo.Revision != plan.Before.AccountCargo.Revision ||
            plan.After.EpicPieces.Revision != plan.Before.EpicPieces.Revision)
            throw new InvalidOperationException("业务不能修改账号资产版本");
        // 账号皮肤仓只允许兑换券使用改动；其余业务改动账号归属皮肤属于越权。
        if (plan.AccountSkinsChanged && plan.Reason != "skin_storage_add")
            throw new InvalidOperationException("业务不能修改账号皮肤仓");
    }

    static void ValidateCurrentInventoryPlan(Character current, Character expected, InventoryPlan plan)
    {
        if (current.Id != expected.Id || current.Revision != expected.Revision ||
            !SameInventory(current.Inventory, plan) || plan.CharacterPrecondition?.MatchesIdentity(current) == false ||
            plan.UnlockSkillTree && !SameLearning(current.Learning, expected.Learning))
            throw new InvalidOperationException("角色库存、规划资格或owner/revision已变化");
    }

    /// <summary>
    /// 在已有连接/事务内写背包（含 revision 复核、UID 分配、槽位、detail 与审计）。
    /// **只参与，不提交**：调用方拥有事务（见 <see cref="CommitInventory"/> 与
    /// <see cref="AuctionListingRepository.RegisterListing"/> 的「背包 + 挂牌同事务」）。
    /// 同程序集内可见是为了让同一库文件的其它仓储复用这一份唯一实现，而不是各自另写一套扣减 SQL。
    /// </summary>
    internal Character CommitInventoryInTransaction(SqliteConnection db, SqliteTransaction transaction, AccountOwners.Lease owner,
        Character expected, InventoryPlan plan, IReadOnlySet<long>? retained = null, IReadOnlyDictionary<long, InventoryItem>? incoming = null)
    {
        ValidateInventoryPlan(expected, plan);
        var current = Require(db, transaction, owner.Account, expected.Name, expected.Inventory);
        return CommitInventoryInTransaction(db, transaction, owner, current, expected, plan, retained, incoming);
    }

    // 复用调用方在同一事务内刚读取的完整角色，避免联合提交重复装载。
    Character CommitInventoryInTransaction(SqliteConnection db, SqliteTransaction transaction, AccountOwners.Lease owner,
        Character current, Character expected, InventoryPlan plan, IReadOnlySet<long>? retained = null,
        IReadOnlyDictionary<long, InventoryItem>? incoming = null)
    {
        // GM进度修改不递增revision；库存和规划资格都必须在写事务内重新核对。
        ValidateCurrentInventoryPlan(current, expected, plan);
        if (plan.RequiredEvent is { } requiredEvent &&
            !requiredEvent.Matches(ServerEventRepository.Read(db, transaction, requiredEvent.Schedule.EventId)))
            throw new InventoryEventRejectedException();
        // 礼堂实例是本次选角的临时场景身份，重读资产不能将它丢掉；不写入角色存档。
        current = current with { WeddingHall = expected.WeddingHall };
        if (plan.CharacterPrecondition?.IsExpired() == true)
            throw new InventoryQualificationExpiredException();
        if (!plan.Changed)
            return current;
        CommitGuildHongbaoPurchase(db, transaction, current, plan);
        WriteAccountItemRefills(db, transaction, owner.Account, current.Id, plan.AccountRefillClaims);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE characters SET revision=revision+1,gold=$gold,inventory_expansion=$expansion,avatar_expansion=$avatarExpansion,devil_usage=$devil,daily_booster_usage=$dailyBooster,weapon_skin_state=$weaponSkin,avatar_preset_state=$avatarPreset,npc_favor=$npcFavor,stamina_until=$stamina,equipment_slot_flags=$equipmentSlots
            WHERE id=$id AND account=$account AND revision=$revision AND equipment_slot_flags=$equipmentSlotsBefore;
            """;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.Parameters.AddWithValue("$gold", (long)plan.After.Gold);
        command.Parameters.AddWithValue("$expansion", plan.After.Expansion);
        command.Parameters.AddWithValue("$avatarExpansion", plan.After.AvatarExpansion);
        command.Parameters.AddWithValue("$devil", System.Text.Json.JsonSerializer.Serialize(plan.After.DevilUses));
        command.Parameters.AddWithValue("$dailyBooster", JsonSerializer.Serialize(plan.After.DailyBoosterUses));
        command.Parameters.AddWithValue("$weaponSkin", JsonSerializer.Serialize(plan.After.Skins));
        command.Parameters.AddWithValue("$avatarPreset", JsonSerializer.Serialize(plan.After.AvatarPresets));
        command.Parameters.AddWithValue("$npcFavor", JsonSerializer.Serialize(plan.After.NpcFavor));
        command.Parameters.AddWithValue("$stamina", plan.After.StaminaUntil);
        command.Parameters.AddWithValue("$equipmentSlots", plan.EquipmentSlotFlagsAfter ?? current.EquipmentSlotFlags);
        command.Parameters.AddWithValue("$equipmentSlotsBefore", current.EquipmentSlotFlags);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Character revision or owner changed");
        if (plan.Before.CubeContract != plan.After.CubeContract)
        {
            using var cube = db.CreateCommand();
            cube.Transaction = transaction;
            cube.CommandText = "INSERT INTO cube_premium(charac_no,selected,cube_type) VALUES($id,$selected,$type) " +
                "ON CONFLICT(charac_no) DO UPDATE SET selected=excluded.selected,cube_type=excluded.cube_type";
            cube.Parameters.AddWithValue("$id", expected.Id);
            cube.Parameters.AddWithValue("$selected", plan.After.CubeContract.Selected);
            cube.Parameters.AddWithValue("$type", plan.After.CubeContract.CubeType);
            cube.ExecuteNonQuery();
        }
        var inventory = WriteInventory(db, transaction, owner, expected.Id, plan, plan.After, retained, incoming);
        inventory = WriteWallet(db, transaction, owner, expected.Id, plan, inventory);
        var learning = current.Learning;
        if (plan.Before.AccountWallet.ContractExpiry(27) != inventory.AccountWallet.ContractExpiry(27))
            learning = learning with { ExpertContractExpiresAt = ReadEffectiveExpertContractExpiry(db, expected.Id, transaction) };
        if (plan.UnlockSkillTree)
        {
            learning = learning with { SelectedTree = 0 };
            WriteLearning(db, transaction, current.Id, learning);
        }
        inventory = WriteCargo(db, transaction, owner, expected.Id, plan, inventory);
        inventory = WriteAccountSkins(db, transaction, owner.Account, plan, inventory);
        WriteShopPurchases(db, transaction, owner.Account, expected.Id, plan.Before, inventory);
        WriteItemMakingSkills(db, transaction, expected.Id, owner.Account, plan);
        inventory = WriteEpicPieces(db, transaction, owner, expected.Id, plan.Before, inventory, plan.Reason);
        WriteOverflowMail(db, transaction, owner, current, plan);
        SkillSwitchInventoryState skillSwitch = current.SkillSwitchInventory.Reconcile(inventory);
        if (!skillSwitch.Same(current.SkillSwitchInventory))
            WriteSkillSwitchInventory(db, transaction, current.Id, owner.Account, skillSwitch);
        var active = current.ActiveMainQuests;
        if (quests is not null && !plan.Reason.StartsWith("quest_", StringComparison.Ordinal))
        {
            var refreshed = DfoServer.Game.Quests.QuestEventProgress.Inventory(
                DfoServer.Game.Quests.QuestInventoryProgress.Refresh(active, inventory, quests, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),DfoServer.Game.Quests.QuestEligibility.Completed(current)),
                quests, plan);
            WriteQuestConditions(db, transaction, current.Id, active, refreshed);
            active = refreshed.ToArray();
        }
        if (plan.ExpBonus is { } bonus)
        {
            if (bonus.Permille is <= 0 or > 10000 || bonus.Until <= 0)
                throw new InvalidDataException("秘药经验状态无效");
            using var exp = db.CreateCommand();
            exp.Transaction = transaction;
            exp.CommandText = """
                INSERT INTO character_exp_bonus(character_id,account,until_unix,permille)
                VALUES($id,$account,$until,$permille)
                ON CONFLICT(character_id) DO UPDATE SET until_unix=excluded.until_unix,permille=excluded.permille;
                """;
            exp.Parameters.AddWithValue("$id", expected.Id);
            exp.Parameters.AddWithValue("$account", owner.Account);
            exp.Parameters.AddWithValue("$until", bonus.Until);
            exp.Parameters.AddWithValue("$permille", bonus.Permille);
            exp.ExecuteNonQuery();
        }
        if (!plan.Before.ConsumableStates.SequenceEqual(plan.After.ConsumableStates))
            WriteConsumableStates(db, transaction, owner.Account, current.Id, inventory.ConsumableStates);
        if (!BossSelectionBuff.Same(plan.Before.BossSelectionBuff, plan.After.BossSelectionBuff))
            WriteBossSelectionBuff(db, transaction, owner.Account, current.Id, inventory.BossSelectionBuff);
        if (!plan.Before.ShieldDeck.Same(inventory.ShieldDeck))
            WriteKnightShieldDeck(db, transaction, owner.Account, current.Id, inventory.ShieldDeck);
        if (plan.Before.PrivilegeService != inventory.PrivilegeService)
            WritePrivilegeService(db, transaction, owner.Account, current.Id, inventory.PrivilegeService);
        if (!plan.Before.ServerItems.Same(inventory.ServerItems)) WriteServerItems(db, transaction, current.Id, inventory.ServerItems);
        // 未参与本次库存操作的任务等进度取事务内实读值，不能用旧expected覆盖在线快照。
        return current with { Revision = current.Revision + 1, Inventory = inventory, ActiveMainQuests = active, Learning = learning,
            EquipmentSlotFlags = plan.EquipmentSlotFlagsAfter ?? current.EquipmentSlotFlags, SkillSwitchInventory = skillSwitch,
            ExpBonusUntil = plan.ExpBonus?.Until ?? current.ExpBonusUntil,
            ExpBonusPermille = plan.ExpBonus?.Permille ?? current.ExpBonusPermille };
    }

    static void WriteQuestConditions(SqliteConnection db, SqliteTransaction transaction, ushort characterId,
        IReadOnlyList<ActiveMainQuest> before, IReadOnlyList<ActiveMainQuest> after)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE character_active_main_quests SET condition_state=$scalar WHERE character_id=$id AND slot=$slot AND quest_id=$quest AND acceptance_id=$acceptance AND condition_state=$before";
        foreach (var row in after)
        {
            var old = before.Single(value => value.AcceptanceId == row.AcceptanceId);
            if (old == row) continue;
            if (old.Slot != row.Slot || old.QuestId != row.QuestId) throw new InvalidDataException("库存不能替换任务接取身份");
            command.Parameters.Clear(); command.Parameters.AddWithValue("$id", characterId);
            command.Parameters.AddWithValue("$slot", row.Slot); command.Parameters.AddWithValue("$quest", row.QuestId);
            command.Parameters.AddWithValue("$acceptance", row.AcceptanceId.ToString("N"));
            command.Parameters.AddWithValue("$before", (long)old.ConditionState); command.Parameters.AddWithValue("$scalar", (long)row.ConditionState);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("库存关联任务状态已变化");
        }
    }

    static bool SameInventory(InventorySnapshot left, InventoryPlan plan)
    {
        var right = plan.Before;
        if (ReferenceEquals(left, right))
            return true;
        if (left.PrivilegeService != right.PrivilegeService || !left.ShieldDeck.Same(right.ShieldDeck) || !left.ServerItems.Same(right.ServerItems) || !BossSelectionBuff.Same(left.BossSelectionBuff, right.BossSelectionBuff) || !left.NpcFavor.SequenceEqual(right.NpcFavor) || !left.ItemMakingSkills.Same(right.ItemMakingSkills) || !left.Skins.Same(right.Skins) || !left.AccountSkins.Same(right.AccountSkins) || !left.ConsumableStates.SequenceEqual(right.ConsumableStates) || left.Gold != right.Gold || left.Expansion != right.Expansion || left.CubeContract != right.CubeContract || left.ReviveCoins != right.ReviveCoins ||
            left.WinPoints != right.WinPoints || left.GoldCarryLimit != right.GoldCarryLimit || left.AuctionGoldLimit != right.AuctionGoldLimit ||
            !left.AccountWallet.Same(right.AccountWallet) || left.StaminaUntil != right.StaminaUntil || !left.DevilUses.SequenceEqual(right.DevilUses) || !DailyBoosterUsage.Same(left.DailyBoosterUses, right.DailyBoosterUses) || left.AvatarExpansion != right.AvatarExpansion || left.CargoCapacity != right.CargoCapacity || left.AccountCargo != right.AccountCargo ||
            !ShopPurchaseCount.Same(left.ShopPurchases, right.ShopPurchases) || !left.EpicPieces.Same(right.EpicPieces) ||
            left.Items.Length != right.Items.Length)
            return false;
        if (left.Items.SequenceEqual(right.Items))
            return true;
        // 顺序不同仍按完整实例比较；复用计划索引，不能让重复槽位替代缺失实例。
        var seen = new HashSet<InventorySlot>();
        return left.Items.All(item => seen.Add(item.Position) && item == plan.BeforeIndex.Find(item.Position));
    }

    static InventorySnapshot ReadInventory(SqliteConnection db, ushort characterId, SqliteTransaction? transaction = null,
        AccountInventory? shared = null, InventorySnapshot? reuseInventory = null)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", characterId);
        command.CommandText = "SELECT c.inventory_expansion,c.gold,c.revive_coins,c.win_points,c.gold_carry_limit,c.auction_gold_limit,c.account,c.cargo_capacity," +
            "coalesce(p.selected,0),coalesce(p.cube_type,-1),c.avatar_expansion,c.devil_usage,c.stamina_until,c.daily_booster_usage,c.weapon_skin_state,c.avatar_preset_state,c.npc_favor," +
            "c.inventory_items_revision,COALESCE(a.inventory_items_revision,0) FROM characters c LEFT JOIN cube_premium p ON p.charac_no=c.id " +
            "LEFT JOIN account_cargo a ON a.account=c.account WHERE c.id=$id";
        ushort expansion, capacity;
        uint gold;
        int coins, wins, carry, auction;
        string account;
        CubeContractState cubeContract;
        SkinStorageState weaponSkins;
        AvatarPresetState avatarPresets;
        ushort avatarExpansion;
        ImmutableArray<DevilServiceUsage> devilUses;
        ImmutableArray<DailyBoosterUsage> dailyBoosterUses;
        ImmutableArray<NpcFavorPoint> npcFavor;
        long staminaUntil;
        InventoryItemRead itemRead;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidDataException("背包角色不存在");
            expansion = checked((ushort)reader.GetInt32(0));
            gold = checked((uint)reader.GetInt64(1));
            coins = reader.GetInt32(2);
            wins = reader.GetInt32(3);
            carry = reader.GetInt32(4);
            auction = reader.GetInt32(5);
            account = reader.GetString(6);
            capacity = checked((ushort)reader.GetInt32(7));
            cubeContract = new(checked((byte)reader.GetInt32(8)), checked((sbyte)reader.GetInt32(9)));
            avatarExpansion = checked((ushort)reader.GetInt32(10));
            devilUses = System.Text.Json.JsonSerializer.Deserialize<ImmutableArray<DevilServiceUsage>>(reader.GetString(11));
            staminaUntil = reader.GetInt64(12);
            dailyBoosterUses = JsonSerializer.Deserialize<ImmutableArray<DailyBoosterUsage>>(reader.GetString(13));
            weaponSkins = JsonSerializer.Deserialize<SkinStorageState>(reader.GetString(14)) ?? throw new InvalidDataException("角色武器幻化状态缺失");
            avatarPresets = JsonSerializer.Deserialize<AvatarPresetState>(reader.GetString(15)) ?? throw new InvalidDataException("角色装扮预设状态缺失");
            npcFavor = JsonSerializer.Deserialize<ImmutableArray<NpcFavorPoint>>(reader.GetString(16));
            itemRead = new(db.DataSource, account, characterId, reader.GetInt64(17), reader.GetInt64(18));
        }

        bool reuseItems = CanReuseInventoryItems(reuseInventory, itemRead);
        shared ??= ReadAccountInventory(db, account, transaction, includeItems: !reuseItems);
        var items = reuseItems ? reuseInventory!.Items : ReadInventoryItems(db, account, characterId, transaction).AddRange(shared.Items)
            .OrderBy(item => item.Position.List).ThenBy(item => item.Position.Index).ToImmutableArray();
        var purchases = ReadShopPurchases(db, account, characterId, transaction).AddRange(shared.Purchases)
            .OrderBy(row => row.NpcId).ThenBy(row => row.ItemId).ThenByDescending(row => row.AccountWide)
            .ThenBy(row => row.ResetDaily).ThenBy(row => row.Period).ToImmutableArray();
        var inventory = new InventorySnapshot(items, expansion, gold)
        {
            ReviveCoins = coins, WinPoints = wins, GoldCarryLimit = carry, AuctionGoldLimit = auction,
            AccountWallet = shared.Wallet, CubeContract = cubeContract, AvatarExpansion = avatarExpansion, DevilUses = devilUses, StaminaUntil = staminaUntil, CargoCapacity = capacity, AccountCargo = shared.Cargo,
            NpcFavor = npcFavor, Skins = weaponSkins, AccountSkins = shared.Skins, AvatarPresets = avatarPresets, ItemMakingSkills = ReadItemMakingSkills(db, transaction, characterId, account),
            ShopPurchases = purchases, EpicPieces = shared.Pieces, DailyBoosterUses = dailyBoosterUses,
            ConsumableStates = ReadConsumableStates(db, transaction, characterId, account),
            BossSelectionBuff = ReadBossSelectionBuff(db, transaction, characterId, account),
            ShieldDeck = ReadKnightShieldDeck(db, transaction, characterId, account),
            PrivilegeService = ReadPrivilegeService(db, transaction, characterId, account),
            ServerItems = ReadServerItems(db, transaction, characterId)
        };
        inventory.Validate();
        RememberInventoryItems(inventory, itemRead);
        return inventory;
    }

    static ImmutableArray<InventoryItem> ReadInventoryItems(SqliteConnection db, string account, ushort? characterId, SqliteTransaction? transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.item_uid,COALESCE(s.list_type,b.list_type),COALESCE(s.slot_index,b.slot_index),i.item_core,a.detail,p.detail FROM character_inventory_items i
            LEFT JOIN character_inventory_slots s ON s.item_uid=i.item_uid AND s.account=i.account AND s.character_id IS i.character_id
            LEFT JOIN character_title_book_slots b ON b.item_uid=i.item_uid AND b.account=i.account AND b.character_id=i.character_id
            LEFT JOIN inventory_avatar_details a ON a.item_uid=i.item_uid
            LEFT JOIN inventory_creature_details p ON p.item_uid=i.item_uid
            WHERE i.account=$account AND i.owner_character_id=COALESCE($id,0) AND i.character_id IS $id
              AND NOT EXISTS(SELECT 1 FROM mailbox_attachments m WHERE m.item_uid=i.item_uid)
            ORDER BY 2,3;
            """;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$id", (object?)characterId ?? DBNull.Value);
        var items = ImmutableArray.CreateBuilder<InventoryItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            items.Add(ReadInventoryItem(reader));
        return items.ToImmutable();
    }

    static InventoryItem ReadInventoryItem(SqliteDataReader reader)
    {
        if (reader.IsDBNull(1))
            throw new InvalidDataException("物品缺少容器位置");
        var core = JsonSerializer.Deserialize<ItemCore>(reader.GetString(3)) ?? throw new InvalidDataException("物品核心无法读取");
        return new(reader.GetInt64(0), new((InventoryListType)reader.GetInt32(1), checked((short)reader.GetInt32(2))), core)
        {
            Avatar = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<AvatarDetail>(reader.GetString(4)),
            Creature = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<CreatureDetail>(reader.GetString(5))
        };
    }

    static InventorySnapshot WriteInventory(SqliteConnection db, SqliteTransaction transaction, AccountOwners.Lease owner,
        ushort characterId, InventoryPlan plan, InventorySnapshot after, IReadOnlySet<long>? mailRetained = null,
        IReadOnlyDictionary<long, InventoryItem>? incoming = null)
    {
        var before = plan.Before;
        string reason = plan.Reason;
        var slots = plan.ChangedSlots;
        if (slots.IsEmpty) return after;
        var changed = slots.ToHashSet();
        var written = new Dictionary<long, (InventoryItem Item, string Hash)>();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        foreach (var slot in slots)
        {
            BindSlot(slot);
            command.CommandText = $"DELETE FROM {PositionTable(slot)} WHERE character_id IS $id AND account=$account AND list_type=$list AND slot_index=$slot";
            command.ExecuteNonQuery();
        }

        var retained = after.Items.Where(item => item.Uid != 0).Select(item => item.Uid).ToHashSet();
        if (mailRetained is not null)
            retained.UnionWith(mailRetained);
        foreach (var item in before.Items.Where(item => !retained.Contains(item.Uid)))
        {
            BindSlot(item.Position);
            command.Parameters.AddWithValue("$uid", item.Uid);
            command.CommandText = "DELETE FROM character_inventory_items WHERE character_id IS $id AND account=$account AND item_uid=$uid";
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("物品删除失去所有权");
        }

        var updated = after.Items.ToBuilder();
        for (int i = 0; i < updated.Count; i++)
        {
            var item = updated[i];
            if (!changed.Contains(item.Position))
                continue;
            BindSlot(item.Position);
            string core = JsonSerializer.Serialize(item.Core);
            command.Parameters.AddWithValue("$core", core);
            if (item.Uid == 0)
            {
                command.CommandText = "INSERT INTO character_inventory_items(character_id,account,item_core) VALUES($id,$account,$core) RETURNING item_uid";
                item = item with { Uid = (long)command.ExecuteScalar()! };
                if (item.Avatar is not null || item.Creature is not null)
                {
                    // 详情与核心共用持久 UID，客户端宠物键为 i32；溢出使整笔发放回滚。
                    item = item with { Core = item.Core with { Value = checked((int)item.Uid) } };
                    core = JsonSerializer.Serialize(item.Core);
                    command.Parameters["$core"].Value = core;
                    command.Parameters.AddWithValue("$uid", item.Uid);
                    command.CommandText = "UPDATE character_inventory_items SET item_core=$core WHERE item_uid=$uid";
                    command.ExecuteNonQuery();
                }
                updated[i] = item;
            }
            else
            {
                command.Parameters.AddWithValue("$uid", item.Uid);
                var previous = plan.BeforeIndex.FindUid(item.Uid);
                var old = previous ?? incoming?.GetValueOrDefault(item.Uid) ??
                    throw new InvalidOperationException("待转移实例不属于当前快照或已验证邮件");
                command.Parameters.AddWithValue("$oldOwner", previous?.Position.List == InventoryListType.AccountCargo ? DBNull.Value : characterId);
                command.CommandText = "UPDATE character_inventory_items SET character_id=$id,item_core=$core,updated_at=CURRENT_TIMESTAMP WHERE character_id IS $oldOwner AND account=$account AND item_uid=$uid";
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("物品更新失去所有权");
                if (old.Avatar is not null && item.Avatar is null)
                {
                    command.CommandText = "DELETE FROM inventory_avatar_details WHERE item_uid=$uid";
                    command.ExecuteNonQuery();
                }
                if (old.Creature is not null && item.Creature is null)
                {
                    command.CommandText = "DELETE FROM inventory_creature_details WHERE item_uid=$uid";
                    command.ExecuteNonQuery();
                }
            }

            if (!command.Parameters.Contains("$uid"))
                command.Parameters.AddWithValue("$uid", item.Uid);
            string? avatar = item.Avatar is null ? null : JsonSerializer.Serialize(item.Avatar);
            string? creature = item.Creature is null ? null : JsonSerializer.Serialize(item.Creature);
            if (avatar is not null)
            {
                command.Parameters.AddWithValue("$detail", avatar);
                command.CommandText = "INSERT INTO inventory_avatar_details(item_uid,detail) VALUES($uid,$detail) ON CONFLICT(item_uid) DO UPDATE SET detail=excluded.detail";
                command.ExecuteNonQuery();
            }
            if (creature is not null)
            {
                command.Parameters.AddWithValue("$detail", creature);
                command.CommandText = "INSERT INTO inventory_creature_details(item_uid,detail) VALUES($uid,$detail) ON CONFLICT(item_uid) DO UPDATE SET detail=excluded.detail";
                command.ExecuteNonQuery();
            }
            command.CommandText = $"INSERT INTO {PositionTable(item.Position)}(character_id,account,list_type,slot_index,item_uid) VALUES($id,$account,$list,$slot,$uid)";
            command.ExecuteNonQuery();
            written.Add(item.Uid, (item, HashJson(core, avatar, creature)));
        }

        var result = after with { Items = updated.ToImmutable() };
        result.Validate();
        var committedIndex = new InventoryIndex(result);
        foreach (var slot in slots)
        {
            var old = plan.BeforeIndex.Find(slot);
            var current = committedIndex.Find(slot);
            BindSlot(slot);
            command.Parameters["$id"].Value = characterId; // 审计记录发起操作的角色，账号物品本身不归属该角色。
            command.Parameters.AddWithValue("$generation", owner.Generation);
            command.Parameters.AddWithValue("$action", reason);
            Add("$oldUid", old?.Uid);
            Add("$newUid", current?.Uid);
            Add("$oldId", old?.Core.ItemId);
            Add("$newId", current?.Core.ItemId);
            Add("$oldValue", old?.Core.Value);
            Add("$newValue", current?.Core.Value);
            Add("$oldKind", old is null ? null : (byte)old.Core.Kind);
            Add("$newKind", current is null ? null : (byte)current.Core.Kind);
            Add("$oldCount", old?.Core.Count ?? 0);
            Add("$newCount", current?.Core.Count ?? 0);
            Add("$countDelta", (current?.Core.Count ?? 0) - (old?.Core.Count ?? 0));
            Add("$oldHash", Hash(old));
            Add("$newHash", Hash(current));
            command.CommandText = """
                INSERT INTO inventory_audit_log(account,character_id,generation,action_name,list_type,slot_index,
                  item_uid_before,item_uid_after,item_id_before,item_id_after,value_before,value_after,
                  item_kind_before,item_kind_after,count_before,count_after,count_delta,core_hash_before,core_hash_after)
                VALUES($account,$id,$generation,$action,$list,$slot,$oldUid,$newUid,$oldId,$newId,$oldValue,$newValue,
                  $oldKind,$newKind,$oldCount,$newCount,$countDelta,$oldHash,$newHash);
                """;
            command.ExecuteNonQuery();
        }

        return result;

        static string PositionTable(InventorySlot slot) => DfoServer.Game.Titles.TitleBookService.IsBook(slot.List)
            ? "character_title_book_slots" : "character_inventory_slots";

        void BindSlot(InventorySlot slot)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", slot.List == InventoryListType.AccountCargo ? DBNull.Value : characterId);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$list", (byte)slot.List);
            command.Parameters.AddWithValue("$slot", slot.Index);
        }
        void Add(string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        string? Hash(InventoryItem? item)
        {
            if (item is null)
                return null;
            // 摘要不含位置；纯移动可同时复用旧值摘要，属性或 detail 变化则分别计算。
            if (written.TryGetValue(item.Uid, out var encoded) && item.Core == encoded.Item.Core &&
                item.Avatar == encoded.Item.Avatar && item.Creature == encoded.Item.Creature)
                return encoded.Hash;
            return HashJson(JsonSerializer.Serialize(item.Core), item.Avatar is null ? null : JsonSerializer.Serialize(item.Avatar),
                item.Creature is null ? null : JsonSerializer.Serialize(item.Creature));
        }
        static string HashJson(string core, string? avatar, string? creature)
        {
            // 保持既有审计 JSON 的属性顺序与 null 字段，复用实际写库的完整 JSON 片段。
            string json = avatar is null && creature is null ? core :
                "{\"Core\":" + core + ",\"Avatar\":" + (avatar ?? "null") + ",\"Creature\":" + (creature ?? "null") + "}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }
}
