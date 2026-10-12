using System.Collections.Immutable;

namespace PvfLib;

public sealed record PvfTutorialAction(ImmutableArray<int> Cinematics, ImmutableArray<int> DestroySelectedMonsters);
public sealed record PvfTutorialCinematic(int MapId, ImmutableArray<int> MonsterActors, ImmutableArray<int> DestroyTargets);

public static class TutorialSceneFile
{
    sealed class Actor(bool subject)
    {
        public bool Subject { get; } = subject;
        public bool Monster { get; set; }
        public bool Group { get; set; }
        public int Index { get; set; } = -1;
        public HashSet<int> Members { get; } = [];
    }
    public static string? BasicAction(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        string? result = null;
        for (int at = 0; at < tokens.Count; at++)
        {
            if (Tag(tokens[at], resolve) != "basic action") continue;
            if (result is not null || at + 1 >= tokens.Count || tokens[at + 1].Type != 6)
                throw new InvalidDataException("地图basic action重复或不是字符串");
            result = resolve(tokens[++at].Value);
        }
        return result;
    }

    public static PvfTutorialAction Action(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var cinematics = new HashSet<int>();
        var destroyBehaviors = new HashSet<int>();
        var selectors = new List<(int[] Monsters, int[] Behaviors)>();
        var monsters = new HashSet<int>(); var behaviors = new HashSet<int>();
        bool trigger = false, behavior = false, monsterSelector = false, doBehavior = false;
        int ordinal = -1;
        for (int at = 0; at < tokens.Count; at++)
        {
            string tag = Tag(tokens[at], resolve);
            switch (tag)
            {
                case "trigger" when !behavior:
                    trigger = true; monsterSelector = false; doBehavior = false; monsters.Clear(); behaviors.Clear(); break;
                case "/trigger" when trigger:
                    selectors.Add(([.. monsters], [.. behaviors])); trigger = false; break;
                case "behavior": behavior = true; ordinal++; break;
                case "/behavior": behavior = false; break;
                case "monster" when trigger: monsterSelector = true; break;
                case "is index" when trigger && monsterSelector:
                    for (int index = at + 1; index < tokens.Count && tokens[index].Type == 0; index++) monsters.Add(tokens[index].Value);
                    break;
                case "do behavior": case "delay do behavior": if (trigger) doBehavior = true; break;
                case "me": case "checkup object":
                    if (trigger && doBehavior && at + 1 < tokens.Count && tokens[at + 1].Type == 0) behaviors.Add(tokens[at + 1].Value);
                    break;
                case "destroy" when behavior: destroyBehaviors.Add(ordinal); break;
                case "cinematic" when behavior:
                    if (at + 1 >= tokens.Count || tokens[at + 1].Type != 0 || tokens[at + 1].Value <= 0)
                        throw new InvalidDataException("训练动作CINEMATIC索引无效");
                    cinematics.Add(tokens[at + 1].Value); break;
            }
        }
        return new(cinematics.Order().ToImmutableArray(), selectors.Where(row => row.Behaviors.Any(destroyBehaviors.Contains))
            .SelectMany(row => row.Monsters).Distinct().Order().ToImmutableArray());
    }

    public static PvfTutorialCinematic Cinematic(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int map = 0;
        bool scene = false, behavior = false, subjectCaptured = false;
        var stack = new Stack<Actor>(); var subject = new HashSet<int>();
        var actors = new HashSet<int>(); var destroyed = new HashSet<int>();
        for (int at = 0; at < tokens.Count; at++)
        {
            string tag = Tag(tokens[at], resolve);
            switch (tag)
            {
                case "map" when !scene:
                    if (at + 1 >= tokens.Count || tokens[at + 1].Type != 0) throw new InvalidDataException("CMT地图身份无效");
                    map = tokens[at + 1].Value; break;
                case "scene": scene = true; break;
                case "behavior": behavior = true; subjectCaptured = false; subject.Clear(); stack.Clear(); break;
                case "/behavior":
                    if (stack.Count != 0) throw new InvalidDataException("训练CMT的ACTOR未闭合");
                    behavior = false; break;
                case "actor" when behavior:
                    if (stack.Count >= 32) throw new InvalidDataException("训练CMT的ACTOR嵌套超过32层");
                    stack.Push(new(stack.Count == 0 && !subjectCaptured)); break;
                case "type" when stack.Count > 0:
                    if (at + 1 >= tokens.Count || tokens[at + 1].Type != 3)
                        throw new InvalidDataException("CMT演员TYPE枚举缺失");
                    string type = Tag(tokens[++at], resolve);
                    stack.Peek().Monster = type == "monster"; stack.Peek().Group = type == "group"; break;
                case "index" when stack.Count > 0 && stack.Peek().Index < 0:
                    if (at + 1 >= tokens.Count || tokens[at + 1].Type != 0) throw new InvalidDataException("CMT演员索引无效");
                    stack.Peek().Index = tokens[at + 1].Value; break;
                case "/actor" when stack.Count > 0:
                    var actor = stack.Pop();
                    var members = actor.Group ? actor.Members : actor.Monster && actor.Index >= 0 ? new HashSet<int> { actor.Index } : [];
                    actors.UnionWith(members);
                    if (stack.Count > 0 && stack.Peek().Group) stack.Peek().Members.UnionWith(members);
                    if (actor.Subject) { subjectCaptured = true; subject.UnionWith(members); }
                    break;
                case "destroy" when behavior: destroyed.UnionWith(subject); break;
            }
        }
        if (stack.Count != 0 || behavior) throw new InvalidDataException("训练CMT行为或ACTOR未闭合");
        return new(map, actors.Order().ToImmutableArray(), destroyed.Order().ToImmutableArray());
    }
    static string Tag(PvfToken token, Func<int, string> resolve) => token.Type == 3
        ? resolve(token.Value).Trim('[', ']').ToLowerInvariant() : "";
}
