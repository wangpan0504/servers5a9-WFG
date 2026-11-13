using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PreyTeamDefenceTests
{
    internal static void Run(RaidActor[] actors, RaidScript source)
    {
        var timer = new RaidTimer(1, 1000);
        RaidClause Timer(string tag) => new(tag, 0, null, null, null, timer,
            tag == "[set timer]" ? [1, 1000, 15] : [1, 1000]);
        var move = new RaidClause("[slenikon]", 0, null, null, null, null, []);
        var phase = source.Phases[0] with { Rules = [.. source.Phases[0].Rules,
            new([new("[if]", 0, null, "[prepare raid]", null, null, [1])], [Timer("[set timer]"), move]),
            new([Timer("[check timer end]")], [Timer("[set timer]"), move])] };
        var script = source with { Phases = source.Phases.SetItem(0, phase) };
        var notices = actors.Select(actor => actor.Session.RaidBattleNoticeArrived).ToArray();
        try
        {
            foreach (var actor in actors) actor.Session.RaidBattleNoticeArrived = null;
            var battle = Create();
            var landing = battle.CapturePreyLanding(battle.View, 5)!;
            int hp = Hp(battle);
            battle.Publish(battle.View);
            That(Hp(battle) == hp - 1300 && battle.IsPreyLandingCurrent(landing),
                "无人入场在当次到站发布结束立即扣血，不等下一段15秒且不清除阻击");
            var penalized = battle.View;
            That(penalized.PreyWarningEvent == 1 && battle.PreyWarningCurrent(penalized),
                "真实阻击碰撞登记一次原生警报事件，扣血未破甲也触发红图标和音效");
            battle.Publish(penalized); battle.Publish(penalized);
            That(ReferenceEquals(penalized, battle.View) && battle.View.PreyWarningEvent == 1,
                "同次到站重复发布不重复漏守扣血或重放警报事件");
            for (int step = 0; step < 16; step++) Step(battle);
            That(battle.CapturePreyLanding(battle.View, 5) is { } next && next.Arrival.Generation != landing.Arrival.Generation,
                "未通关阻击保留到下一圈，签发新的到站身份");
            battle.Publish(battle.View);
            That(Hp(battle) == hp - 2600 && battle.View.PreyWarningEvent == 2,
                "下圈无人成功进入再次惩罚同一未通关阻击，签发独立警报事件");

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            IDisposable? first = null, second = null;
            actors[0].Session.RaidBattleNoticeArrived = (_, _) => first ??= battle.TrackPreyDefenceLanding(landing);
            actors[1].Session.RaidBattleNoticeArrived = (_, _) => second ??= battle.TrackPreyDefenceLanding(landing);
            hp = Hp(battle);
            battle.Publish(battle.View);
            That(first is not null && second is not null && Hp(battle) == hp, "发布登记全部到站尝试，不能抢在正式入场结果前误罚");
            first!.Dispose();
            That(Hp(battle) == hp, "一队失败时仍等待另一队本次到站的入场结果");
            second!.Dispose(); second!.Dispose();
            That(Hp(battle) == hp - 1300, "所有入场失败当次立即扣一次，重复结束不重复扣血");
            foreach (var actor in actors) actor.Session.RaidBattleNoticeArrived = null;

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            hp = Hp(battle);
            using (var early = battle.TrackPreyDefenceLanding(landing))
            {
                That(early is not null, "直接668投影早于战况发布也能登记本次到站尝试");
                battle.Publish(battle.View);
                That(Hp(battle) == hp, "先排队后发布仍等待同一到站尝试，不误判无人");
            }
            That(Hp(battle) == hp - 1300, "提前登记的入场失败后仍只处罚一次");

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            hp = Hp(battle);
            using (battle.TrackPreyDefenceLanding(landing)) { }
            That(Hp(battle) == hp, "早于发布结束的失败尝试仍等待完整通知，不抢判后续小队");
            battle.Publish(battle.View);
            That(Hp(battle) == hp - 1300, "完整发布发现没有成功入场后当次处罚");

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            hp = Hp(battle);
            using (var failedEntry = battle.TrackPreyDefenceLanding(landing))
            {
                Guid failedRun = Guid.NewGuid();
                using var uninstalled = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
                    { PreyGuide = false, PreyPartyId = 700 }, checked((uint)landing.Dungeon),
                    [(actors[0], new(actors[0].Character, 0, 1, failedRun, 1))]);
                That(failedEntry is not null && uninstalled is not null, "入场失败夹具保留未安装的正式Run预留");
                battle.Publish(battle.View);
                That(Hp(battle) == hp, "尚在尝试中的预留不提前判罚");
            }
            That(Hp(battle) == hp - 1300, "入场未提交安装的预留释放后当次漏守，预留不冒充成功");

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            first = null;
            actors[0].Session.RaidBattleNoticeArrived = (_, _) => throw new IOException("fixture recipient failure");
            actors[1].Session.RaidBattleNoticeArrived = (_, _) => first ??= battle.TrackPreyDefenceLanding(landing);
            hp = Hp(battle);
            bool reported = false;
            try { battle.Publish(battle.View); }
            catch (IOException) { reported = true; }
            That(reported && first is not null && Hp(battle) == hp,
                "前一接收者异常仍通知后续队长并等待其尝试，不能提前判漏守");
            foreach (var actor in actors) actor.Session.RaidBattleNoticeArrived = null;
            first!.Dispose();
            That(Hp(battle) == hp - 1300, "异常接收者不改变剩余入场失败后的单次裁定");

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            first = null;
            actors[0].Session.RaidBattleNoticeArrived = (_, _) => first ??= battle.TrackPreyDefenceLanding(landing);
            hp = Hp(battle);
            battle.Publish(battle.View);
            Guid instance = Guid.NewGuid();
            var rows = actors.Take(2).Select(actor => (Actor: actor,
                Identity: new DungeonRunIdentity(actor.Character, 0, 1, instance, 1))).ToArray();
            using var reservation = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
                { PreyGuide = false, PreyPartyId = 701 }, checked((uint)landing.Dungeon), rows);
            That(reservation is not null, "当前阻击允许正式Run预留");
            That(first is not null && Hp(battle) == hp, "预留尚未安装只代表入场进行中");
            reservation!.Install();
            first!.Dispose();
            That(Hp(battle) == hp, "真实Run成功安装后本次到站不罚");
            foreach (var actor in actors) actor.Session.RaidBattleNoticeArrived = null;
            for (int step = 0; step < 16; step++) Step(battle);
            battle.Publish(battle.View);
            That(Hp(battle) == hp, "下一圈仍有有效未清图队伍驻守时不罚");
            battle.ReleaseDungeon(actors[0], instance); battle.ReleaseDungeon(actors[1], instance);
            That(Hp(battle) == hp - 1300 && battle.CapturePreyLanding(battle.View, 5)?.Dungeon == landing.Dungeon,
                "未通关撤退按原失败扣血但保留阻击，仍可继续进入");

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            first = null;
            actors[0].Session.RaidBattleNoticeArrived = (_, _) => first ??= battle.TrackPreyDefenceLanding(landing);
            battle.Publish(battle.View);
            instance = Guid.NewGuid();
            rows = [(actors[0], new(actors[0].Character, 0, 1, instance, 1))];
            using (var entered = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
                { PreyGuide = false, PreyPartyId = 702 }, checked((uint)landing.Dungeon), rows))
            {
                That(entered is not null, "同站退图竞态建立正式预留");
                entered!.Install();
                hp = Hp(battle);
                battle.ReleaseDungeon(actors[0], instance);
                first!.Dispose();
                That(Hp(battle) == hp - 1300, "成功入场后先撤退再结束到站任务只扣原失败血，不重复漏守");
            }
            foreach (var actor in actors) actor.Session.RaidBattleNoticeArrived = null;

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            first = null;
            actors[0].Session.RaidBattleNoticeArrived = (_, _) => first ??= battle.TrackPreyDefenceLanding(landing);
            battle.Publish(battle.View);
            Step(battle);
            hp = Hp(battle);
            first!.Dispose();
            That(Hp(battle) == hp, "离站后的旧入场完成不能惩罚新到站状态");
            actors[0].Session.RaidBattleNoticeArrived = null;

            battle = Create();
            landing = battle.CapturePreyLanding(battle.View, 5)!;
            var firstIdentity = new DungeonRunIdentity(actors[0].Character, 0, 1, Guid.NewGuid(), 1);
            using var firstArea = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
                { PreyGuide = false, PreyPartyId = 710 }, checked((uint)landing.Dungeon), [(actors[0], firstIdentity)], landing: landing);
            That(firstArea is not null, "第一处阻击按真实到站区域安装");
            firstArea!.Install();
            int otherSlot = Enumerable.Range(1, 16).First(slot => battle.View.Symbols[$"[area_{slot}]"] == 0);
            battle.SetMechanism(battle.Instance, battle.View.Revision, $"[area_{otherSlot}]", landing.Dungeon);
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[slenikon from area]", otherSlot + 300);
            var otherLanding = battle.CapturePreyLanding(battle.View, 5)!;
            var otherIdentity = new DungeonRunIdentity(actors[1].Character, 0, 1, Guid.NewGuid(), 1);
            using var otherArea = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
                { PreyGuide = false, PreyPartyId = 711 }, checked((uint)landing.Dungeon), [(actors[1], otherIdentity)], landing: otherLanding);
            That(otherArea is not null && otherLanding.Arrival.FromArea != landing.Arrival.FromArea,
                "同模板在另一格的阻击可独立入场，不共享PartyMax占用");
            otherArea!.Install();
            using var sameArea = battle.ReserveDungeon(new(battle.Instance, 0, 0, RaidKind.Prey)
                { PreyGuide = false, PreyPartyId = 712 }, checked((uint)landing.Dungeon),
                [(actors[2], new(actors[2].Character, 0, 1, Guid.NewGuid(), 1))], landing: otherLanding);
            That(sameArea is null, "同区域的原人数上限仍生效，不能重复入场");
            var clear = new DungeonClearFact(firstIdentity, checked((uint)landing.Dungeon), [Guid.NewGuid()])
                { ClearMap = new DungeonMap("boss", new(0, 0), 77, "fixture/prey-defence.map") };
            That(battle.CompletePreyDungeon([(actors[0], clear)]) is not null &&
                battle.View.Symbols[$"[area_{landing.Arrival.FromArea - 300}]"] == 0 &&
                battle.View.Symbols[$"[area_{otherSlot}]"] == landing.Dungeon,
                "完成第一格只清除该Run原区域，保留另一格相同怪物及正在战斗的队伍");

            battle = Create();
            battle.SetMechanism(battle.Instance, battle.View.Revision, "[slenikon hp]", 1300);
            battle.Publish(battle.View);
            That(Hp(battle) == 0 && battle.View.PhaseExpired && battle.View.Timers.IsEmpty,
                "当次漏守耗尽船体沿既有失败关闭入口和时钟");
        }
        finally
        {
            for (int index = 0; index < actors.Length; index++) actors[index].Session.RaidBattleNoticeArrived = notices[index];
        }

        AntonRaidBattle Create()
        {
            var battle = new AntonRaidBattle(script, kind: RaidKind.Prey, preyRandom: new Random(7), preyGuide: false);
            foreach (var actor in actors.Take(3)) That(battle.JoinStart(actor), "漏守测试复用三个当前成员");
            That(battle.ExpireLukeTimer(battle.Instance, new(9000, 0), battle.View.TimerGenerations[new(9000, 0)]) is not null,
                "漏守测试完成准备");
            for (int step = 0; step < 16 && battle.CapturePreyLanding(battle.View, 5) is null; step++) Step(battle);
            That(battle.CapturePreyLanding(battle.View, 5) is not null, "沿原航线到达已存在的阻击");
            return battle;
        }

        void Step(AntonRaidBattle battle) => That(battle.ExpireLukeTimer(battle.Instance, timer,
            battle.View.TimerGenerations[timer]) is not null, "漏守测试推进一次原代航行计时器");
        static int Hp(AntonRaidBattle battle) => battle.View.Symbols["[slenikon hp]"];
    }
}
