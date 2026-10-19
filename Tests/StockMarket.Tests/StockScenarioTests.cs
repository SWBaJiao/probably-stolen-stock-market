using ProbablyStolenStockMarket.Market;
using System.Text.Json;
using System.Text.Json.Nodes;

static class StockScenarioTests
{
    const string MixedTemplate = "stock:water_filter_quality";
    sealed class Wallet : IWallet
    {
        public int Cash { get; private set; } = 1000;
        public void ChangeCash(int delta) => Cash += delta;
    }
    static string Snapshot(object value) => JsonSerializer.Serialize(value, MarketEngine.JsonOptions);
    static MarketEngine Clone(MarketEngine engine)
    {
        var config = MarketConfig.Deserialize(engine.Config.Serialize());
        return new MarketEngine(config, MarketEngine.Deserialize(engine.Serialize(), config));
    }
    static MarketEngine Engine(bool mixedOnly = false, ulong seed = 9281UL)
    {
        var config = MarketConfig.CreateDefault();
        config.DailyLimit = 0m;
        config.StockEventIntervalMinDays = config.StockEventIntervalMaxDays = 3;
        config.StockDurationMinDays = config.StockDurationMaxDays = 4;
        if (mixedOnly)
        {
            config.StockEventTemplates = config.StockEventTemplates.Where(t => t.Id == MixedTemplate).ToList();
            config.StockTemplateCooldownDays = 0;
        }
        var engine = new MarketEngine(config);
        engine.State.SaveIdentity = "stock-scenario-save";
        engine.ApplyStorageAccess(false);
        engine.AdvanceDay(1);
        engine.State.RandomState = seed;
        return engine;
    }

    public static void Run(Action<bool, string> check)
    {
        LegacyDefaults(check);
        QueryAndRandomIsolation(check);
        PersistenceAndNativeIsolation(check);
        BrokerDeliveryAndVerification(check);
    }

    static void LegacyDefaults(Action<bool, string> check)
    {
        var old = JsonNode.Parse(MarketConfig.CreateDefault().Serialize())!.AsObject();
        foreach (var name in old.Select(p => p.Key).Where(k => k.StartsWith("Stock", StringComparison.Ordinal) || k == "EnableStockEvents" || k == "EventShockDailyLimit").ToArray()) old.Remove(name);
        var upgraded = MarketConfig.Deserialize(old.ToJsonString());
        check(upgraded.EnableStockEvents && upgraded.StockEventTemplates.Count == 20 && upgraded.StockEventTemplates.Select(t => t.Id).Distinct().Count() == 20,
            "旧配置缺股市事件字段仍自动获得启用状态和二十个独立模板");
        check(upgraded.Companies.All(c => upgraded.StockEventTemplates.Count(t => t.PrimaryCompanyId == c.Id && t.Polarity > 0) == 1 && upgraded.StockEventTemplates.Count(t => t.PrimaryCompanyId == c.Id && t.Polarity < 0) == 1),
            "默认十家公司各有一个主营利好和一个主营利空事件");
        var mixed = upgraded.StockEventTemplates.Single(t => t.Id == MixedTemplate);
        check(mixed.Impacts["water"] == -.10m && mixed.Impacts["materials"] == .03m && mixed.Impacts["industrial"] == .02m,
            "清泉事故保留清泉利空与替代耗材维修公司利好的分化设定");
    }

    static void QueryAndRandomIsolation(Action<bool, string> check)
    {
        var engine = Engine();
        var initial = engine.Serialize();
        var scheduled = engine.GetStockScheduledEvents();
        check(scheduled.Count > 0 && scheduled.All(e => e.Day > 1 && e.EventId.StartsWith("stock:", StringComparison.Ordinal)),
            "首日生成可供内幕适配层读取的未来股市事件候选");
        for (var i = 0; i < 40; i++)
        {
            engine.GetStockScheduledEvents();
            engine.GetPublicStockNews();
            foreach (var company in engine.Config.Companies) engine.GetPublicStockNews(company.Id);
            engine.GetSharedBrokerPhoneBooking();
            engine.GetBrokerPhoneBooking();
            engine.GetBrokerPhoneBooking(BrokerIds.Jianqing);
        }
        check(engine.GetPublicStockNews().Count == 0 && engine.Serialize() == initial,
            "公开新闻不泄露未公开排程且频繁新闻电话只读查询不消耗随机数或创建状态");
        var expected = Snapshot(engine.GetStockScheduledEvents());
        scheduled[0].Day = 9999;
        scheduled[0].EventId = "被调用方修改";
        check(Snapshot(engine.GetStockScheduledEvents()) == expected,
            "未来候选接口返回副本调用方修改不能篡改实际排程");

        var untouched = Engine();
        var activePlayer = Engine();
        foreach (var company in activePlayer.Config.Companies) activePlayer.State.Positions[company.Id] = new Position { Quantity = 150, Cost = 15000m };
        check(activePlayer.BookRandomBrokerByPhone(activePlayer.GetStockScheduledEvents()).Success,
            "共用电话能直接预约原生队列之外的股市专用事件线索");
        for (var day = 2; day <= 35; day++)
        {
            for (var i = 0; i < 12; i++) { activePlayer.GetPublicStockNews(); activePlayer.GetSharedBrokerPhoneBooking(); activePlayer.GetStockScheduledEvents(); }
            untouched.AdvanceDay(day);
            activePlayer.AdvanceDay(day);
        }
        check(Snapshot(untouched.State.StockEvents) == Snapshot(activePlayer.State.StockEvents),
            "不同持仓与电话报价消耗的随机数不改变后续股市排程强度主冲击日或恢复比例");
    }

