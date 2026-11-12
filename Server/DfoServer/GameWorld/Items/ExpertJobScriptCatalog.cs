using System.Globalization;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal readonly record struct ExpertJobRate(int MaximumLevelDifference, int SuccessPercent, int ExperienceMin, int ExperienceMax);
internal sealed record ExpertJobRecipe(int RecipeId, int ProductId, int RequiredLevel, (int ItemId, int Count)[] Materials,
    int ExperienceMin, int ExperienceMax, int ProductCount = 1);
internal readonly record struct ExpertJobScriptResult(int KeyA, int KeyB, int KeyC, int ItemId, double Multiplier,
    int AdditionalTable, int BigWinTable, int BigWinPercent);
internal readonly record struct ExpertJobExtractionSelection(int MinimumGrade, int MaximumGrade, int ItemId, int Weight, double Multiplier);
internal readonly record struct ExpertJobMonsterCardExp(int Level, int Min, int Max);
internal readonly record struct ExpertJobSkill(ushort Id, byte Level);

internal sealed record ExpertJobCardRecipe(int CardId, int BeadId, int RecipeId, int RequiredLevel, byte Rarity, (int ItemId, int Count)[] Materials, (int ItemId, int Count)[] BeadMaterials);

internal sealed record ExpertJobLearnableRecipe(int ItemId, byte Job, int RequiredLevel, byte? Rarity);

internal sealed class ExpertJobScriptCatalog
{
    static readonly Regex Number = new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    readonly Dictionary<byte, JobScript> jobs;
    readonly Dictionary<int, ExpertJobLearnableRecipe> learnable = new();
    readonly Dictionary<int, ExpertJobCardRecipe> cardRecipes = new();
    readonly Dictionary<int, (byte Job, int Level)> extractors = new();

    ExpertJobScriptCatalog(Dictionary<byte, JobScript> jobs) => this.jobs = jobs;

    public static ExpertJobScriptCatalog Load(PvfArchive pvf, ItemCatalog items, IReadOnlyDictionary<int, string> stackable)
    {
        var jobs = new Dictionary<byte, JobScript>();
        string ownDisjoint = pvf.Contains("character/expertjob.etc")
            ? PvfScript.Section(pvf.ReadText("character/expertjob.etc"), "disjointer big win rate") : "";
        foreach (var (id, path) in PvfScript.List(pvf.ReadText("character/expertjob.lst")))
        {
            if (id is < 1 or > 4) continue;
            string text = pvf.ReadText("character/" + path);
            jobs[(byte)id] = JobScript.Parse((byte)id, text, items, ownDisjoint, recipeId =>
                stackable.TryGetValue(recipeId, out var recipePath)
                    ? ItemRecipeFile.Parse(recipeId, recipePath, pvf.ReadTokens("stackable/" + recipePath), pvf.ResolveString) : null);
        }
        var result = new ExpertJobScriptCatalog(jobs);
        foreach (var (job, script) in jobs)
        {
            ushort skill = job switch { 1 => 191, 2 => 193, 4 => 192, _ => 0 };
            foreach (int tool in script.ExtractExp.Keys)
            {
                if (!stackable.TryGetValue(tool, out var toolPath)) continue;
                string restriction = PvfScript.Section(pvf.ReadText("stackable/" + toolPath), "expertjob only");
                int[] levels = PvfScript.Integers(restriction);
                string label = job switch { 1 => "enchanter", 2 => "alchemist", 4 => "doll_controller", _ => "" };
                if (levels is [> 0] && restriction.Contains('`' + label + '`', StringComparison.Ordinal))
                    result.extractors.Add(tool, (job, levels[0]));
            }
            foreach (var candidate in script.LearningRecipes.Values)
            {
                if (!stackable.TryGetValue(candidate.ItemId, out var path)) continue;
                var declaration = ExpertRecipeFile.Parse(candidate.ItemId, pvf.ReadTokens("stackable/" + path), pvf.ResolveString);
                if (!declaration.Supported || declaration.Skills.Length != 1 || declaration.Skills[0].ItemId != skill ||
                    declaration.Skills[0].Count > candidate.RequiredLevel ||
                    (candidate.Rarity is not null) != (declaration.RecipeKind == "[enchant]")) continue;
                result.learnable.Add(candidate.ItemId, candidate);
                if (candidate.Rarity is { } rarity)
                {
                    var recipe = ItemRecipeFile.Parse(candidate.ItemId, path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString);
                    if (!recipe.Diagnostics.IsEmpty || recipe.Inputs.IsEmpty || recipe.Inputs.Any(row => row.ItemId < 0 || row.Count <= 0)) continue;
                    foreach (var pair in script.CardProducts.Where(row => row.Value.RecipeId == candidate.ItemId))
                        result.cardRecipes.Add(pair.Key, new(pair.Key, pair.Value.BeadId, candidate.ItemId, candidate.RequiredLevel, rarity,
                            recipe.Inputs.Select(row => (row.ItemId, row.Count)).ToArray(),
                            recipe.BeadInputs.Select(row => (row.ItemId, row.Count)).ToArray()));
                }

            }
        }
        return result;
    }

