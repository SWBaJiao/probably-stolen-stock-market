using MelonModTemplate.Market;
using System.Text.Json;
using System.Text.Json.Nodes;

static class EventLifecycleTests
{
    static MarketEngine Engine()
    {
        var c = MarketConfig.CreateDefault(); c.DailyLimit = 0m;
        c.Events.Clear();
        c.Events.Add(new EventRule { EventId = "rise", Description = "公开上行事件", Impacts = new() { ["water"] = .1m, ["medical"] = .05m } });
        c.Events.Add(new EventRule { EventId = "fall", Description = "不可公开的下行事件名", Impacts = new() { ["water"] = -.1m } });
        var e = new MarketEngine(c); e.ApplyStorageAccess(false); e.AdvanceDay(1); return e;
    }
    static MarketEngine Clone(MarketEngine e) { var c = MarketConfig.Deserialize(e.Config.Serialize()); return new MarketEngine(c, MarketEngine.Deserialize(e.Serialize(), c)); }
    static ActiveMarketEvent Native(string id = "rise", string key = "one", int? end = 5, bool permanent = false, bool visible = true) => new() { EventId = id, OccurrenceKey = key, StartDay = 1, ExpectedEndDayExclusive = end, IsPermanent = permanent, IsPublic = visible };
    static EventLifecycleState Life(MarketEngine e, string id = "rise", string key = "one") => e.State.EventLifecycles.Values.Single(l => l.EventId == id && l.OccurrenceKey == key);
    static bool Near(decimal a, decimal b, decimal epsilon = .00000000001m) => Math.Abs(a - b) <= epsilon;
    static void RoundTrip(Action<bool, string> check, MarketEngine e, string label)
    {
        var json = e.Serialize(); var copy = Clone(e);
        check(copy.Serialize() == json, label + "保存读取保持价格因子随机状态与进度");
    }
    public static void Run(Action<bool, string> check)
    {
        var undated = new MarketEngine(MarketConfig.CreateDefault());
        var empty = undated.Serialize();
        check(!undated.ActivateEvent("badNutrifruitHarvest", "unknown-day", 0) && undated.Serialize() == empty, "无游戏日期时拒绝事件激活而不留下不可恢复的生命周期");
        var e = Engine(); e.ActivateEvent("rise", "one", 1, true, 5);
        var life = Life(e); var retention = life.RetentionRatio;
        check(e.State.Companies["water"].Price == 110m && e.State.Companies["medical"].Price == 105m && life.Companies["water"].CurrentFactor == 1.1m, "事件首日按原配置一次大跳并记录真实首冲因子");
        check(retention >= .2m && retention <= .5m && Near(life.Companies["water"].RetainedTargetFactor, 1m + .1m * retention) && Near(life.Companies["medical"].RetainedTargetFactor, 1m + .05m * retention), "每次事件随机保留比例在区间内且全部公司共用同一次抽样");
        var activated = e.Serialize();
        check(!e.ActivateEvent("rise", "one", 1, true, 5) && e.Serialize() == activated && !e.AdvanceDay(1), "重复激活与首日重复推进不重冲击不重抽保留比例");
        RoundTrip(check, e, "首冲后");
        e.AdvanceDay(2);
        check(e.State.Companies["water"].Price > 110m && e.State.Companies["water"].Price <= 110m * 1.0075m && life.Companies["water"].ContinuationUsed > 0 && e.State.Companies["water"].History.Last().Reason.Contains("持续影响"), "持续期从下一日轻微同方向延续且单事件不超过千分之七点五");
        var currentAtEnd = life.Companies["water"].CurrentFactor;
        var priceAtEnd = e.State.Companies["water"].Price;
        check(e.EndEvent("rise", "one", 2) && e.State.Companies["water"].Price == priceAtEnd && Near(life.Companies["water"].RetainedTargetFactor, 1m + (currentAtEnd - 1m) * retention), "结束当天不跳价且目标按首冲与已生效延续的总偏移冻结");
        var ended = e.Serialize();
        check(!e.ActivateEvent("rise", "one", 2, true, 5) && e.Serialize() == ended && !e.EndEvent("rise", "one", 2), "结束后的重复激活或结束回调不能复活事件或重置恢复目标");
        RoundTrip(check, e, "刚结束时");
        e.AdvanceDay(3);
        check(e.State.Companies["water"].Price < priceAtEnd && e.State.Companies["water"].Price >= priceAtEnd * .99m && e.State.Companies["water"].History.Last().Reason.Contains("余波修复"), "下一游戏日才开始轻微回归且不超过合并日限");
        RoundTrip(check, e, "修复中");
        e.AdvanceDay(100);
        check(e.State.EventLifecycles.Count == 0 && e.State.CompletedEventLifecycles.Count == 1 && e.State.Companies["water"].PersistentEventFactor > 1m && Near(e.GetEventAdjustedAnchor("water"), e.Config.InitialPrice * e.State.Companies["water"].PersistentEventFactor), "修复完成后仅部分回归并将真实剩余溢价滚入永久估值锚点");
        RoundTrip(check, e, "修复完成后");
        var finished = e.Serialize();
        check(!e.SyncActiveEvents(new[] { Native() }, 100) && e.Serialize() == finished, "已完成事件的重复活动条目不会重建生命周期或再次抽样");
        var unanchored = Clone(e); unanchored.State.Companies["water"].PersistentEventFactor = 1m;
        e.Config.DailyLimit = .03m; unanchored.Config.DailyLimit = .03m;
        e.AdvanceDay(300); unanchored.AdvanceDay(300);
        check(e.GetEventAdjustedAnchor("water") > 100m && e.State.Companies["water"].Price > unanchored.State.Companies["water"].Price, "后续日常估值回归围绕保留溢价锚点而不会自动擦除永久效应");

        e = Engine(); e.ActivateEvent("fall", "one", 1, false, 3);
        var low = e.State.Companies["water"].Price; e.AdvanceDay(2);
        check(e.State.Companies["water"].Price < low && e.State.Companies["water"].History.Last().Reason.StartsWith("市场异动") && !e.State.Companies["water"].History.Last().Reason.Contains("不可公开") && !e.State.Companies["water"].History.Last().Reason.Contains("fall"), "利空持续时继续轻微下跌且分时原因不泄露隐藏事件名或标识");
        e.AdvanceDay(100);
        check(e.State.EventLifecycles.Count == 0 && e.State.Companies["water"].PersistentEventFactor < 1m && e.GetEventAdjustedAnchor("water") < 100m, "利空恢复后保留永久折价而不完全回到原始估值");

        e = Engine(); e.ActivateEvent("rise", "A", 1); e.ActivateEvent("rise", "B", 1);
        var prior = e.State.Companies["water"].Price; e.AdvanceDay(2);
        var first = Life(e, "rise", "A"); var second = Life(e, "rise", "B");
        check(e.State.EventLifecycles.Count == 2 && e.State.EventRetentions.Count == 2 && e.State.Companies["water"].Price > prior && e.State.Companies["water"].Price <= prior * 1.01m && first.Companies["water"].ContinuationUsed + second.Companies["water"].ContinuationUsed > 0, "同标识不同发生实例分别跟踪且两个同向续幅合计不超过百分之一");
        e.EndEvent("rise", "A", 2); e.AdvanceDay(3);
        check(!first.NativeActive && second.NativeActive && first.Companies["water"].CurrentFactor <= first.Companies["water"].AppliedInitialImpact + 1.03m, "一个事件结束只恢复自身偏移不会结束另一实例");
        e.EndEvent("rise", "B", 3); e.AdvanceDay(120);
        check(e.State.EventLifecycles.Count == 0 && e.State.CompletedEventLifecycles.Count == 2 && e.State.Companies["water"].PersistentEventFactor > 1m, "两个同向事件先后结束后分别保留偏移并完成归档");

        e = Engine(); e.ActivateEvent("rise", "one", 1); e.ActivateEvent("fall", "one", 1);
        check(Near(e.GetEventAdjustedAnchor("water"), 99m), "涨跌事件的独立乘数允许互相抵消而不覆盖彼此");
        e.EndEvent("fall", "one", 1); var baseline = Clone(e); baseline.Config.EventDailyDriftLimit = 0;
        e.RecordTrade("ordinary", "sale", new[] { "water" }, 1000m); baseline.RecordTrade("ordinary", "sale", new[] { "water" }, 1000m);
        e.Config.DailyLimit = .03m; baseline.Config.DailyLimit = .03m;
        e.AdvanceDay(2); baseline.AdvanceDay(2);
        check(Math.Abs(e.State.Companies["water"].Price / baseline.State.Companies["water"].Price - 1m) <= .01m && Life(e, "rise").NativeActive && !Life(e, "fall").NativeActive, "日常成交结算后生命周期仅按自身因子比例调整且合并限幅独立生效");

        var jumped = Engine(); jumped.SyncActiveEvents(new[] { Native(end: 5) }, 1); var stepped = Clone(jumped);
        jumped.SyncActiveEvents(Array.Empty<ActiveMarketEvent>(), 10);
        for (var day = 2; day <= 10; day++) stepped.SyncActiveEvents(day < 5 ? new[] { Native(end: 5) } : Array.Empty<ActiveMarketEvent>(), day);
        check(jumped.State.Companies.All(c => c.Value.Price == stepped.State.Companies[c.Key].Price) && Near(Life(jumped).Companies["water"].ContinuationUsed, Life(stepped).Companies["water"].ContinuationUsed) && Near(Life(jumped).Companies["water"].RetainedTargetFactor, Life(stepped).Companies["water"].RetainedTargetFactor), "漏观测多日后先补已知结束前的持续影响再冻结总偏移并逐日恢复");
        var overdue = Engine(); overdue.SyncActiveEvents(new[] { Native(end: 2) }, 1); overdue.SyncActiveEvents(new[] { Native(end: 2) }, 5);
        check(Life(overdue).NativeActive && Life(overdue).ActiveConfirmedThroughDay == 5 && !Life(overdue).RecoveryTargetFrozen, "原生仍活动的权威快照覆盖早已过期的预计结束日");
        overdue.SyncActiveEvents(Array.Empty<ActiveMarketEvent>(), 6);
        check(Life(overdue).LastActiveDay == 5 && !Life(overdue).NativeActive, "超期活动消失后按最后真实确认日结束而不倒推预计旧日期");
        var natural = Engine(); natural.SyncActiveEvents(new[] { Native(end: 3) }, 1); natural.AdvanceDay(3);
        check(!Life(natural).NativeActive && Life(natural).LastActiveDay == 2, "缺少新快照时可按已知有效区间自然到期");
        var permanent = Engine(); permanent.SyncActiveEvents(new[] { Native(end: 2, permanent: true) }, 1); permanent.AdvanceDay(8);
        check(Life(permanent).NativeActive, "永久事件不会被预计时长自动结束");
        var noDuration = Engine(); noDuration.ActivateEvent("rise", "one", 1); noDuration.AdvanceDay(8);
        check(Life(noDuration).NativeActive && Life(noDuration).Companies["water"].ContinuationUsed <= .025m, "缺结束元数据的事件不猜固定天数且持续预算仍有限");

        foreach (var price in new[] { .01m, .03m, .17m })
        {
            var penny = Engine(); penny.State.Companies["water"].Price = price;
            penny.ActivateEvent("rise", "one", 1);
            var p = penny.State.Companies["water"].Price; var factor = Life(penny).Companies["water"].CurrentFactor;
            penny.AdvanceDay(2);
            check(penny.State.Companies["water"].Price == p && Life(penny).Companies["water"].CurrentFactor == factor && Life(penny).Companies["water"].ContinuationUsed == 0m && penny.State.Companies["water"].IntradayPoints.Count == 2, $"低价 {price} 无可施加分币续幅时不记录虚假因子或额外样本");
        }
        var stalled = Engine(); stalled.State.Companies["water"].Price = .17m; stalled.ActivateEvent("rise", "one", 1); stalled.EndEvent("rise", "one", 1); var initialFactor = Life(stalled).Companies["water"].CurrentFactor; stalled.AdvanceDay(20);
        check(stalled.State.EventLifecycles.Count == 1 && Life(stalled).Companies["water"].CurrentFactor == initialFactor && stalled.State.Companies["water"].PersistentEventFactor == 1m && stalled.State.Companies["water"].Price == .19m, "低价回归超过半分但日限不足一分时保留待修复而不伪装永久完成");
        stalled.State.Companies["water"].Price = 100m; stalled.AdvanceDay(120);
        check(stalled.State.EventLifecycles.Count == 0, "后续外部价格变化使分币可行后原待修复事件仍能继续完成");

        var legacy = Engine(); legacy.State.ProcessedEvents.Add("4:rise:one"); legacy.State.OccurredEvents.Add("4:rise:one"); legacy.State.Companies["water"].Price = 117m;
        var old = JsonNode.Parse(legacy.Serialize())!; old.AsObject().Remove("EventLifecycles"); old.AsObject().Remove("EventRetentions"); old.AsObject().Remove("CompletedEventLifecycles");
        foreach (var company in old["Companies"]!.AsObject()) company.Value!.AsObject().Remove("PersistentEventFactor");
        var adopted = new MarketEngine(legacy.Config, MarketEngine.Deserialize(old.ToJsonString(), legacy.Config));
        adopted.SyncActiveEvents(new[] { Native(end: 5) }, 1, initialLoad: true);
        check(adopted.State.Companies["water"].Price == 117m && Life(adopted).LegacyAdopted && Life(adopted).Companies["water"].CurrentFactor == 1m && Life(adopted).Companies["water"].AppliedInitialImpact == 0m, "旧processed仍活动事件只接续新增影响不重冲击也不假造旧偏移");
        var adoptedSave = adopted.Serialize(); adopted.SyncActiveEvents(new[] { Native(end: 5) }, 1, initialLoad: true);
        check(adopted.Serialize() == adoptedSave, "旧事件重复同步不重新抽样或补造历史持续幅度");
        var historical = new MarketEngine(legacy.Config, MarketEngine.Deserialize(old.ToJsonString(), legacy.Config)); historical.SyncActiveEvents(Array.Empty<ActiveMarketEvent>(), 1, true);
        check(historical.State.EventLifecycles.Count == 0 && historical.State.EventRetentions.Count == 0 && historical.State.Companies["water"].Price == 117m, "旧档已经结束的历史事件不补建生命周期或回撤旧价格");
        var fixedConfig = MarketConfig.CreateDefault(); fixedConfig.EventRetentionMin = .35m; fixedConfig.EventRetentionMax = .35m; fixedConfig.Validate();
        var invalid = false; try { fixedConfig.EventRetentionMin = 0; fixedConfig.Validate(); } catch (ArgumentException) { invalid = true; }
        check(invalid, "允许固定保留比作测试但拒绝零保留等退化区间");
    }
}
