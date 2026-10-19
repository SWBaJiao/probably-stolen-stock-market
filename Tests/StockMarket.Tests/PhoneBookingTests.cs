using ProbablyStolenStockMarket.Market;
using System.Text.Json;
using System.Text.Json.Nodes;

static class PhoneBookingTests
{
    static MarketEngine Engine()
    {
        var config = TestConfig.Create();
        config.Events.Clear();
        config.Events.Add(new EventRule { EventId = "phone-private-event", Description = "电话不应透露的事件标题", Sectors = new() { "weapons" }, Impacts = new() { ["defense"] = .1m, ["commerce"] = -.05m } });
        var engine = new MarketEngine(config);
        engine.ApplyStorageAccess(false);
        engine.AdvanceDay(1);
        return engine;
    }
    static ScheduledEvent Future(int day = 3, string key = "phone-occurrence") => new() { EventId = "phone-private-event", OccurrenceKey = key, Day = day };
    public static void Run(Action<bool, string> check)
    {
        var e = Engine(); var emptySave = e.Serialize();
        check(e.GetBrokerPhoneBooking() == null && e.GetDueBrokerPhoneVisit(new[] { Future() }) == null && e.Serialize() == emptySave, "没有电话预约时未来事件不会自动生成访客");
        check(!e.BookBrokerByPhone(new[] { Future(1), Future(2), Future(6) }).Success && e.Serialize() == emptySave, "电话只接受次日到访后第三日至第五日窗口内的事件");
        check(!e.BookBrokerByPhone(Array.Empty<ScheduledEvent>()).Success && e.Serialize() == emptySave, "没有可售未来线索时预约失败且不创建计划");
        var boundary = Engine();
        check(boundary.BookBrokerByPhone(new[] { Future(5) }).Success, "电话预约包含到访后第三日这一观察窗上边界");
        var pricesBefore = e.State.Companies.ToDictionary(c => c.Key, c => (c.Value.Price, c.Value.History.Count, c.Value.Candles.Count, c.Value.IntradayPoints.Count));
        e.State.Broker.LastArrivalDay = 1;
        check(e.BookBrokerByPhone(new[] { Future() }).Success, "电话预约不受原三日自动到访冷却限制");
        var booked = e.GetBrokerPhoneBooking()!;
        check(booked.RequestDay == 1 && booked.ArrivalDay == 2 && booked.Status == BrokerPhoneBookingStatus.Booked && e.State.Broker.IsPhoneBooking && !e.State.Broker.IsManual && e.State.LastDay == 1 && e.State.Companies.All(c => pricesBefore[c.Key] == (c.Value.Price, c.Value.History.Count, c.Value.Candles.Count, c.Value.IntradayPoints.Count)), "预约只固定明日计划不临时推进日期行情或价格记录");
        var publicBooking = JsonSerializer.Serialize(booked, MarketEngine.JsonOptions);
        check(!publicBooking.Contains("安盾") && !publicBooking.Contains("环宇") && !publicBooking.Contains("phone-private-event") && !publicBooking.Contains("事件标题") && !publicBooking.Contains("CompanyDirections"), "电话预约投影仅公开到访日来源商品类别不泄露预测内容");
        var saved = e.Serialize();
        check(e.BookBrokerByPhone(new[] { Future() }).Success && e.Serialize() == saved && e.GetDueBrokerPhoneVisit(new[] { Future() }) == null, "同日重拨复用已固定报价且预约当日不提前到访");
        var reloaded = new MarketEngine(e.Config, MarketEngine.Deserialize(saved, e.Config));
        check(reloaded.Serialize() == saved && reloaded.GetBrokerPhoneBooking()!.OfferId == booked.OfferId && reloaded.GetBrokerVisit()!.AppearanceKey == e.GetBrokerVisit()!.AppearanceKey, "明日预约报价真假来源外观保存重读后保持不变");
        var quoteBefore = JsonSerializer.Serialize(e.State.Rumors[booked.OfferId], MarketEngine.JsonOptions);
        reloaded.AdvanceDay(2);
        var due = reloaded.GetDueBrokerPhoneVisit(new[] { Future() });
        check(due != null && due.VisitId == booked.VisitId && due.IsPhoneBooking && !due.Expired && JsonSerializer.Serialize(reloaded.State.Rumors[booked.OfferId], MarketEngine.JsonOptions) == quoteBefore && reloaded.MarkBrokerArrived(due.VisitId) && reloaded.State.Broker.LastArrivalDay == 2, "次日仅发送既定电话预约并允许在旧冷却中实际到访");
        var arrivedSave = reloaded.Serialize();
        var arrived = new MarketEngine(reloaded.Config, MarketEngine.Deserialize(arrivedSave, reloaded.Config));
        check(arrived.GetDueBrokerPhoneVisit(new[] { Future() })!.HasArrived && arrived.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Arrived && arrived.Serialize() == arrivedSave, "已到访电话预约读档时只恢复既定访客");
        var wallet = new Wallet();
        var writes = 0;
        var failedPurchase = arrived.BuyBrokerOffer(booked.OfferId, new[] { Future() }, wallet, () => { if (writes++ == 0) throw new IOException("模拟电话访客购买保存失败"); });
        check(!failedPurchase.Success && wallet.Cash == 1000 && arrived.Serialize() == arrivedSave && arrived.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Arrived, "电话购买失败同时恢复原到访状态现金纸条与日历记录");
        check(arrived.BuyBrokerOffer(booked.OfferId, new[] { Future() }, wallet, () => { }).Success && wallet.Cash == 950 && arrived.GetIntelRecords().Single().ObservationStartDay == 3 && arrived.GetIntelRecords().Single().ObservationEndDay == 5 && arrived.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Completed, "电话访客沿用原购买事务并以实际到访日起建立未来三日观察窗");
        var purchasedSave = arrived.Serialize();
        check(arrived.GetDueBrokerPhoneVisit(new[] { Future() }) == null && !arrived.BookBrokerByPhone(new[] { Future() }).Success && arrived.Serialize() == purchasedSave, "已购电话情报同日不再发送访客也不重抽下一条");
        arrived.CloseBrokerVisit(booked.VisitId);
        check(arrived.BookBrokerByPhone(new[] { Future(4, "next-occurrence") }).Success && arrived.GetBrokerPhoneBooking()!.RequestDay == 2 && arrived.GetBrokerPhoneBooking()!.ArrivalDay == 3, "购完并遣散昨日预约访客后当天可以再预约次日到访");
        var nextSaved = arrived.Serialize();
        check(arrived.BookBrokerByPhone(new[] { Future(4, "next-occurrence") }).Success && arrived.Serialize() == nextSaved && arrived.State.Broker.PhoneBookings.Count == 2 && arrived.State.Rumors.ContainsKey(booked.OfferId) && arrived.State.Broker.PhoneBookings[0].Status == BrokerPhoneBookingStatus.Completed, "接待后新预约重拨不重抽且保留已购情报与旧预约归档");

        var cancelled = Engine(); cancelled.BookBrokerByPhone(new[] { Future() });
        var cancelledId = cancelled.GetBrokerPhoneBooking()!.OfferId;
        check(cancelled.CancelBrokerPhoneBooking() && cancelled.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Cancelled && cancelled.State.Rumors[cancelledId].Expired && cancelled.State.Reputations.Values.All(r => r.Correct == 0 && r.Incorrect == 0), "取消预约保留失效报价归档且不惩罚来源信誉");
        var cancelledSaved = cancelled.Serialize();
        check(!cancelled.BookBrokerByPhone(new[] { Future() }).Success && cancelled.Serialize() == cancelledSaved, "同一登记日取消预约后不能借重拨重新抽取报价");
        cancelled.AdvanceDay(2);
        check(cancelled.GetDueBrokerPhoneVisit(new[] { Future() }) == null, "取消的预约次日不会再次出现");
        var unavailable = Engine(); unavailable.BookBrokerByPhone(new[] { Future() }); unavailable.AdvanceDay(2);
        check(unavailable.GetDueBrokerPhoneVisit(Array.Empty<ScheduledEvent>()) == null && unavailable.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Cancelled, "到访前基础事件取消则预约失效且不临时抽取替代情报");
        var expired = Engine(); expired.BookBrokerByPhone(new[] { Future() }); expired.AdvanceDay(3);
        check(expired.GetDueBrokerPhoneVisit(new[] { Future(5) }) == null && expired.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Expired && expired.State.Rumors.Count == 1, "错过预约日后保留过期记录且不会推迟自动发送访客");

        var old = Engine(); var oldVisit = old.TryPlanBrokerVisit(new[] { Future() })!;
        check(old.GetDueBrokerPhoneVisit(new[] { Future() }) == null && !old.State.Broker.IsPhoneBooking, "旧未到访自动计划不会经电话调度自动发出");
        check(old.MarkBrokerArrived(oldVisit.VisitId), "旧已存在计划仍可完成实际到访");
        var activeSave = old.Serialize();
        check(!old.BookBrokerByPhone(new[] { Future() }).Success && old.Serialize() == activeSave && old.BuyBrokerOffer(oldVisit.OfferId, new[] { Future() }, new Wallet(), () => { }).Success, "电话不能替换活跃旧访客且旧已到访仍可完成原购买");
        old.AdvanceDay(2); var activeNextDay = old.Serialize();
        check(!old.BookBrokerByPhone(new[] { Future(4) }).Success && old.Serialize() == activeNextDay, "跨日仍在场的活跃访客不会被电话刷新逻辑隐式替换");
        var oldJson = JsonNode.Parse(activeSave)!;
        oldJson["Broker"]!.AsObject().Remove("IsPhoneBooking"); oldJson["Broker"]!.AsObject().Remove("PhoneBookings");
        var legacy = new MarketEngine(old.Config, MarketEngine.Deserialize(oldJson.ToJsonString(), old.Config));
        check(!legacy.State.Broker.IsPhoneBooking && legacy.GetBrokerPhoneBooking() == null && legacy.GetDueBrokerPhoneVisit(new[] { Future() }) == null, "旧 schema 缺电话字段时保持无预约且不产生新访客");
        var malformed = JsonNode.Parse(saved)!;
        malformed["Broker"]!["IsPhoneBooking"] = false;
        var rejected = false;
        try { MarketEngine.Deserialize(malformed.ToJsonString(), e.Config); } catch (ArgumentException) { rejected = true; }
        check(rejected, "只有明确电话预约才允许未来计划日进入存档");
        foreach (var wasManual in new[] { false, true })
        {
            var previousVersion = Engine();
            var previousVisit = previousVersion.TryPlanBrokerVisit(new[] { Future() }, manual: wasManual)!;
            previousVersion.MarkBrokerArrived(previousVisit.VisitId);
            var source = previousVersion.State.Rumors[previousVisit.OfferId].SellerKey;
            previousVersion.State.Reputations[source] = new SellerReputation { Correct = 3, Incorrect = 2 };
            var upgraded = new MarketEngine(previousVersion.Config, MarketEngine.Deserialize(previousVersion.Serialize(), previousVersion.Config));
            var oldQuote = JsonSerializer.Serialize(upgraded.State.Rumors[previousVisit.OfferId], MarketEngine.JsonOptions);
            var prices = upgraded.State.Companies.ToDictionary(c => c.Key, c => c.Value.Price);
            check(!upgraded.BookBrokerByPhone(new[] { Future() }).Success && !upgraded.State.Broker.Departed, $"旧{(wasManual ? "手动" : "自动")}在场计划未经实际适配确认仍不能被预约替换");
            upgraded.CloseBrokerVisit(previousVisit.VisitId);
            // 未购旧报价仅增加失效标记，已有内容与来源不能被升级清理覆盖。
            var closedQuote = JsonSerializer.Serialize(upgraded.State.Rumors[previousVisit.OfferId], MarketEngine.JsonOptions);
            var oldQuoteNode = JsonNode.Parse(oldQuote)!; oldQuoteNode["Expired"] = true;
            check(upgraded.BookBrokerByPhone(new[] { Future() }).Success && upgraded.GetBrokerPhoneBooking()!.ArrivalDay == 2 && upgraded.GetBrokerPhoneBooking()!.RequestDay == 1 && upgraded.State.Rumors.ContainsKey(previousVisit.OfferId) && closedQuote == oldQuoteNode.ToJsonString(MarketEngine.JsonOptions) && JsonSerializer.Serialize(upgraded.State.Rumors[previousVisit.OfferId], MarketEngine.JsonOptions) == closedQuote && upgraded.State.Reputations[source].Correct == 3 && upgraded.State.Reputations[source].Incorrect == 2 && upgraded.State.Companies.All(c => prices[c.Key] == c.Value.Price), $"旧{(wasManual ? "手动" : "自动")}残留到访明确结束后可预约次日且保留旧情报信誉行情");
        }
    }
    sealed class Wallet : IWallet
    {
        public int Cash { get; private set; } = 1000;
        public void ChangeCash(int delta) => Cash += delta;
    }
}
