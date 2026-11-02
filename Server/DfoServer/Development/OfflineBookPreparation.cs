using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using System.Globalization;
using System.Text.Json;

namespace DfoServer.Development;

// 开发用：按收藏簿职业格（职业+分支）创建95级二觉角色，只服务本机测试数据。
// 复用正式建角与仓储入口，保证技能、初始装备、疲劳与冒险团里程基线一并写入。
internal static class OfflineBookPreparation
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Run(ServerOptions options, CharacterCatalog characters, TownCatalog towns,
        CharacterRepository repository, SkillLearningCatalog learning, CharacterGrowthCatalog growth, string[] arguments)
    {
        if (!byte.TryParse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte job) || job > 15)
            throw new InvalidDataException("职业必须是0–15");
        if (!byte.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte branch) || branch is < 1 or > 5)
            throw new InvalidDataException("分支必须是1–5");
        string name = arguments[2];
        if (!CharacterName.IsValid(name))
            throw new InvalidDataException("角色名无效");
        string audit = Path.GetFullPath(arguments[3]);
        if (Directory.Exists(audit) || File.Exists(audit))
            throw new IOException("审计目录必须不存在");
        // 可选的第五个参数把角色建到指定账号下，供补发、公会、好友等本机多用例测试；
        // 缺省沿用开发账号，不改变既有调用。账号不存在时按开发口令摘要补建凭据行。
        string account = options.DevelopmentAccount;
        if (arguments.Length > 4)
        {
            if (!LoginCredentials.IsAccount(arguments[4]))
                throw new InvalidDataException("账号无效");
            account = arguments[4].ToLowerInvariant();
        }
        var credentials = new AuthenticationRepository(Path.Combine(options.DataDirectory, "game.db"));
        if (credentials.Find(account) is null)
            new AuthenticationService(credentials).Create(account, LoginCredentials.DevelopmentDigest);
        var profile = characters.Get(job);
        // 收藏簿按职业+分支入册；二觉写高半字节2，等级与接受剧情等级和现有95级角色一致。
        NewCharacter creation = CharacterCreation.Prepare(name, profile, towns.Room, towns.InitialReturn, learning.Get)
            with { GrowType = checked((byte)(branch | 0x20)), Level = 95, AcceptedStoryLevel = 95 };
        creation = creation with { InitialLearning = SkillPointLedger.Synchronize(job, creation.GrowType, 95,
            creation.InitialLearning with { Experience = growth.GetLevelStartExperience(95) }, learning) };
        creation = creation with { Skills = creation.InitialLearning.Trees.Single(tree =>
            tree.Context == creation.InitialLearning.ActiveContext).Skills.OrderBy(skill => skill.Id)
            .Select(skill => new CharacterSkill(skill.Id, skill.Level)).ToArray() };
        Directory.CreateDirectory(audit);
        File.WriteAllText(Path.Combine(audit, "intent.json"), JsonSerializer.Serialize(new
        {
            Database = Path.Combine(options.DataDirectory, "game.db"),
            Account = account,
            Name = name,
            Job = job,
            Branch = branch,
            GrowType = creation.GrowType,
            Level = creation.Level,
            AcceptedStoryLevel = creation.AcceptedStoryLevel,
            Skills = creation.Skills.Length,
            Items = creation.Inventory.Items.Length
        }, Json));
        using var owner = new AccountOwners().Acquire(account, () =>
        {
        });
        Character created = repository.Create(owner, creation);
        Character persisted = repository.List(owner).Single(value => value.Id == created.Id);
        if (persisted.Name != name || persisted.Job != job || persisted.GrowType != creation.GrowType ||
            persisted.Level != 95 || persisted.AcceptedStoryLevel != 95 ||
            !persisted.Learning.Initialized || persisted.Learning.Experience != growth.GetLevelStartExperience(95) ||
            !persisted.Skills.SequenceEqual(creation.Skills))
            throw new InvalidDataException("创建后读取的角色与请求不一致");
        File.WriteAllText(Path.Combine(audit, "committed.json"),
            JsonSerializer.Serialize(new { Character = persisted, CommitUtc = DateTime.UtcNow }, Json));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            persisted.Id,
            persisted.Slot,
            persisted.Name,
            persisted.Job,
            persisted.GrowType,
            persisted.Level,
            Skills = persisted.Skills.Length,
            Items = persisted.Inventory.Items.Length
        }));
    }
}