    public bool TryCardRecipe(int cardId, out ExpertJobCardRecipe recipe) => cardRecipes.TryGetValue(cardId, out recipe!);

    // 六列成功率与0/1/2/3/4/6稀有度的对应为明确声明的兼容推断，可由配置覆盖。
    public int CardSuccessPercent(int level, byte rarity)
    {
        int column = rarity switch { 0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, 6 => 5, _ => -1 };
        if (column < 0 || !jobs.TryGetValue(1, out var job) || !job.CardSuccess.TryGetValue(level, out var values)) return 0;
        return Math.Clamp(values[column], 0, 100);
    }

    public bool TryLearningRecipe(int itemId, out ExpertJobLearnableRecipe recipe) => learnable.TryGetValue(itemId, out recipe!);

    public bool CanExtract(byte job, int level, int tool) => extractors.TryGetValue(tool, out var required) &&
        required.Job == job && level >= required.Level;

    public bool IsLearned(byte job, int level, IEnumerable<int> learned, int recipe) =>
        learnable.TryGetValue(recipe, out var definition) && definition.Job == job && definition.RequiredLevel <= level &&
        (learned.Contains(recipe) || (job is 1 or 2 or 4 && jobs[job].AutoRecipes.Any(row => row.ItemId == recipe && row.Count <= level)));

    public int[] LearnedRecipes(byte job, int level, IEnumerable<int> learned) =>
        UnlockedRecipes(job, level).Concat(job == 1 ? learned.Where(id => learnable.GetValueOrDefault(id)?.Rarity is not null) : [])
            // 附魔资格也在自动表中，不能只从持久化的稀有度配方构造列表。
            .Concat(job == 1 && jobs.TryGetValue(job, out var script)
                ? script.AutoRecipes.Where(row => row.Count <= level).Select(row => row.ItemId) : [])
            .Where(id => IsLearned(job, level, learned, id)).Distinct().Order().ToArray();

    public byte[] LearnedRarities(IEnumerable<int> recipes) => recipes.Select(id => learnable.GetValueOrDefault(id)?.Rarity)
        .Where(rarity => rarity.HasValue).Select(rarity => rarity!.Value).Distinct().Order().ToArray();

    public int Level(byte job, int experience) => jobs.TryGetValue(job, out var script) ? script.Level(experience) : 1;

    // 等级 1 起按 [expertness exp] 阈值累计；达到第 N-1 个阈值时显示 N 级，与服务端 Level 一致。
    public int MaxLevel(byte job) => jobs.TryGetValue(job, out var script) ? Math.Max(script.Thresholds.Length, 1) : 0;

