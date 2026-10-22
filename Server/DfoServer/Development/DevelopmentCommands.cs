using DfoServer.Game.Accounts;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using DfoServer.Infrastructure;
using DfoServer.Game.Mailbox;
using PvfLib;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;

namespace DfoServer.Development;

internal static class DevelopmentCommands
{
    public static int RunGm(string[] args, ServerOptions options, PvfArchive pvf)
    {
        string line = string.Join(' ', args);
        DevelopmentRequest request;
        try
        {
            request = DevelopmentRequest.Parse(line, options.DevelopmentAccount);
        }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }

        if (options.GmPort != 0)
        {
            try
            {
                if (DevelopmentGmClient.TryExecute(options.GmPort, line, out var remote))
                {
                    if (remote.StartsWith("ERR ", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine(remote[4..]);
                        return 1;
                    }

                    Console.WriteLine(remote);
                    return 0;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        if (request.Action is DevelopmentAction.CashShopExpiration or DevelopmentAction.CashShopUsablePeriod or DevelopmentAction.InfiniteFatigue or DevelopmentAction.PcRoom)
        {
            Console.Error.WriteLine("调试开关仅支持在线服务，请传入实际运行实例的 --gm-port。");
            return 1;
        }

        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            using var databaseLock = new FileStream(Path.Combine(options.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var repository = new CharacterRepository(Path.Combine(options.DataDirectory, "game.db"));
            bool reset = request.Action is DevelopmentAction.Level or DevelopmentAction.Grow or DevelopmentAction.GrowOptions;
            ItemResources? resources = request.Action is DevelopmentAction.Item or DevelopmentAction.ItemInfo or DevelopmentAction.Items || reset ? new ItemResources(pvf) : null;
            MailboxService? mailbox = request.Action == DevelopmentAction.Item ? new MailboxService(repository, resources!.Items, resources.GoldLimits, resources.EpicPieces) : null;
            var learning = reset ? SkillLearningCatalog.Load(pvf, resources!.Contracts.ExpertSkillLevels) : null;
            var progress = reset ? new CharacterProgressReset(CharacterCatalog.Load(pvf, resources!.Items), learning!, CharacterGrowthCatalog.Load(pvf, learning)) : null;
            var events = request.Action == DevelopmentAction.Event ? new Game.Events.ServerEventService(
                new ServerEventRepository(Path.Combine(options.DataDirectory, "game.db"))) : null;
            // 扩展装备栏只读写角色标志、纸娃娃外观只读角色存档，不需要物品与邮件资源。
            bool extended = request.Action >= DevelopmentAction.Wallet && request.Action is not (DevelopmentAction.EquipSlots or DevelopmentAction.Appearance);
            resources ??= extended ? new ItemResources(pvf) : null;
            mailbox ??= extended ? new MailboxService(repository, resources!.Items, resources.GoldLimits, resources.EpicPieces) : null;
            var catalog = extended ? new DevelopmentCatalog(pvf, resources!, request.Action is DevelopmentAction.QuestSearch or DevelopmentAction.QuestChain or DevelopmentAction.TitleList or DevelopmentAction.TitleComplete
                ? GameWorld.Quests.QuestCatalog.Load(pvf) : null)
            {
                Growth = request.Action == DevelopmentAction.ExpTable ? GameWorld.Characters.CharacterGrowthCatalog.Load(pvf) : null,
                Stats = request.Action == DevelopmentAction.Stats ? new Game.Characters.CharacterStatComputer(GameWorld.Characters.CharacterCatalog.Load(pvf, resources!.Items)) : null
            } : null;
            // 离线命令是一次性进程，查询前同步等待索引；在线服务仍走后台构建。
            if (request.Action is DevelopmentAction.ItemSearch or DevelopmentAction.ItemCategories or DevelopmentAction.SetGrant or DevelopmentAction.QuestSearch or DevelopmentAction.TitleList or DevelopmentAction.ItemFilter)
            {
                catalog!.Warm(request.Action is not (DevelopmentAction.QuestSearch or DevelopmentAction.TitleList), request.Action is DevelopmentAction.QuestSearch or DevelopmentAction.TitleList, TimeSpan.FromMinutes(10));
                if (request.Action == DevelopmentAction.ItemFilter) catalog.WarmAttributes(TimeSpan.FromMinutes(10));
            }
            Console.WriteLine(DevelopmentGm.Execute(repository, new AccountOwners(), _ => null, request, mailbox, resources?.Items, progress, events: events, catalog: catalog));
            return 0;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("运行库正被服务占用。在线调整需要传入与运行实例一致的 --gm-port；否则先停止该实例。");
            return 1;
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
