using ProbablyStolenStockMarket.Market;
using System.Text.Json.Nodes;

static class EventShockTimingTests
{
    static MarketEngine Engine(ulong seed = 1, decimal chance = 1m)
    {
        var config = TestConfig.Create(); config.DailyLimit = 0m; config.EventPreheatChance = chance;
        config.Events.Clear();
        config.Events.Add(new EventRule { EventId = "timing", Description = "公开供需事件", Impacts = new() { ["water"] = .1m, ["medical"] = -.1m } });
        var engine = new MarketEngine(config); engine.AdvanceDay(1); engine.State.RandomState = seed;
        return engine;
    }
    static EventLifecycleState Life(MarketEngine e) => e.State.EventLifecycles.Values.Single();
    static MarketEngine Clone(MarketEngine e) => new(MarketConfig.Deserialize(e.Config.Serialize()), MarketEngine.Deserialize(e.Serialize(), e.Config));
    static (MarketEngine engine, ulong seed) Plan(Func<int, bool> accepts, decimal chance = 1m, int end = 6, bool visible = true)
    {
        for (ulong seed = 1; seed < 10000; seed++)
        {
            var engine = Engine(seed, chance); engine.ActivateEvent("timing", "one", 1, visible, end);
            if (accepts(Life(engine).ShockDay)) return (engine, seed);
        }
        throw new Exception("测试种子未覆盖指定冲击日");
    }
    static bool Near(decimal a, decimal b) => Math.Abs(a - b) < .000000001m;
    public static void Run(Action<bool, string> check)
    {
        var p = Plan(d => d == 4);
        var e = p.engine; var life = Life(e);
        check(life.ShockDay == 4 && !life.ShockApplied && life.ShockPlanVersion == 1 && e.State.Companies["water"].Price == 100.5m && e.State.Companies["medical"].Price == 99.5m, "延迟大跳前可按同一事件逐公司方向轻微预热");
        check(e.State.OccurredEvents.Contains("6:timing:one") && e.State.ObservedEventDirections["6:timing:one"]["water"] == 1 && e.State.ObservedEventDirections["6:timing:one"]["medical"] == -1, "延迟股价冲击不延迟原事件事实及情报验证方向登记");
        var saved = e.Serialize(); var copy = Clone(e);
        check(copy.Serialize() == saved && !e.ActivateEvent("timing", "one", 1, true, 6) && !e.AdvanceDay(1) && e.Serialize() == saved, "等待冲击的选日种子价格与当日预热随档固定且重入不重抽");
        for (var day = 2; day < 4; day++)
        {
            var previous = e.State.Companies["water"].Price; e.AdvanceDay(day); copy.AdvanceDay(day);
            check(e.State.Companies["water"].Price > previous && e.State.Companies["water"].Price <= previous * 1.005m && e.State.Companies["water"].Price == copy.State.Companies["water"].Price, "预热日只有轻微同向影响且读档后保持同一路径");
        }
        var before = e.State.Companies["water"].Price; var used = life.Companies["water"].ContinuationUsed;
        e.Config.Events[0].Impacts["water"] = .4m;
        e.AdvanceDay(4);
        check(life.ShockApplied && e.State.Companies["water"].Price == decimal.Round(before * 1.1m, 2, MidpointRounding.AwayFromZero) && life.Companies["water"].ContinuationUsed == used && life.Companies["water"].AppliedInitialImpact > .09m, "计划日只执行注册时的冲击快照一次且不另消耗预热续幅预算");
        check(life.Companies["medical"].AppliedInitialImpact < -.09m && e.State.Companies["water"].IntradayPoints.Count == 3, "同一次事件全部公司共享大跳日并只追加一次实际冲击采样");
        saved = e.Serialize(); check(!e.AdvanceDay(4) && !e.ActivateEvent("timing", "one", 1) && e.Serialize() == saved, "大跳后重复日期与激活回调不能再次施加冲击");
        copy = Clone(e); e.AdvanceDay(5); copy.AdvanceDay(5);
        check(e.Serialize() == copy.Serialize() && e.State.Companies["water"].Price <= decimal.Round(before * 1.1m, 2, MidpointRounding.AwayFromZero) * 1.0075m, "大跳以后从下一日延续且读档恢复不重跳");

        e = Plan(d => d == 5).engine; life = Life(e);
        before = e.State.Companies["water"].Price;
        e.EndEvent("timing", "one", 1);
        check(life.ShockCancelled && !life.ShockApplied && life.RecoveryTargetFrozen && Near(life.Companies["water"].RetainedTargetFactor, 1m + .005m * life.RetentionRatio), "提前真实结束取消未来冲击并仅按真实预热偏移冻结保留目标");
        e.AdvanceDay(100);
        check(e.State.EventLifecycles.Count == 0 && e.State.Companies["water"].Price > 100m && e.State.Companies["water"].Price < before && e.State.Companies["water"].History.All(h => h.Price < 102m), "提前结束后不补大跳且预热只修复部分并保留永久偏移");

        e = Plan(d => d == 4, chance: 0m).engine;
        check(e.State.Companies["water"].Price == 100m && e.State.Companies["water"].IntradayPoints.Count == 2, "未触发预热时事件生效当天不生成虚假变价采样");
        e.AdvanceDay(3); check(e.State.Companies["water"].Price == 100m && !Life(e).ShockApplied, "无预热时计划日前价格不受事件影响");
        e.AdvanceDay(4); check(e.State.Companies["water"].Price == 110m && Life(e).ShockApplied, "不预热仍会在已保存的未来日期执行一次大跳");

        e = Plan(d => d == 4, chance: 0m).engine; e.Config.EventPreheatChance = 1m;
        copy = Clone(e); e.AdvanceDay(3); copy.AdvanceDay(3);
        check(e.State.Companies["water"].Price == 100m && e.Serialize() == copy.Serialize(), "已保存预热概率与种子不会因配置修改或读档改变原计划");
        e = Plan(d => d == 10, end: 20).engine; e.AdvanceDay(9);
        var budgetUsed = Life(e).Companies["water"].ContinuationUsed;
        check(budgetUsed == .025m && e.State.Companies["water"].Price == 102.5m, "连续预热最多使用初始冲击幅度四分之一的小幅总预算");
        e.AdvanceDay(10); before = e.State.Companies["water"].Price; e.AdvanceDay(11);
        check(e.State.Companies["water"].Price == before && Life(e).Companies["water"].ContinuationUsed == budgetUsed, "预热耗尽预算后大跳仍执行且冲击后续幅不能再次获得预算");

        p = Plan(d => d > 1, visible: false); e = p.engine;
        check(e.State.Companies["water"].History.All(h => !h.Reason.Contains("timing") && !h.Reason.Contains("公开供需") && !h.Reason.Contains("计划")) && e.State.Ledger.All(l => !l.Description.Contains("公开供需")), "隐藏事件的预热原因和公开流水不泄漏标识标题或未来计划");
        var control = Clone(e); control.State.EventLifecycles.Clear();
        e.AdvanceDay(2); control.AdvanceDay(2);
        check(e.State.RandomState == control.State.RandomState, "预热按种子与日期确定而不额外消耗每日随机流");

        p = Plan(d => d == 4); e = p.engine; copy = Clone(e);
        e.SyncActiveEvents(Array.Empty<ActiveMarketEvent>(), 12);
        for (var day = 2; day <= 12; day++) copy.SyncActiveEvents(day < 6 ? new[] { new ActiveMarketEvent { EventId = "timing", OccurrenceKey = "one", StartDay = 1, ExpectedEndDayExclusive = 6 } } : Array.Empty<ActiveMarketEvent>(), day);
        check(e.State.Companies.All(c => c.Value.Price == copy.State.Companies[c.Key].Price) && Life(e).ShockApplied && Near(Life(e).Companies["water"].CurrentFactor, Life(copy).Companies["water"].CurrentFactor), "跨漏观测多日补齐活动期预热和计划大跳再恢复与逐日观察一致");

        p = Plan(d => d > 1, end: 20); e = p.engine;
        for (var i = 0; i < 4; i++) { e.State.RandomState = p.seed; e.ActivateEvent("timing", "another" + i, 1, true, 20); }
        check(e.State.Companies["water"].Price <= 101m && e.State.Companies["medical"].Price >= 99m && e.State.EventLifecycles.Values.All(l => !l.ShockApplied), "同一天分批接入多个事件的预热共用百分之一合并限额");
        copy = Clone(e); var capped = e.State.Companies["water"].Price; copy.State.RandomState = p.seed; copy.ActivateEvent("timing", "after-load", 1, true, 20);
        check(copy.State.Companies["water"].Price == capped, "日内累计小幅额度随档保存不会被读档后的新激活绕过");
        var reducedConfig = MarketConfig.Deserialize(e.Config.Serialize()); reducedConfig.EventDailyDriftLimit = .005m;
        var reduced = new MarketEngine(reducedConfig, MarketEngine.Deserialize(e.Serialize(), reducedConfig));
        var oldWater = reduced.State.Companies["water"].Price; var oldMedical = reduced.State.Companies["medical"].Price;
        reduced.State.RandomState = p.seed; reduced.ActivateEvent("timing", "smaller-limit", 1, true, 20);
        check(oldWater == 101m && oldMedical == 99m && reduced.State.Companies["water"].Price == oldWater && reduced.State.Companies["medical"].Price == oldMedical, "同日读档降低小幅上限后同向预热既不反向追回历史也不加深已有超限");

        var seen = new HashSet<int>();
        for (ulong seed = 1; seed <= 256; seed++) { var sample = Engine(seed); sample.ActivateEvent("timing", "window", 1, true, 4); seen.Add(Life(sample).ShockDay); }
        check(seen.SetEquals(new[] { 1, 2, 3 }), "有限事件抽签可覆盖包含接入日及结束前末日在内的整个合法区间");
        e = Engine(); e.ActivateEvent("timing", "unknown", 1); check(Life(e).ShockDay >= 1 && Life(e).ShockDay <= 5, "未知时长采用有限五日窗口而不猜测事件结束日");
        e = Engine(); e.ActivateEvent("timing", "permanent", 1, true, 2, true); check(Life(e).ShockDay >= 1 && Life(e).ShockDay <= 5, "永久事件冲击使用有限配置窗口并忽略不适用的预计结束日");
        e = Engine(); e.ActivateEvent("timing", "huge", 1, true, int.MaxValue); check(Life(e).ShockDay < int.MaxValue && Life(e).ShockDay >= 1 && e.Serialize().Length < 20000, "超长有限时长选日不截断真实区间也不预分配每日集合");

        e = Plan(d => d == 1).engine; var old = JsonNode.Parse(e.Serialize())!;
        foreach (var l in old["EventLifecycles"]!.AsObject())
            foreach (var field in new[] { "ShockPlanVersion", "ShockDay", "ShockApplied", "ShockCancelled", "PreheatSeed", "PreheatChance" }) l.Value!.AsObject().Remove(field);
        before = e.State.Companies["water"].Price;
        var migrated = new MarketEngine(e.Config, MarketEngine.Deserialize(old.ToJsonString(), e.Config));
        check(Life(migrated).ShockApplied && Life(migrated).ShockPlanVersion == 1 && migrated.State.Companies["water"].Price == before && Life(migrated).RetentionRatio == Life(e).RetentionRatio && Life(migrated).Companies["water"].CurrentFactor == Life(e).Companies["water"].CurrentFactor, "旧零点八生命周期迁移标记首冲已执行且不改变真实因子与随机保留");
        saved = migrated.Serialize(); check(!migrated.ActivateEvent("timing", "one", 1) && migrated.Serialize() == saved && Clone(migrated).Serialize() == saved, "旧首冲迁移幂等且重复激活或读档不重新规划冲击");
        e = Engine(); e.State.ProcessedEvents.Add("6:timing:old"); e.SyncActiveEvents(new[] { new ActiveMarketEvent { EventId = "timing", OccurrenceKey = "old", StartDay = 0, ExpectedEndDayExclusive = 6 } }, 1);
        check(Life(e).LegacyAdopted && Life(e).ShockApplied && e.State.Companies["water"].Price == 100m, "旧已处理事件首次接续只标记已冲击而不抽未来大跳或补旧价格");
    }
}
