namespace DfoServer.GameWorld.Maps;

internal sealed partial class MapGeometryConfig
{
    private static bool IsNativeRestore(string kind) => kind is
        "native_tiles" or "native_ext_tilefiles" or "native_ext_tilefiles_clone" or "native_ext_tilemap" or "native_anchor_restore" or "native_geometry_restore";

    private static IEnumerable<int> NativeRepairSlots(string path, Rule rule, Dictionary<string,string> aliases)
    {
        for (int at = 1; at < Math.Min(rule.From[0].Length,rule.To.Length); at += 2)
            if (rule.From[0][at] != rule.To[at] && aliases.ContainsKey(ResolveReference(path,rule.To[at].Text!)))
            {
                if (rule.From[0][at - 1] != rule.To[at - 1])
                    throw new InvalidDataException("World native extension repair slot identity");
                yield return at;
            }
    }

    private static void ValidateNativeRecord(string path, string kind, string tag, Atom[] before, Atom[] after)
    {
        if (kind == "native_geometry_restore")
        {
            if (before.Length == 0 || before.Concat(after).Any(atom => atom.Kind != 0 || atom.Number is < -1048576 or > 1048576))
                throw new InvalidDataException("World native geometry integer range or missing source");
            foreach (var values in new[] { before, after })
            {
                if (values.Length == 0) continue;
                if (tag == "[force map width]")
                {
                    if (values.Length != 1 || values[0].Number <= 0)
                        throw new InvalidDataException("World native force width shape");
                }
                else
                {
                    if (values.Length % 4 != 0) throw new InvalidDataException("World native virtual rectangle shape");
                    for (int at = 0; at < values.Length; at += 4)
                        if (values[at + 2].Number <= 0 || values[at + 3].Number <= 0)
                            throw new InvalidDataException("World native virtual rectangle dimensions");
                }
            }
            return;
        }
        if (kind == "native_anchor_restore")
        {
            ValidateNativeAnchor(tag,before,after);
            return;
        }
        if (kind == "native_tiles")
        {
            if ((before.Length,after.Length) is not ((1,1) or (3,3) or (4,4) or (5,5) or (5,4) or (5,3) or (5,1)))
                throw new InvalidDataException("World native tile column shape");
            foreach (var atom in before.Concat(after)) ValidateNativeTileReference(path,atom);
            return;
        }
        if (kind is "native_ext_tilefiles" or "native_ext_tilefiles_clone")
        {
            if (before.Length == 0 || after.Length == 0 || before.Length % 2 != 0 || after.Length % 2 != 0)
                throw new InvalidDataException("World native extended file shape");
            _ = TileFileIds(before);
            _ = TileFileIds(after);
            for (int at = 1; at < before.Length; at += 2) ValidateNativeTileReference(path,before[at]);
            for (int at = 1; at < after.Length; at += 2) ValidateNativeTileReference(path,after[at]);
            return;
        }
        if (before.Length == 0 || after.Length == 0 || before.Concat(after).Any(atom => atom.Kind != 0 || atom.Number < -1))
            throw new InvalidDataException("World native extended matrix atom");
    }

    private static void ValidateNativeTileReference(string path, Atom atom)
    {
        if (atom.Kind != 6 || string.IsNullOrEmpty(atom.Text) || !ResolveReference(path,atom.Text).EndsWith(".til",StringComparison.Ordinal))
            throw new InvalidDataException("World native tile reference kind/path");
    }

    private static void ValidateNativeExtendedRecipe(List<Rule> rules)
    {
        var tiles = rules.Where(rule => rule.Tag == "[tile]").ToArray();
        var files = rules.Where(rule => rule.Tag == "[tile files]").ToArray();
        var matrix = rules.Where(rule => rule.Tag == "[tile map]").ToArray();
        if (tiles.Length != 1 || files.Length != 1 || matrix.Length != 1 ||
            tiles[0].Kind != "native_tiles" || files[0].Kind is not ("native_ext_tilefiles" or "native_ext_tilefiles_clone") || matrix[0].Kind != "native_ext_tilemap")
            throw new InvalidDataException("World native extension requires complete native tile/file/matrix plan");
        static int Rows(Atom[] values, int columns, HashSet<int> ids)
        {
            if (values.Length == 0 || values.Length % columns != 0 ||
                values.Any(atom => atom.Kind != 0 || atom.Number != -1 && !ids.Contains(atom.Number)))
                throw new InvalidDataException("World native extension column shape or missing id");
            return values.Length / columns;
        }
        int rows = Rows(matrix[0].To,tiles[0].To.Length,TileFileIds(files[0].To));
        for (int profile = 0; profile < 3; ++profile)
            if (Rows(matrix[0].From[profile],tiles[0].From[profile].Length,TileFileIds(files[0].From[profile])) != rows)
                throw new InvalidDataException("World native extension row count changed");
    }


