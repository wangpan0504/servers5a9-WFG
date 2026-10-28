using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record WeaponSkinRecipeDefinition(int Id, int ResultItemId, ImmutableArray<ItemMaterialCost> Materials);
internal sealed record WeaponSkinCandidate(int ItemId, int Level, string Name, int EquipmentType, int Job);
internal sealed record SkinStorageToken(byte Type, int SkinId, ItemExpiration Expiration, ImmutableArray<string> Places)
{
    public ImmutableArray<int> SkinIds { get; init; } = [SkinId];
    // [action target] 决定所有权落在账号皮肤仓还是角色皮肤仓。
    public bool AccountWide { get; init; }
}

internal sealed class SkinStorageCatalog
{
    public const string RecipesPath = "skin/weaponskincontainer.etc";
    public const string ItemsPath = "etc/weaponskiniteminfo.dat";
    public FrozenDictionary<int, WeaponSkinRecipeDefinition> Recipes { get; }
    public FrozenDictionary<int, WeaponSkinCandidate> Weapons { get; }
    public FrozenSet<int> DamageFonts { get; }
    public bool HasDamageFonts => DamageFonts.Contains(1);
    public FrozenSet<int> Cutscenes { get; }
    public bool HasCutscenes => Cutscenes.Contains(30000);
    public FrozenSet<int> PartyFrames { get; }
    public bool HasPartyFrames => PartyFrames.Contains(20000);
    public FrozenSet<int> Emoticons { get; }
    readonly PvfArchive archive;
    readonly IReadOnlyDictionary<int, string> stackable;
    readonly ConcurrentDictionary<int, SkinStorageToken> tokens = new();
    static readonly SkinStorageToken Unsupported = new(0, 0, new(), []);

    public SkinStorageCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable)
    {
        this.archive = archive;
        this.stackable = stackable;
        Recipes = archive.Contains(RecipesPath)
            ? WeaponSkinFile.ReadRecipes(archive.ReadTokens(RecipesPath), archive.ResolveString)
                .Select(row => new WeaponSkinRecipeDefinition(row.Id, row.ResultItemId,
                    row.Materials.Select(cost => new ItemMaterialCost(cost.ItemId, cost.Count)).ToImmutableArray())).ToFrozenDictionary(row => row.Id)
            : FrozenDictionary<int, WeaponSkinRecipeDefinition>.Empty;
        Weapons = archive.Contains(ItemsPath)
            ? WeaponSkinFile.ReadItems(archive.ReadTokens(ItemsPath), archive.ResolveString)
                .Select(row => new WeaponSkinCandidate(row.ItemId, row.Level, row.Name, row.EquipmentType, row.Job)).ToFrozenDictionary(row => row.ItemId)
            : FrozenDictionary<int, WeaponSkinCandidate>.Empty;
        var skinTypes = archive.Contains("skin/skin.lst") ? PvfScript.List(archive.ReadText("skin/skin.lst"))
            .Where(row => archive.Contains("skin/" + row.Value)).ToDictionary(row => row.Key,
                row => SkinStorageFile.ReadType(archive.ReadTokens("skin/" + row.Value), archive.ResolveString)) : [];
        DamageFonts = skinTypes.Where(row => row.Value == "damage font").Select(row => row.Key).ToFrozenSet();
        Cutscenes = skinTypes.Where(row => row.Value == "skill cutscene").Select(row => row.Key).ToFrozenSet();
        PartyFrames = skinTypes.Where(row => row.Value == "party frame").Select(row => row.Key).ToFrozenSet();
        Emoticons = skinTypes.Where(row => row.Value == "instant emoticon").Select(row => row.Key).ToFrozenSet();
    }

    public bool IsToken(int id) => Token(id) is not null;
    public SkinStorageToken? Token(int id)
    {
        if (!stackable.ContainsKey(id)) return null;
        var result = tokens.GetOrAdd(id, key =>
        {
            string path = "stackable/" + stackable[key];
            var source = archive.ReadTokens(path);
            var item = StackableFile.Parse(key, path, source, archive.ResolveString);
            if (StackableActionCatalog.Arguments(item) is not { Label: "[add skin storage]", Parameters.Count: > 0 })
                return Unsupported;
            var action = SkinStorageFile.ReadAction(item, source, archive.ResolveString);
            if (action is null || action.Target is not ("[character]" or "[account]")) return Unsupported;
            // 0429E5F0 把皮肤文件 [type] 映射成 wire 类别：party frame=0、skill cutscene=1、
            // damage font=2、instant emoticon=3、weapon skin=4。
            byte type = action.SkinId == 40000 ? (byte)4 : DamageFonts.Contains(action.SkinId) ? (byte)2 :
                Cutscenes.Contains(action.SkinId) ? (byte)1 : PartyFrames.Contains(action.SkinId) ? (byte)0 :
                Emoticons.Contains(action.SkinId) ? (byte)3 : (byte)255;
            if (type == 255 || type == 2 && !HasDamageFonts || type == 1 && !HasCutscenes ||
                type == 0 && !HasPartyFrames) return Unsupported;
            // 1764130 只对非武器类别读取第二份账号列表，武器皮肤没有账号归属的显示位。
            if (type == 4 && action.Target == "[account]") return Unsupported;
            if (action.SkinIds.Length > byte.MaxValue || action.SkinIds.Distinct().Count() != action.SkinIds.Length ||
                (type == 4 ? action.SkinIds.Length != 1 :
                    action.SkinIds.Any(id => !(type == 2 ? DamageFonts : type == 1 ? Cutscenes : type == 0 ? PartyFrames : Emoticons).Contains(id))))
                return Unsupported;
            long absoluteTime = action.ExpirationMode == "[date]" ? ItemExpiration.ParseAbsoluteTime(action.Date) : 0;
            if (action.ExpirationMode == "[date]" && absoluteTime == 0)
                throw new InvalidDataException("皮肤动作的指定日期不能解释为永久");
            var expiration = new ItemExpiration(absoluteTime, action.Days);
            return new(type, action.SkinId, expiration, action.Places)
                { SkinIds = action.SkinIds, AccountWide = action.Target == "[account]" };
        });
        return result == Unsupported ? null : result;
    }
}