    static void PersistenceAndNativeIsolation(Action<bool, string> check)
    {
        var engine = Engine();
        var originalPlan = Snapshot(engine.State.StockEvents);
        var restored = Clone(engine);
        check(Snapshot(restored.State.StockEvents) == originalPlan && restored.Serialize() == engine.Serialize(),
            "保存恢复完整保留未来冲击日期强度预热种子及保留比例快照");
        for (var day = 2; day <= 40; day++) { engine.AdvanceDay(day); restored.AdvanceDay(day); }
        check(Snapshot(restored.State.StockEvents) == Snapshot(engine.State.StockEvents) && Snapshot(restored.State.Companies) == Snapshot(engine.State.Companies),
            "读档后跨越多轮股市事件的行情与新排程仍与连续游玩一致");

        var baseline = Engine(mixedOnly: true);
        var nativeEmpty = Clone(baseline);
        var first = baseline.State.StockEvents.Occurrences.Values.OrderBy(e => e.ShockDay).First();
        for (var day = 2; day <= first.ShockDay + 1; day++)
        {
            baseline.AdvanceDay(day);
            nativeEmpty.SyncActiveEvents(Array.Empty<ActiveMarketEvent>(), day);
        }
        check(Snapshot(nativeEmpty.State.StockEvents) == Snapshot(baseline.State.StockEvents) && Snapshot(nativeEmpty.State.Companies) == Snapshot(baseline.State.Companies),
            "原版权威空活动快照不会提前结束或改变独立股市事件生命周期");
        var news = baseline.GetPublicStockNews().Single(n => n.Id == first.Id);
        check(news.CompanyDirections["water"] == -1 && news.CompanyDirections["materials"] == 1 && news.CompanyDirections["industrial"] == 1 && news.ActualImpacts["water"] < 0 && news.ActualImpacts["materials"] > 0 && news.ActualImpacts["industrial"] > 0,
            "一次公开事故实际造成跨公司涨跌分化并保留作用方向");
        var safeNews = Snapshot(baseline.GetPublicStockNews());
        news.Body = "被调用方修改";
        news.CompanyDirections.Clear();
        news.ActualImpacts.Clear();
        check(Snapshot(baseline.GetPublicStockNews()) == safeNews && baseline.GetPublicStockNews("medical").Count == 0,
            "公开新闻按关联公司筛选且返回可修改副本不会污染真实新闻和价格记录");

        var frozen = Engine(mixedOnly: true);
        var occurrence = frozen.State.StockEvents.Occurrences.Values.OrderBy(e => e.ShockDay).First();
        var occurrenceSnapshot = Snapshot(occurrence);
        frozen.Config.StockEventTemplates[0].Title = "稍后修改的新模板标题";
        frozen.Config.StockEventTemplates[0].Impacts["water"] = -.02m;
        var loaded = Clone(frozen);
        check(Snapshot(loaded.State.StockEvents.Occurrences[occurrence.Id]) == occurrenceSnapshot,
            "已经排定的标题方向和强度不受稍后模板修改或读档影响");
    }