    // 满级仍显示最后一档累计经验上限，不能用满级起始经验截断经验条。
    public int ClampExperience(byte job, int experience)
    {
        if (!jobs.TryGetValue(job, out var script)) throw new InvalidDataException("PVF 没有该副职业的等级阈值");
        int maximum = script.Thresholds.LastOrDefault();
        if (maximum < 0) throw new InvalidDataException("副职业累计熟练度上限不能为负数");
        return Math.Clamp(experience, 0, maximum);
    }

    public int AddExperience(byte job, int experience, long gained)
    {
        if (gained < 0) throw new InvalidDataException("副职业熟练度增量不能为负数");
        int current = ClampExperience(job, experience), maximum = ClampExperience(job, int.MaxValue);
        return current + (int)Math.Min(gained, maximum - current);
    }

    // 写入某等级的最低累计熟练度：1 级为 0，其余取该等级的起始阈值。
    public int ExperienceForLevel(byte job, int level)
    {
        if (!jobs.TryGetValue(job, out var script)) throw new InvalidDataException("PVF 没有该副职业的等级阈值");
        int maximum = Math.Max(script.Thresholds.Length, 1);
        if (level < 1 || level > maximum) throw new InvalidDataException($"副职业等级须在 1–{maximum}");
        return level == 1 ? 0 : script.Thresholds[level - 2];
    }

    public IReadOnlyList<ExpertJobSkill> Skills(byte job) => jobs.TryGetValue(job, out var script)
        ? script.Skills : throw new NotSupportedException("副职业资源未装配");

    public (int Min, int Max) DisjointGain(byte job) =>
        jobs.TryGetValue(job, out var script) ? script.Gain : (0, 0);

    public int[] GiveUpCosts(byte job) => jobs.TryGetValue(job, out var script) && script.GiveUpCosts.Length != 0
        ? script.GiveUpCosts : [1000, 10000, 100000, 1000000];

    public int EnduranceInitial(byte job) => jobs.TryGetValue(job, out var script) ? script.EnduranceInitial : 300;

    public IReadOnlyList<int> ClearQuests(byte job) => jobs.TryGetValue(job, out var script) ? script.ClearQuests : [];
    public IReadOnlyList<int> GiveUpDeletedItems(byte job) => jobs.TryGetValue(job, out var script) ? script.GiveUpDeletedItems : [];

    public bool TryUpgradeCost(byte job, int grade, out int gold)
    {
        gold = 0;
        return jobs.TryGetValue(job, out var script) && script.UpgradeCosts.TryGetValue(grade, out gold);
    }

    public bool TryRepair(byte job, int grade, out int gold, out int maxEndurance)
    {
        gold = 0;
        maxEndurance = 300;
        if (!jobs.TryGetValue(job, out var script) || grade < 1 || grade > script.RepairCosts.Length)
            return false;
        (gold, maxEndurance) = script.RepairCosts[grade - 1];
        return maxEndurance > 0;
    }

    public bool TryExtractExp(byte job, int itemId, out int min, out int max)
    {
        min = max = 0;
        if (!jobs.TryGetValue(job, out var script) || !script.ExtractExp.TryGetValue(itemId, out var range))
            return false;
        (min, max) = range;
        return true;
    }

    public bool TryExtractResult(byte job, int extractorId, int rarity, int state, out ExpertJobScriptResult result)
    {
        result = default;
        if (!jobs.TryGetValue(job, out var script)) return false;
        foreach (var row in script.ExtractionResults)
            if (row.KeyA == extractorId && row.KeyB == rarity && row.KeyC == state)
            {
                result = row;
                return true;
            }
        return false;
    }

    public int ExtractionBase(byte job) => jobs.GetValueOrDefault(job)?.ExtractionBase ?? 0;

    public IReadOnlyList<ExpertJobExtractionSelection> ExtractionPool(byte job, int table, bool bigWin) =>
        jobs.TryGetValue(job, out var script) ? (bigWin ? script.ExtractionBigWins : script.ExtractionAdditional).GetValueOrDefault(table) ?? [] : [];

