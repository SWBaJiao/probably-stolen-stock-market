using MelonModTemplate.Market;
using System.Text.Json;
using System.Text.Json.Nodes;

static class DailyTradeTests
{
    static MarketEngine Engine()
    {
        var config = TestConfig.Create();
        config.DailyLimit = 0m; config.EventShockWindowDays = 1;
        var engine = new MarketEngine(config);
        engine.ApplyStorageAccess(false); engine.AdvanceDay(1);
        config.DailyLimit = .03m;
        return engine;
    }
    static MarketEngine Clone(MarketEngine engine) => new(engine.Config, MarketEngine.Deserialize(engine.Serialize(), engine.Config));
    static bool EqualPrices(MarketEngine a, MarketEngine b) => a.State.Companies.All(c => c.Value.Price == b.State.Companies[c.Key].Price);
    public static void Run(Action<bool, string> check)
    {
        var unknownDay = new MarketEngine(TestConfig.Create());
        string? diagnosis = null; unknownDay.Diagnostic = message => diagnosis = message;
        check(!unknownDay.RecordTrade("no-day", "x", new[] { "water" }, 1000m) && unknownDay.State.Sessions.Count == 0 && diagnosis?.Contains("日期") == true, "尚未接入游戏日的成交拒绝猜测日期并输出诊断");
        var e = Engine(); var control = Clone(e);
        e.RecordTrade("open", "one", new[] { "water" }, 1000m);
        check(e.State.TradeSignalsByDay[1]["water"] == 650m && e.State.TradeSignalsByDay[1]["harvest"] == 200m && e.State.TradeSignalsByDay[1]["logistics"] == 150m, "成功成交当下按交叉权重归入当前游戏日净信号");
        check(!e.RecordTrade("open", "one", new[] { "water" }, 1000m) && e.State.TradeSignalsByDay[1]["water"] == 650m, "重复成交回调不会重复计入日汇总");
        e.RecordTrade("undismissed", "two", new[] { "medical" }, 1000m);
        var beforeClosePoints = e.State.Companies["water"].IntradayPoints.Count;
        e.CloseCustomer("open");
        check(EqualPrices(e, control) && e.State.Companies["water"].IntradayPoints.Count == beforeClosePoints, "成交与遣散均不即时改变价格或追加分时点");
        e.AdvanceDay(2); control.AdvanceDay(2);
        check(new[] { "water", "harvest", "medical", "commerce", "logistics" }.All(id => e.State.Companies[id].Price > control.State.Companies[id].Price) && !e.State.Sessions["undismissed"].Closed, "次日按前日净成交统一利好关联公司且不漏未遣散顾客");
        var settled = e.Serialize();
        check(!e.AdvanceDay(2) && e.Serialize() == settled && e.State.TradeSignalsByDay.Count == 0, "同一新日只消费前日成交一次并清除已消费日桶");
        var savedAgain = Clone(e); var beforeLateClose = savedAgain.Serialize();
        check(savedAgain.Serialize() == settled && !savedAgain.AdvanceDay(2), "日结后读档不会重新迁移仍未遣散会话的历史成交");
        savedAgain.CloseCustomer("undismissed");
        var lateCloseControl = Clone(e);
        savedAgain.AdvanceDay(3); lateCloseControl.AdvanceDay(3);
        check(EqualPrices(savedAgain, lateCloseControl), "迟到的顾客遣散不会将已结算成交再次归入新日");
        var reason = e.State.Companies["water"].IntradayPoints.Last().Reason;
        check(reason.Contains("每日") && reason.Contains("前一日") && reason.Contains("第 1 日") && reason.Contains("%"), "日行情分时原因展示前日日期与成交影响百分比");

        e = Engine(); control = Clone(e);
        e.RecordTrade("same", "sale", new[] { "water" }, 1000m);
        e.RecordTrade("same", "buy", new[] { "water" }, -1000m);
        e.AdvanceDay(2); control.AdvanceDay(2);
        check(EqualPrices(e, control), "当日同类买卖净额抵消时次日只有基础行情");
        e = Engine(); control = Clone(e);
        e.RecordTrade("buyer", "buy", new[] { "medical", "CHEMICAL_SUPPLIES", "medical" }, -1000m);
        check(e.State.TradeSignalsByDay[1]["medical"] == -450m && e.State.TradeSignalsByDay[1]["materials"] == -250m, "买入保留负方向且多标签按行业平均而非重复放大");
        e.AdvanceDay(2); control.AdvanceDay(2);
        check(e.State.Companies["medical"].Price < control.State.Companies["medical"].Price && e.State.Companies["materials"].Price < control.State.Companies["materials"].Price, "玩家买入在次日对交叉公司形成轻微利空");

        e = Engine(); e.RecordTrade("spanning", "day-one", new[] { "water" }, 1000m); e.AdvanceDay(2);
        control = Clone(e);
        e.RecordTrade("spanning", "day-two", new[] { "medical" }, 1000m);
        check(e.State.TradeSignalsByDay.Count == 1 && e.State.TradeSignalsByDay[2].ContainsKey("medical") && !e.State.TradeSignalsByDay[2].ContainsKey("water"), "跨日未结束会话每笔成交按各自游戏日入账");
        e.CloseCustomer("spanning"); e.AdvanceDay(3); control.AdvanceDay(3);
        check(e.State.Companies["medical"].Price > control.State.Companies["medical"].Price && e.State.Companies["water"].Price == control.State.Companies["water"].Price, "跨日会话关闭不会重放前一天已经消费的成交");
        e = Engine(); e.RecordTrade("jump", "x", new[] { "water" }, 1000m); control = Clone(e);
        e.AdvanceDay(5); control.AdvanceDay(2); control.AdvanceDay(3); control.AdvanceDay(4); control.AdvanceDay(5);
        check(EqualPrices(e, control) && e.State.RandomState == control.State.RandomState && e.State.TradeSignalsByDay.Count == 0 && e.State.Companies["water"].History.Where(p => p.Day >= 3).All(p => p.Reason.Contains("成交结算影响 0.00%")), "一次跨多日推进仅在紧邻下一日消费原日信号");

        e = Engine(); e.RecordTrade("persist", "x", new[] { "water" }, 1000m); control = Clone(e);
        check(control.State.TradeSignalsByDay[1]["water"] == 650m, "未结算日信号随存档准确恢复");
        e.AdvanceDay(2); control.AdvanceDay(2);
        check(e.Serialize() == control.Serialize(), "读档后次日结算结果及随机状态完全一致");
        e = Engine(); e.RecordTrade("large", "x", new[] { "water" }, int.MaxValue);
        var before = e.State.Companies.ToDictionary(c => c.Key, c => c.Value.Price); e.AdvanceDay(2);
        check(e.State.Companies.All(c => Math.Abs(c.Value.Price / before[c.Key] - 1m) <= e.Config.DailyLimit && c.Value.DailyRetailImpact <= e.Config.TradeDailyLimit), "大量成交与基础波动合并后总日涨跌仍受百分之三限制");
        foreach (var price in new[] { .01m, .03m, .17m })
        {
            e = Engine(); e.State.Companies["water"].Price = price; e.RecordTrade("penny", "x", new[] { "water" }, int.MaxValue); e.AdvanceDay(2);
            check(Math.Abs(e.State.Companies["water"].Price / price - 1m) <= e.Config.DailyLimit, $"低价 {price} 的分币舍入不突破非事件日变动限制");
        }
        e = Engine(); e.Config.Events.Add(new EventRule { EventId = "shock", Impacts = new() { ["water"] = .2m } });
        e.ActivateEvent("shock", "once", 1);
        check(e.State.Companies["water"].Price == 115m && e.State.Companies["water"].IntradayPoints.Last().Reason != "每日行情", "事件仍在激活时独立大幅跳价且不受日常百分之三限制");

        var oldConfigJson = JsonNode.Parse(TestConfig.Create().Serialize())!; oldConfigJson.AsObject().Remove("TradeDailyLimit");
        check(MarketConfig.Deserialize(oldConfigJson.ToJsonString()).TradeDailyLimit == .02m, "旧本地配置缺少新字段时自动采用百分之二成交影响上限");
        var legacyConfig = TestConfig.Create();
        var legacy = new MarketState { LastDay = 1, Companies = legacyConfig.Companies.ToDictionary(c => c.Id, _ => new CompanyState()), Sessions = new() { ["pending"] = new CustomerSession { Signals = new() { ["medical"] = 700m }, TradeIds = new() { "legacy-trade" } }, ["closed"] = new CustomerSession { Closed = true, Signals = new() { ["water"] = 650m } } } };
        legacy.Companies["water"].Price = 101m;
        var legacyJson = JsonNode.Parse(JsonSerializer.Serialize(legacy, MarketEngine.JsonOptions))!;
        legacyJson.AsObject().Remove("TradeSettlementVersion"); legacyJson.AsObject().Remove("TradeSignalsByDay");
        var migrated = new MarketEngine(legacyConfig, MarketEngine.Deserialize(legacyJson.ToJsonString(), legacyConfig));
        check(migrated.State.TradeSettlementVersion == 1 && migrated.State.TradeSignalsByDay[1]["medical"] == 700m && !migrated.State.TradeSignalsByDay[1].ContainsKey("water") && migrated.State.Companies["water"].Price == 101m, "旧档仅迁移未结束会话信号且不回放已涨价的关闭历史");
        var migrationSave = migrated.Serialize(); var repeated = Clone(migrated);
        check(repeated.Serialize() == migrationSave && repeated.State.TradeSignalsByDay[1]["medical"] == 700m, "反序列化加构造及再次读取的迁移标记阻止重复复制");
        control = Clone(migrated); control.State.TradeSignalsByDay.Clear();
        migrated.AdvanceDay(2); control.AdvanceDay(2);
        check(migrated.State.Companies["medical"].Price > control.State.Companies["medical"].Price && migrated.State.Companies["water"].Price == control.State.Companies["water"].Price, "旧档待结算信号仅次日消费一次且不重复闭合成交");
        var oldUnknown = JsonNode.Parse(legacyJson.ToJsonString())!; oldUnknown["LastDay"] = -1;
        var undated = new MarketEngine(legacyConfig, MarketEngine.Deserialize(oldUnknown.ToJsonString(), legacyConfig));
        check(undated.State.TradeSettlementVersion == 1 && undated.State.TradeSignalsByDay.Count == 0, "旧档缺日期的未结束会话不猜交易日也不重播历史信号");
        var stale = Engine(); stale.State.TradeSignalsByDay[0] = new() { ["water"] = 650m }; control = Clone(stale); control.State.TradeSignalsByDay.Clear();
        stale.AdvanceDay(2); control.AdvanceDay(2);
        check(EqualPrices(stale, control) && stale.State.TradeSignalsByDay.Count == 0, "过期非相邻日信号只清理而不延后计入今日行情");
    }
}
