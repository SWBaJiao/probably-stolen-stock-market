using System.Text.Json.Nodes;
using MelonModTemplate.Market;

static class StockEventTests
{
    public static void Run(Action<bool, string> check)
    {
        var config = MarketConfig.CreateDefault(); config.DailyLimit = 0m;
        check(config.StockEventTemplates.Count == 20 && config.StockEventTemplates.GroupBy(t => t.PrimaryCompanyId).All(g => g.Count() == 2 && g.Sum(t => t.Polarity) == 0), "专用模板每家公司一利好一利空共二十条");
        var engine = new MarketEngine(config); engine.AdvanceDay(1);
        var saved = engine.Serialize();
        var restored = new MarketEngine(config, MarketEngine.Deserialize(saved, config));
        check(saved == restored.Serialize(), "专用排程读档不重抽实例、强度、种子与随机状态");
        engine.AdvanceDay(70);
        var all = engine.State.StockEvents.Occurrences.Values.OrderBy(e => e.ShockDay).ToArray();
        check(all.All(e => e.Intensity >= .8m && e.Intensity <= 1.2m && e.EndDayExclusive - e.StartDay >= 2 && e.EndDayExclusive - e.StartDay <= 4 && e.ShockDay >= e.StartDay && e.ShockDay < e.EndDayExclusive), "专用事件强度持续期与主冲击日期均在有效快照范围");
        check(all.GroupBy(e => e.TemplateId).All(g => g.Zip(g.Skip(1)).All(p => p.Second.ShockDay - p.First.ShockDay >= 14)), "滚动排程同模板至少冷却十四日");
        check(all.Zip(all.Skip(1)).All(p => p.Second.ShockDay - p.First.ShockDay >= 2 && p.Second.ShockDay - p.First.ShockDay <= 4), "充足模板下专用事件间隔二至四日");
        config.EnableStockEvents = false; engine.AdvanceDay(100); config.EnableStockEvents = true; engine.AdvanceDay(101);
        check(engine.GetStockScheduledEvents().All(e => e.Day > 101) && MarketEngine.Deserialize(engine.Serialize(), config).LastDay == 101, "停用跨多日再启用只生成未来有效计划");

        var legacy = JsonNode.Parse(MarketConfig.CreateDefault().Serialize())!.AsObject(); legacy.Remove("StockEventTemplates");
        // 保留原有合法公司结构，只移除全部行业与行业依赖，验证新增模板不会阻塞旧配置。
        legacy["Sectors"] = new JsonArray(); legacy["Events"] = new JsonArray(); legacy["Sellers"] = new JsonArray();
        var migrated = MarketConfig.Deserialize(legacy.ToJsonString());
        check(!migrated.EnableStockEvents && migrated.StockEventTemplates.Count == 0, "旧自定义行业无完整可用模板时关闭新排程且保留旧配置");

        var cap = TestConfig.Create(); cap.DailyLimit = 0m; cap.EventShockWindowDays = 1; cap.EventPreheatChance = 0m;
        cap.Events.Add(new EventRule { EventId = "cap-a", Impacts = new() { ["water"] = .1m } });
        cap.Events.Add(new EventRule { EventId = "cap-b", Impacts = new() { ["water"] = .1m } });
        var capped = new MarketEngine(cap); capped.AdvanceDay(1); capped.ActivateEvent("cap-a", "a", 1); capped.ActivateEvent("cap-b", "b", 1);
        check(capped.State.Companies["water"].Price == 115m && capped.State.Companies["water"].EventShockRatio == 1.15m, "同日迟到的原生主冲击共享百分之十五净上限");
        check(Math.Abs(capped.State.EventLifecycles.Values.Aggregate(1m, (f, l) => f * l.Companies["water"].CurrentFactor) - 1.15m) < .00000001m, "冲击实际因子乘积等于真实施加变化");
        var old = JsonNode.Parse(capped.Serialize())!;
        old["Companies"]!["water"]!["Price"] = 132m; old["Companies"]!["water"]!["EventShockRatio"] = 1.32m; old["Companies"]!["water"]!["EventShockNetSignal"] = .3m;
        var oldEngine = new MarketEngine(cap, MarketEngine.Deserialize(old.ToJsonString(), cap)); oldEngine.ActivateEvent("cap-a", "late-positive", 1);
        check(oldEngine.State.Companies["water"].Price == 132m, "旧档已超新上限的同日利好不会反跌或追溯撤回旧价");
        var opposite = TestConfig.Create(); opposite.DailyLimit = 0m; opposite.EventShockWindowDays = 5; opposite.EventPreheatChance = 0m;
        opposite.Events.Add(new EventRule { EventId = "net-positive", Impacts = new() { ["water"] = .1m } });
        opposite.Events.Add(new EventRule { EventId = "net-negative", Impacts = new() { ["water"] = -.1m } });
        var neutral = new MarketEngine(opposite); neutral.AdvanceDay(1);
        neutral.ActivateEvent("net-positive", "positive", 1); neutral.ActivateEvent("net-negative", "negative", 1);
        // 将两个合法未执行计划放到相同未来日期，验证统一日推进的批量抵消。
        foreach (var life in neutral.State.EventLifecycles.Values) { life.ShockApplied = false; life.ShockDay = 2; life.Companies["water"].CurrentFactor = 1m; life.Companies["water"].AppliedInitialImpact = 0m; }
        neutral.State.Companies["water"].Price = 100m; neutral.AdvanceDay(2);
        check(neutral.State.Companies["water"].Price == 100m && neutral.State.EventLifecycles.Values.All(l => l.ShockApplied && l.Companies["water"].CurrentFactor == 1m), "同日相反主冲击先抵消且不记录未真实施加的恢复因子");
        foreach (var price in new[] { .01m, .03m, .17m })
        {
            var penny = new MarketEngine(cap); penny.AdvanceDay(1); penny.State.Companies["water"].Price = price;
            penny.ActivateEvent("cap-a", "penny-a", 1); penny.ActivateEvent("cap-b", "penny-b", 1);
            check(penny.State.Companies["water"].Price / price <= 1.15m, $"低价 {price} 分币量化不突破主冲击日上限");
        }
    }
}