    public ExpertJobDisjointRules DisjointRules => jobs.GetValueOrDefault((byte)3)?.DisjointRules ?? ExpertJobDisjointRules.Empty;

    public bool IsKnownCard(byte job, int itemId) =>
        jobs.TryGetValue(job, out var script) && script.KnownCards.Contains(itemId);

    public IReadOnlyCollection<int> KnownCards(byte job) =>
        jobs.TryGetValue(job, out var script) ? script.KnownCards : (IReadOnlyCollection<int>)[];

    public ExpertJobMonsterCardExp MonsterCardExp(byte job, int level)
    {
        if (!jobs.TryGetValue(job, out var script) || script.MonsterCard.Length == 0)
            return new(level, 0, 0);
        var selected = script.MonsterCard[^1];
        foreach (var row in script.MonsterCard)
            if (row.Level >= level) { selected = row; break; }
        return selected;
    }

    public int[] UnlockedRecipes(byte job, int level)
    {
        if (!jobs.TryGetValue(job, out var script)) return [];
        return script.Recipes.Values.Where(recipe => recipe.RequiredLevel <= level)
            .Select(recipe => recipe.RecipeId).OrderBy(id => id).ToArray();
    }

    public int[] ShopItems(byte job) => jobs.TryGetValue(job, out var script) ? script.ShopItems : [];

    public bool TryRecipe(byte job, int recipeId, out ExpertJobRecipe recipe)
    {
        recipe = null!;
        return jobs.TryGetValue(job, out var script) && script.Recipes.TryGetValue(recipeId, out recipe!);
    }

    public (int SuccessPercent, int ExperienceMin, int ExperienceMax) Outcome(byte job, ExpertJobRecipe recipe, int jobLevel)
    {
        if (!jobs.TryGetValue(job, out var script) || script.Rates.Length == 0)
            return (100, recipe.ExperienceMin, recipe.ExperienceMax);
        int difference = recipe.RequiredLevel - jobLevel;
        var rate = script.Rates[^1];
        foreach (var candidate in script.Rates)
            if (candidate.MaximumLevelDifference >= difference) { rate = candidate; break; }
        return (rate.SuccessPercent, rate.ExperienceMin, rate.ExperienceMax);
    }

    sealed class JobScript
    {
        public IReadOnlyList<ExpertJobSkill> Skills { get; init; } = [];
        public Dictionary<int, ExpertJobLearnableRecipe> LearningRecipes { get; init; } = [];
        public ExpertJobRate[] Rates { get; init; } = [];
        public Dictionary<int, ExpertJobRecipe> Recipes { get; init; } = [];
        public (int Min, int Max) Gain { get; init; }
        public int[] Thresholds { get; init; } = [];
        public int[] GiveUpCosts { get; init; } = [];
        public Dictionary<int, int> UpgradeCosts { get; init; } = [];
        public (int Gold, int MaxEndurance)[] RepairCosts { get; init; } = [];
        public int EnduranceInitial { get; init; } = 300;
        public Dictionary<int, (int Min, int Max)> ExtractExp { get; init; } = [];
        public ExpertJobScriptResult[] ExtractionResults { get; init; } = [];
        public int ExtractionBase { get; init; }
        public Dictionary<int, ExpertJobExtractionSelection[]> ExtractionAdditional { get; init; } = new();
        public Dictionary<int, ExpertJobExtractionSelection[]> ExtractionBigWins { get; init; } = new();
        public ExpertJobDisjointRules DisjointRules { get; init; } = ExpertJobDisjointRules.Empty;
        public HashSet<int> KnownCards { get; init; } = [];
        public ExpertJobMonsterCardExp[] MonsterCard { get; init; } = [];
        public Dictionary<int, (int RecipeId, int BeadId)> CardProducts { get; init; } = new();
        public Dictionary<int, int[]> CardSuccess { get; init; } = new();
        public int[] ShopItems { get; init; } = [];
        public int[] ClearQuests { get; init; } = [];
        public int[] GiveUpDeletedItems { get; init; } = [];
        public (int ItemId, int Count)[] AutoRecipes { get; init; } = [];