    // 可见末列只追加同源绘制别名；原缺源修复合同保持独立。
    private static void ValidateVisibleExtensionClone(string path,List<Rule> rules,Dictionary<string,string> aliases,Dictionary<string,List<Rule>> all)
    {
        var file = rules.SingleOrDefault(r => r.Kind == "native_ext_tilefiles_clone");
        if (file is null) return;
        var tile = rules.Single(r => r.Kind == "native_tiles");
        var matrix = rules.Single(r => r.Kind == "native_ext_tilemap");
        string GuardedSource(string alias)
        {
            if (!aliases.TryGetValue(alias, out string? source) || !all.TryGetValue(alias, out var guards) || guards.Count != 1 ||
                guards[0].Kind != "tile_pass14" || guards[0].From.Any(values => !values.SequenceEqual(guards[0].To)))
                throw new InvalidDataException("World visible clone pass guard");
            return source;
        }
        for (int profile=0;profile<3;++profile)
        {
            var before=file.From[profile];var after=file.To;int columns=tile.From[profile].Length;
            if (after.Length<=before.Length || (after.Length-before.Length)%2!=0 || !before.SequenceEqual(after.Take(before.Length)) || tile.To.Length!=columns ||
                matrix.From[profile].Length!=matrix.To.Length || columns<1)
                throw new InvalidDataException("World visible clone file prefix or columns");
            if (!tile.From[profile].Take(columns-1).SequenceEqual(tile.To.Take(columns-1)))
                throw new InvalidDataException("World visible clone base prefix");
            string baseAlias=ResolveReference(path,tile.To[^1].Text!);
            if (GuardedSource(baseAlias)!=ResolveReference(path,tile.From[profile][^1].Text!))
                throw new InvalidDataException("World visible clone base source");
            var oldSources = new Dictionary<int,string>();
            var addedSources = new Dictionary<int,string>();
            var sourceIds = new Dictionary<string,int>(StringComparer.Ordinal);
            var used = new HashSet<int>();
            for (int at = 0; at < before.Length; at += 2)
                oldSources.Add(before[at].Number, ResolveReference(path, before[at + 1].Text!));
            for (int at = before.Length; at < after.Length; at += 2)
            {
                int id = after[at].Number;
                string source = GuardedSource(ResolveReference(path, after[at + 1].Text!));
                if (oldSources.ContainsKey(id) || !addedSources.TryAdd(id, source) || !sourceIds.TryAdd(source, id))
                    throw new InvalidDataException("World visible clone slot or source identity");
            }
            for(int at=0;at<matrix.To.Length;++at)
            {
                if(at%columns!=columns-1)
                { if(matrix.From[profile][at]!=matrix.To[at]) throw new InvalidDataException("World visible clone nonterminal cell");continue; }
                int originalId = matrix.From[profile][at].Number, selectedId = matrix.To[at].Number;
                // 未选中的末格完整保留；空格不能借克隆合同填图。
                if (matrix.From[profile][at] == matrix.To[at])
                {
                    if (originalId != -1 && !oldSources.ContainsKey(originalId))
                        throw new InvalidDataException("World visible clone preserved terminal definition");
                    continue;
                }
                if (!oldSources.TryGetValue(originalId, out string? originalSource) ||
                    !addedSources.TryGetValue(selectedId, out string? selectedSource) || originalSource != selectedSource)
                    throw new InvalidDataException("World visible clone terminal source");
                used.Add(selectedId);
            }
            if (used.Count != addedSources.Count) throw new InvalidDataException("World visible clone unused definition");
        }
    }

    private static void ValidateNativeAnchor(string tag, Atom[] before, Atom[] after)
    {
        if (tag == "[dungeon movable area]")
        {
            if (after.Length != 12 || before.Length != after.Length ||
                before.Concat(after).Any(atom => atom.Kind != 0 || atom.Number is < -1048576 or > 1048576))
                throw new InvalidDataException("World native metadata rectangles shape/range");
            bool identity = before.SequenceEqual(after);
            for (int at = 0; at < after.Length; at += 4)
            {
                int x = after[at].Number, width = after[at + 2].Number;
                if (width <= 0 || after[at + 3].Number <= 0 || before[at + 1] != after[at + 1] || before[at + 3] != after[at + 3] ||
                    before[at].Number != x + (!identity && x >= 672 ? 224 : 0) ||
                    before[at + 2].Number != width + (!identity && x < 672 && (long)x + width > 672 ? 224 : 0))
                    throw new InvalidDataException("World native metadata inverse insertion mapping");
            }
            return;
        }
        string mechanism = tag switch
        {
            "[animation]" => "dungeon_animation5",
            "[passive object]" => "dungeon_passive4",
            "[monster]" => "dungeon_monster10_fixed_normal",
            "[ai character]" when after.Length >= 10 && after.Length % 10 == 0 &&
                after[4].Text == "[monster]" && after[5].Text == "[NPC]" && after[7].Text == "[boss]" => "dungeon_ai10_npc_monster_boss",
            "[ai character]" when after.Length % 8 == 0 => "dungeon_ai8",
            "[ai character]" => "dungeon_ai10_npc_monster_boss",
            "[event monster position]" => "dungeon_event_xyz3",
            "[special passive object]" => "dungeon_special11_single_child",
            _ => throw new InvalidDataException("World native anchor mechanism")
        };
        var layout = AnchorLayout(mechanism);
        if (after.Length == 0 || after.Length % layout.Stride != 0)
            throw new InvalidDataException("World native anchor record shape");
        // 先复用既有正向合同验证目标的记录布局、身份尾段及X范围，再接受其原生端点或严格逆映射。
        var expanded = after.Select((atom,index) => index % layout.Stride == layout.X
            ? atom with { Number = atom.Number + (atom.Number >= 672 ? 224 : 0) } : atom).ToArray();
        ValidateAnchor(mechanism,after,expanded);
        if (before.SequenceEqual(after)) return;
        if (tag == "[animation]" && before.Length == after.Length + 5)
        {
            ValidateAnimationAppend(after,before);
            return;
        }
        ValidateAnchor(mechanism,after,before);
    }
}
