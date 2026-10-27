using System.Collections.Immutable;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LukeResourceCompletionTests
{
    internal static LukeResourceCompletionProfile Profile() => new("selftest/explicit-one-box-not-original-economy",
        "fixture/current-pvf-party-card", new(DayOfWeek.Sunday, new(6, 0), TimeSpan.FromHours(8)),
        [new(0, 1, 1, 2, new(0, 0, 1, 0, 0, 0)), new(0, 2, 1, 2, new(0, 1, 1, 0, 0, 0))]);

    public static void RunCore()
    {
        int before = Count;
        RaidField Field(string tag, int value) => new(tag, 0, [new(0, value, null)]);
        var raw = new RaidScript("etc/raid/luke/luke.etc", null, null, null, ImmutableDictionary<string,int>.Empty,
            [new("P1", [], [], [new("party_card", -1, 100, 10308804, 0)]) { Declarations = [Field("[minimal dungeon clear count]", 0)] },
             new("P2", [], [], [new("party_card", -1, 100, 10304817, 0)]) { Declarations = [Field("[minimal dungeon clear count]", 2)] }],
            [Field("[weekly clear count]", 2)], []);
        ItemTemplate? Item(int id) => new(id, ItemKind.Consumable, 0, 1, 0, 10000, 1, 2, 1, "[trade]", 65535, 0, "测试原箱", "[booster] 0");
        var profile = Profile();
        var result = LukeResourceCompletion.Create(profile, raw, Item);
        That(result.WeeklyClearLimit == 2 && result.Phases.Select(p => p.MinimumCommittedRuns).SequenceEqual(new[] {0,2}),
            "资源周清上限与阶段最少通关分别读取，零门槛不当零资格次数");
        That(result.Phases.Select(p => p.Rewards.Offers.Single().Grants.Single().ItemId).SequenceEqual(new[] {10308804,10304817}) &&
            result.Phases.All(p => p.Rewards.Offers.Single().Grants.Single().Count == 1), "显式单箱策略发原箱，不拆成25/100材料");
        var changed = profile with { Phases = profile.Phases.SetItem(0, profile.Phases[0] with { BoxCount = 3, WeeklyEligibilityLimit = 4, Card = 7 }) };
        var adjusted = LukeResourceCompletion.Create(changed, raw, Item);
        That(result.Cycle == profile.Cycle && result.Cycle.Day == DayOfWeek.Sunday && result.Cycle.Time == new TimeOnly(6,0) &&
            result.Cycle.Offset == TimeSpan.FromHours(8), "调用者周锚三元组完整保留，不借用门票过期日期");
        That(adjusted.WeeklyClearLimit == 2 && adjusted.Phases[0].WeeklyEligibilityLimit == 4 &&
            adjusted.Phases[0].Rewards.Offers.Single() is { Card: 7, Grants: [{ Count: 3 }] }, "调用者数量/资格额度/卡身份明示，不从RawB推导");
        void Reject(Action action) { bool rejected=false; try {action();} catch(InvalidDataException){rejected=true;} That(rejected,"坏配置或坏资源明确拒绝，不降级空奖"); }
        foreach(var invalid in new[] { profile with {Source=""}, profile with {ResourceVersion=""}, profile with {Phases=[]},
            profile with {Cycle=new(DayOfWeek.Sunday,new(6,0),TimeSpan.FromHours(15))},
            profile with {Phases=profile.Phases.SetItem(0,profile.Phases[0] with {BoxCount=0})},
            profile with {Phases=profile.Phases.SetItem(0,profile.Phases[0] with {BoxCount=10001})},
            profile with {Phases=profile.Phases.SetItem(0,profile.Phases[0] with {Kind=2})},
            profile with {Phases=profile.Phases.SetItem(0,profile.Phases[0] with {WeeklyEligibilityLimit=0})},
            profile with {Phases=profile.Phases.SetItem(0,profile.Phases[0] with {Result=profile.Phases[1].Result})} })
            Reject(()=>LukeResourceCompletion.Create(invalid,raw,Item));
        foreach(var invalid in new[] {raw with {Headers=[]}, raw with {Headers=raw.Headers.Add(raw.Headers[0])},
            raw with {Headers=[Field("[weekly clear count]",0)]}, raw with {Issues=[new("bad",raw.Path,0,"bad")]},
            raw with {Phases=raw.Phases.SetItem(0,raw.Phases[0] with {Declarations=[]})},
            raw with {Phases=raw.Phases.SetItem(0,raw.Phases[0] with {Rewards=[]})},
            raw with {Phases=raw.Phases.SetItem(0,raw.Phases[0] with {Rewards=raw.Phases[0].Rewards.Add(raw.Phases[0].Rewards[0])})},
            raw with {Phases=raw.Phases.SetItem(0,raw.Phases[0] with {Rewards=[raw.Phases[0].Rewards[0] with {RawB=99}]})} })
            Reject(()=>LukeResourceCompletion.Create(profile,invalid,Item));
        Reject(()=>LukeResourceCompletion.Create(profile,raw,_=>null));
        Reject(()=>LukeResourceCompletion.Create(profile,raw,id=>Item(id)! with {StackableType="[material]"}));
        Reject(()=>LukeResourceCompletion.Create(profile,raw,id=>Item(id)! with {Id=id+1}));
        Reject(()=>LukeResourceCompletion.Create(profile,raw,id=>Item(id)! with {Kind=ItemKind.Material}));
        Reject(()=>LukeResourceCompletion.Create(profile,raw,id=>Item(id)! with {StackableType="[booster]"}));
        Reject(()=>LukeResourceCompletion.Create(profile,raw,id=>Item(id)! with {StackableType="[booster] 1"}));
        Reject(()=>LukeResourceCompletion.Create(profile,raw,id=>Item(id)! with {StackableType="[booster] 0 1"}));
        var policy=new LukeRaidStartPolicy("selftest",raw);
        var manual=policy with {Completion=result};
        That(ReferenceEquals(LukeResourceCompletion.Resolve(manual,null,null!),manual), "既有仅手工奖励配置原引用保留");
        That(ReferenceEquals(LukeResourceCompletion.Resolve(policy,null,null!),policy) && LukeResourceCompletion.Resolve(null,null,null!) is null,
            "默认无配置不读资源或开启Luke入口");
        Reject(()=>LukeResourceCompletion.Resolve(policy with {Completion=result,ResourceCompletion=profile},null,null!));
        Reject(()=>LukeResourceCompletion.Resolve(policy with {ResourceCompletion=profile},null,null!));
        Console.WriteLine($"PASS {Count-before} 卢克资源奖励策略定向断言");
    }
}