        public int Level(int experience)
        {
            int level = 1;
            foreach (int threshold in Thresholds)
            {
                if (experience < threshold) break;
                level++;
            }
            return Math.Min(level, Math.Max(Thresholds.Length, 1));
        }

        public static JobScript Parse(byte job, string text, ItemCatalog items, string ownDisjoint, Func<int, ItemRecipeFile?> readRecipe)
        {
            int[] skills = PvfScript.Integers(Field(text, "skill"));
            if (skills.Length % 2 != 0 || Enumerable.Range(0, skills.Length / 2).Any(i =>
                    skills[2 * i] is <= 0 or > ushort.MaxValue || skills[2 * i + 1] is <= 0 or > byte.MaxValue) ||
                Enumerable.Range(0, skills.Length / 2).Select(i => skills[2 * i]).Distinct().Count() != skills.Length / 2)
                throw new InvalidDataException("副职业赠技必须为不重复的技能/等级对");
            int[] thresholds = Pairs(PvfScript.Integers(Field(text, "expertness exp"))).Select(pair => pair.ItemId).ToArray();
            int[] gain = PvfScript.Integers(Field(text, "gain exp"));
            int[] rates = PvfScript.Integers(Field(text, "compound rate"));
            int[] rows = PvfScript.Integers(Field(text, "items"));
            int[] productExp = PvfScript.Integers(Field(text, "product exp"));
            int[] giveUp = PvfScript.Integers(Field(text, "giveup cost"));
            int[] upgrade = PvfScript.Integers(Field(text, "upgrade cost"));
            int[] repair = PvfScript.Integers(Field(text, "endurance repair cost"));
            int[] extractExp = PvfScript.Integers(Field(text, "extract exp"));
            int[] shop = PvfScript.Integers(Field(text, "only sell items"));
            int[] monster = PvfScript.Integers(Field(text, "monstercard exp"));
            int enduranceInitial = PvfScript.Integers(Field(text, "endurance initial value")).FirstOrDefault(300);
            int baseConst = PvfScript.Integers(Field(text, "base const")).FirstOrDefault(0);
            var exp = new Dictionary<int, (int Min, int Max)>();
            for (int index = 0; index + 2 < productExp.Length; index += 3)
                exp[productExp[index]] = (productExp[index + 1], productExp[index + 2]);
            var extract = new Dictionary<int, (int Min, int Max)>();
            for (int index = 0; index + 2 < extractExp.Length; index += 3)
                extract[extractExp[index]] = (extractExp[index + 1], extractExp[index + 2]);
            var learning = new Dictionary<int, ExpertJobLearnableRecipe>();
            var cardProducts = new Dictionary<int, (int RecipeId, int BeadId)>();
            if (rows.Length % 4 == 0)
                for (int index = 0; index < rows.Length; index += 4)
                    if (rows[index + 1] > 0 && rows[index + 3] > 0)
                        learning[rows[index + 1]] = new(rows[index + 1], job, rows[index + 3], null);
            foreach (Match match in Regex.Matches(Field(text, "rarity recipe"), @"\[rarity\](.*?)\[/rarity\]", RegexOptions.Singleline))
            {
                string block = match.Groups[1].Value;
                int[] rarity = PvfScript.Integers(block.Split('[', 2)[0]);
                int[] recipe = PvfScript.Integers(PvfScript.Section(block, "recipe"));
                int[] level = PvfScript.Integers(PvfScript.Section(block, "expert job level"));
                if (rarity is [{ } value] && value is >= 0 and <= byte.MaxValue && recipe is [> 0] && level is [> 0])
                {
                    learning[recipe[0]] = new(recipe[0], 1, level[0], (byte)value);
                    int[] products = PvfScript.Integers(PvfScript.BlockAllowingIdenticalDuplicates(block, "base result"));
                    if (products.Length % 2 == 0)
                        for (int at = 0; at < products.Length; at += 2)
                            if (products[at] > 0 && products[at + 1] > 0)
                                cardProducts[products[at]] = (recipe[0], products[at + 1]);
                }
            }
            var recipes = new Dictionary<int, ExpertJobRecipe>();
            if (rows.Length % 4 == 0)
            {
                for (int index = 0; index < rows.Length; index += 4)
                {
                    int recipeId = rows[index + 1], productId = rows[index + 2], required = rows[index + 3];
                    // EXJ 只给配方、产物与等级；制作消耗来自 STK input item，不是商店 need material。
                    var declaration = readRecipe(recipeId);
                    if (recipeId <= 0 || productId <= 0 || required <= 0 || declaration is null ||
                        !declaration.Diagnostics.IsEmpty || declaration.SourceType != 0 || declaration.Inputs.IsEmpty ||
                        declaration.Outputs is not [{ } output] || output.ItemId != productId || output.Count <= 0 ||
                        declaration.MaintainAbility is not null || declaration.MaintainSocketStat) continue;
                    exp.TryGetValue(recipeId, out var range);
                    recipes[recipeId] = new(recipeId, productId, required,
                        declaration.Inputs.Select(row => (row.ItemId, row.Count)).ToArray(), range.Min, range.Max, output.Count);
                }
            }
            var parsedRates = new List<ExpertJobRate>();
            if (rates.Length % 4 == 0)
                for (int index = 0; index < rates.Length; index += 4)
                    parsedRates.Add(new(rates[index], rates[index + 1], rates[index + 2], rates[index + 3]));
            var upgradeCosts = new Dictionary<int, int>();
            if (upgrade.Length % 2 == 0)
                for (int index = 0; index + 1 < upgrade.Length; index += 2)
                    upgradeCosts[upgrade[index]] = upgrade[index + 1];
            var repairCosts = new List<(int Gold, int MaxEndurance)>();
            if (repair.Length % 2 == 0)
                for (int index = 0; index + 1 < repair.Length; index += 2)
                    repairCosts.Add((repair[index], repair[index + 1]));
            var cards = new HashSet<int>();
            foreach (var match in Regex.Matches(text, @"\[base result\](.*?)\[/base result\]",
                RegexOptions.Singleline | RegexOptions.IgnoreCase).Cast<Match>())
            {
                int[] values = PvfScript.Integers(match.Groups[1].Value);
                for (int index = 0; index + 1 < values.Length; index += 2)
                    if (values[index] > 0) cards.Add(values[index]);
            }
            var monsterRows = new List<ExpertJobMonsterCardExp>();
            var cardSuccess = new Dictionary<int, int[]>();
            if (monster.Length % 9 == 0)
                for (int index = 0; index + 8 < monster.Length; index += 9)
                {
                    monsterRows.Add(new(monster[index], monster[index + 7], monster[index + 8]));
                    cardSuccess[monster[index]] = monster[(index + 1)..(index + 7)];
                }
            var shopItems = new List<int>();
            if (shop.Length % 2 == 0)
                for (int index = 0; index + 1 < shop.Length; index += 2)
                    if (shop[index + 1] > 0) shopItems.Add(shop[index + 1]);
            return new()
            {
                Skills = Array.AsReadOnly(Enumerable.Range(0, skills.Length / 2)
                    .Select(i => new ExpertJobSkill((ushort)skills[2 * i], (byte)skills[2 * i + 1])).ToArray()),
                LearningRecipes = learning,
                CardProducts = cardProducts,
                CardSuccess = cardSuccess,
                Thresholds = thresholds,
                Gain = gain.Length >= 2 ? (gain[0], gain[1]) : (0, 0),
                Rates = parsedRates.ToArray(),
                Recipes = recipes,
                GiveUpCosts = giveUp.Length == 0 ? [1000, 10000, 100000, 1000000] : giveUp,
                UpgradeCosts = upgradeCosts,
                RepairCosts = repairCosts.ToArray(),
                EnduranceInitial = enduranceInitial <= 0 ? 300 : enduranceInitial,
                ExtractExp = extract,
                ExtractionResults = Results(Field(text, "extraction result")),
                ExtractionBase = PvfScript.Integers(Field(text, "enchanter extraction result item")).ElementAtOrDefault(1),
                ExtractionAdditional = Selections(Field(text, "additional result")),
                ExtractionBigWins = Selections(Field(text, "big win result")),
                DisjointRules = string.IsNullOrWhiteSpace(Field(text, "disjoint result")) ? ExpertJobDisjointRules.Empty :
                    new(Field(text, "disjoint result"), Field(text, "additional result"), Field(text, "big win result"), baseConst, ownDisjoint),
                KnownCards = cards,
                MonsterCard = monsterRows.ToArray(),
                ShopItems = shopItems.ToArray(),
                ClearQuests = PvfScript.Integers(Field(text, "clear quests")).Where(id => id is > 0 and < 30000).Distinct().ToArray(),
                GiveUpDeletedItems = PvfScript.Integers(Field(text, "giveup delete item")).Where(id => id > 0).Distinct().ToArray(),
                AutoRecipes = Pairs(PvfScript.Integers(Field(text, "auto learn recipe"))).Select(row => (row.Count, row.ItemId)).ToArray()
            };
        }

