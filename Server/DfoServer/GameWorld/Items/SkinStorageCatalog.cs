using System.Collections.Frozen;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record WeaponSkinRecipeDefinition(int Id, int ResultItemId, ImmutableArray<ItemMaterialCost> Materials);
internal sealed record WeaponSkinCandidate(int ItemId, int Level, string Name, int EquipmentType, int Job);
internal sealed record SkinStorageToken(byte Type, int SkinId, ItemExpiration Expiration, ImmutableArray<string> Places);

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
    }

    public bool IsToken(int id) => Token(id) is not null;
    public SkinStorageToken? Token(int id)
    {
        if (!stackable.ContainsKey(id)) return null;
        var result = tokens.GetOrAdd(id, key =>
        {
            string path = "stackable/" + stackable[key];
            var action = SkinStorageFile.ReadAction(key, path, archive.ReadTokens(path), archive.ResolveString);
            if (action is null || action.Target != "[character]") return Unsupported;
            byte type = action.SkinId == 40000 ? (byte)4 : DamageFonts.Contains(action.SkinId) ? (byte)2 :
                Cutscenes.Contains(action.SkinId) ? (byte)1 : (byte)255;
            if (type == 255 || type == 2 && !HasDamageFonts || type == 1 && !HasCutscenes) return Unsupported;
            long absoluteTime = action.ExpirationMode == "[date]" ? ItemExpiration.ParseAbsoluteTime(action.Date) : 0;
            if (action.ExpirationMode == "[date]" && absoluteTime == 0)
                throw new InvalidDataException("皮肤动作的指定日期不能解释为永久");
            var expiration = new ItemExpiration(absoluteTime, action.Days);
            return new(type, action.SkinId, expiration, action.Places);
        });
        return result == Unsupported ? null : result;
    }
}
