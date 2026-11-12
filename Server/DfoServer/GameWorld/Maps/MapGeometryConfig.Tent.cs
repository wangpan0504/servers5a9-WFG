namespace DfoServer.GameWorld.Maps;

internal sealed partial class MapGeometryConfig
{
    private static bool TentKind(string kind) => kind is "dungeon_tent_animation5" or "dungeon_tent_passive4";
    private static bool TentAnimation(Atom atom) => atom.Kind == 6 && atom.Text!.Replace('\\','/').EndsWith("animation_10424/tentopen_1.ani",StringComparison.OrdinalIgnoreCase);

    // 帐篷在插列线左侧的阻挡物也属于同一入口，必须随整组迁移。
    private static void ValidateTentRecord(string kind, Atom[] source, Atom[] target)
    {
        bool animation = kind == "dungeon_tent_animation5";
        int stride = animation ? 5 : 4, x = animation ? 2 : 1, moved = 0;
        if (source.Length == 0 || source.Length != target.Length || source.Length % stride != 0)
            throw new InvalidDataException("World tent record shape");
        for (int at = 0; at < source.Length; at += stride)
        {
            bool member = animation ? TentAnimation(source[at]) : source[at].Kind == 0 && source[at].Number == 19259;
            if (member)
            {
                moved++;
                if (source[at + x].Number is < 448 or > 896 || source[at + x + 1].Number is < 0 or > 360 ||
                    animation && source[at + 1].Text != "[bottom]")
                    throw new InvalidDataException("World tent member bounds");
            }
            for (int n = 0; n < stride; n++)
            {
                Atom a = source[at + n], b = target[at + n];
                if (a.Kind != (animation && n < 2 ? 6 : 0) || b.Kind != a.Kind)
                    throw new InvalidDataException("World tent token shape");
                if (a.Kind == 0 && a.Number is < -1048576 or > 1048352)
                    throw new InvalidDataException("World tent coordinate bounds");
                if (member && n == x)
                {
                    if (b.Number != a.Number + 224) throw new InvalidDataException("World tent translation");
                }
                else if (a != b) throw new InvalidDataException("World tent unrelated field changed");
            }
        }
        if (moved != (animation ? 1 : 7)) throw new InvalidDataException("World tent incomplete cohort");
    }

    private static void ValidateTentPlan(IReadOnlyList<Rule> rules)
    {
        Rule? animation = rules.SingleOrDefault(rule => rule.Kind == "dungeon_tent_animation5");
        Rule? objects = rules.SingleOrDefault(rule => rule.Kind == "dungeon_tent_passive4");
        if (animation is null && objects is null) return;
        Rule? gate = rules.SingleOrDefault(rule => rule.Kind == "gate_points8");
        if (animation is null || objects is null || gate is null || rules.Count(rule => rule.Kind == "dungeon_tiles") != 1)
            throw new InvalidDataException("World tent requires paired actors, gate and four-to-five floor");
        HashSet<(int X, int Y)> expected = [(-17,10),(35,54),(144,34),(144,116),(246,62),(257,143),(71,2)];
        for (int p = 0; p < 3; p++)
        {
            Atom[] a = animation.From[p];
            int at = 0;
            while (at < a.Length && !TentAnimation(a[at])) at += 5;
            if (at == a.Length) throw new InvalidDataException("World tent missing animation");
            int x = a[at + 2].Number, y = a[at + 3].Number;
            Atom[] g = gate.From[p];
            if (g.Length != 8 || (g[2].Number != x + 55 && g[2].Number != x + 55 + 224) || g[3].Number != y + 91 ||
                gate.To[2].Number != x + 55 + 224 || gate.To[3] != g[3])
                throw new InvalidDataException("World tent gate relation");
            HashSet<(int X, int Y)> actual = [];
            Atom[] o = objects.From[p];
            int count = 0;
            for (int n = 0; n < o.Length; n += 4)
                if (o[n].Number == 19259) { count++; actual.Add((o[n + 1].Number - x,o[n + 2].Number - y)); }
            if (count != 7 || !actual.SetEquals(expected)) throw new InvalidDataException("World tent physical cohort relation");
        }
    }
}
