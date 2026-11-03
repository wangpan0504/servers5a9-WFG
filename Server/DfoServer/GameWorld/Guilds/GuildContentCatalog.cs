using System.Collections.Frozen;
using PvfLib;
using DfoServer.GameWorld.Items;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildContentDefinition(uint Id, byte Type, int Value, int Days, byte Mode,
    int DisplayItem, int Extra, int Price);

internal sealed class GuildContentCatalog
{
    readonly FrozenDictionary<uint, GuildContentDefinition> contents;
    public int CargoLimit { get; }
    public int DungeonMileage { get; }
    public GuildContentCatalog(PvfArchive pvf) : this(pvf.ReadTokens("etc/guild.etc"), pvf.ResolveString) { }
    internal GuildContentCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty) throw new InvalidDataException("公会内容声明结构不完整");
        PvfTokenSection Section(string name) => document.Sections.SingleOrDefault(row => row.Name == name)
            ?? throw new InvalidDataException("缺少公会内容声明: " + name);
        int Scalar(string name)
        {
            var values = Section(name).Values;
            if (values.Length != 1 || values[0].Type != 0 || values[0].Value <= 0)
                throw new InvalidDataException("公会内容标量无效: " + name);
            return values[0].Value;
        }
        CargoLimit = Scalar("guild cargo size limit");
        DungeonMileage = Scalar("guild mileage for dungeon clear");
        var rows = Section("guild contents list").Values;
        if (rows.Length == 0 || rows.Length % 9 != 0) throw new InvalidDataException("公会内容必须是九列声明");
        var values = new Dictionary<uint, GuildContentDefinition>();
        for (int at = 0; at < rows.Length; at += 9)
        {
            if (rows.Skip(at).Take(8).Any(token => token.Type != 0) || rows[at + 8].Type is not (6 or 7) ||
                rows[at].Value <= 0 || rows[at + 1].Value is < 1 or > byte.MaxValue || rows[at + 2].Value <= 0 ||
                rows[at + 4].Value is < 0 or > 1 || rows[at + 7].Value <= 0)
                throw new InvalidDataException("公会内容字段无效");
            var row = new GuildContentDefinition(checked((uint)rows[at].Value), checked((byte)rows[at + 1].Value),
                rows[at + 2].Value, rows[at + 3].Value, checked((byte)rows[at + 4].Value), rows[at + 5].Value,
                rows[at + 6].Value, rows[at + 7].Value);
            if (!values.TryAdd(row.Id, row)) throw new InvalidDataException("公会内容编号重复");
        }
        if (CargoLimit is < 8 or > 56) throw new InvalidDataException("公会仓库上限超出本版范围");
        contents = values.ToFrozenDictionary();
    }
    public GuildContentDefinition? Find(uint id) => contents.GetValueOrDefault(id);
    public IReadOnlyCollection<GuildContentDefinition> Contents => contents.Values;
}

internal sealed record GuildMileageGiftDefinition(ItemTemplate Item, int Amount)
{
    public const uint Action = 146;
    public static GuildMileageGiftDefinition? Parse(StackableFile raw, ItemTemplate item)
    {
        var action = StackableActionCatalog.Arguments(raw);
        if (raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            action is not { Label: "[add guildmileage]", Parameters: [> 0] } ||
            raw.DeclaredUseActionPacket != 1 || raw.TypeDeclaration is null ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 }) return null;
        return new(item, action.Parameters[0]);
    }
}