    static void BrokerDeliveryAndVerification(Action<bool, string> check)
    {
        foreach (var brokerId in new[] { BrokerIds.Wenzhou, BrokerIds.Jianqing })
        {
            var outcomes = new HashSet<RumorResolution>();
            var consistent = true;
            for (var seed = 1; seed <= 40; seed++)
            {
                var engine = Engine(mixedOnly: true, seed: unchecked(0x9E3779B97F4A7C15UL * (ulong)seed));
                var scheduled = engine.GetStockScheduledEvents();
                var visit = engine.TryPlanBrokerVisit(scheduled, true, brokerId)!;
                var offer = engine.State.Rumors[visit.OfferId];
                var occurrence = engine.State.StockEvents.Occurrences[offer.OccurrenceKey];
                consistent &= offer.CompanyDirections.All(p => p.Value == Math.Sign(occurrence.Impacts[p.Key]) * (offer.PredictsOccurrence ? 1 : -1));
                var beforePurchase = engine.GetRumors().Single(r => r.Id == offer.Id);
                consistent &= beforePurchase.Content == "购买后查看线索" && beforePurchase.CompanyIds.Count == 0 && beforePurchase.PublicNews.Length == 0 && beforePurchase.VerificationSummary.Length == 0;
                engine.MarkBrokerArrived(visit.VisitId, brokerId);
                var wallet = new Wallet();
                consistent &= engine.BuyBrokerOffer(offer.Id, scheduled, wallet, () => { }, brokerId).Success && wallet.Cash == 950;
                var bought = engine.GetIntelRecords().Single();
                var originalContent = bought.Content;
                var originalDirections = Snapshot(bought.CompanyDirections);
                consistent &= bought.Status == RumorResolution.Pending && bought.PublicNews.Length == 0 && bought.VerificationSummary.Length == 0 && bought.ObservationStartDay == 2 && bought.ObservationEndDay == 4 && bought.PaperState == IntelPaperState.Pending && !bought.Content.Contains(occurrence.Title, StringComparison.Ordinal);
                engine.SetIntelPaperState(offer.Id, IntelPaperState.Delivered, 4100 + seed);
                var restored = Clone(engine);
                consistent &= restored.GetIntelRecords().Single().Content == originalContent && restored.GetIntelRecords().Single().PaperItemId == 4100 + seed;
                engine.AdvanceDay(occurrence.ShockDay);
                engine.ResolveRumors(engine.GetStockScheduledEvents());
                var verified = engine.GetIntelRecords().Single();
                outcomes.Add(verified.Status);
                consistent &= verified.Status == (offer.PredictsOccurrence ? RumorResolution.True : RumorResolution.False) && verified.PublicNews.Contains(occurrence.Title, StringComparison.Ordinal) && verified.VerificationSummary.Length > 0 && verified.Content == originalContent && Snapshot(verified.CompanyDirections) == originalDirections;
                var reputation = engine.State.Reputations[offer.SellerKey];
                engine.ResolveRumors(engine.GetStockScheduledEvents());
                consistent &= reputation.Correct + reputation.Incorrect == 1 && Clone(engine).GetIntelRecords().Single().VerificationSummary == verified.VerificationSummary;
            }
            check(consistent && outcomes.SetEquals(new[] { RumorResolution.True, RumorResolution.False }),
                $"{brokerId}专用事件真假均可达所有预测统一反向纸条日历档案保存原文且仅公开后核验一次");

            var cancelled = Engine(mixedOnly: true);
            var upcoming = cancelled.GetStockScheduledEvents();
            var cancelledVisit = cancelled.TryPlanBrokerVisit(upcoming, true, brokerId)!;
            cancelled.MarkBrokerArrived(cancelledVisit.VisitId, brokerId);
            cancelled.BuyBrokerOffer(cancelledVisit.OfferId, upcoming, new Wallet(), () => { }, brokerId);
            var original = cancelled.GetIntelRecords().Single();
            var reputations = Snapshot(cancelled.State.Reputations);
            var target = cancelled.State.Rumors[cancelledVisit.OfferId].OccurrenceKey;
            check(cancelled.CancelStockEvent(target) && !cancelled.CancelStockEvent(target) && Snapshot(cancelled.State.Reputations) == reputations,
                $"{brokerId}已购专用事件取消幂等且不扣来源信誉");
            var unverified = cancelled.GetIntelRecords().Single();
            check(unverified.Status == RumorResolution.Unverified && unverified.PublicNews.Length == 0 && unverified.VerificationSummary.Contains("未能验证", StringComparison.Ordinal) && unverified.Content == original.Content && cancelled.GetPublicStockNews().All(n => n.Id != target) && Clone(cancelled).GetIntelRecords().Single().Status == RumorResolution.Unverified,
                $"{brokerId}取消后保留原预测纸条及三日日历窗口并标未验证而不泄露取消事件正文");
        }
    }
}