        // 附魔师把 [endurance repair cost] 原样写了两遍；分解师 [gain exp] 只有节、没有闭合块。
        static string Field(string text, string tag)
        {
            string block = PvfScript.BlockAllowingIdenticalDuplicates(text, tag);
            return block.Length != 0 ? block : PvfScript.Section(text, tag);
        }

        static ExpertJobScriptResult[] Results(string block)
        {
            double[] values = Numbers(block);
            if (values.Length == 0 || values.Length % 8 != 0) return [];
            var rows = new ExpertJobScriptResult[values.Length / 8];
            for (int index = 0, row = 0; index + 7 < values.Length; index += 8, row++)
                rows[row] = new((int)values[index], (int)values[index + 1], (int)values[index + 2],
                    (int)values[index + 3], values[index + 4], (int)values[index + 5], (int)values[index + 6], (int)values[index + 7]);
            return rows;
        }

        static Dictionary<int, ExpertJobExtractionSelection[]> Selections(string block)
        {
            double[] values = Numbers(block);
            if (values.Length % 6 != 0) throw new InvalidDataException("副职业萃取附加表必须为六列");
            var rows = new Dictionary<int, List<ExpertJobExtractionSelection>>();
            for (int at = 0; at < values.Length; at += 6)
            {
                if (values[at] <= 0 || values[at + 1] < 0 || values[at + 2] < values[at + 1] || values[at + 3] <= 0 ||
                    values[at + 4] is < 0 or > 10000 || !double.IsFinite(values[at + 5]) || values[at + 5] <= 0)
                    throw new InvalidDataException("副职业萃取附加表字段非法");
                int key = checked((int)values[at]);
                if (!rows.TryGetValue(key, out var group)) rows.Add(key, group = []);
                group.Add(new(checked((int)values[at + 1]), checked((int)values[at + 2]), checked((int)values[at + 3]),
                    checked((int)values[at + 4]), values[at + 5]));
            }
            return rows.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        }

        static double[] Numbers(string text)
        {
            var values = new List<double>();
            foreach (Match match in Number.Matches(text))
                values.Add(double.Parse(match.Value, CultureInfo.InvariantCulture));
            return values.ToArray();
        }

        static IEnumerable<(int ItemId, int Count)> Pairs(int[] values)
        {
            for (int index = 0; index + 1 < values.Length; index += 2)
                yield return (values[index], values[index + 1]);
        }
    }
}
