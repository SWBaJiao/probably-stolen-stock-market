using ProbablyStolenStockMarket.Market;

static class SharedBrokerPhoneTests
{
    static MarketEngine Engine(ulong seed = 17)
    {
        var config = TestConfig.Create(); config.DailyLimit = 0m; config.Events.Clear(); config.Sellers.Clear();
        config.Events.Add(new EventRule { EventId = "shared-phone", Sectors = new() { "water" }, Impacts = new() { ["water"] = -.1m, ["harvest"] = .05m } });
        var engine = new MarketEngine(config); engine.ApplyStorageAccess(false); engine.AdvanceDay(1); engine.State.RandomState = seed;
        return engine;
    }
    static ScheduledEvent Future(int day = 3, string key = "one") => new() { EventId = "shared-phone", OccurrenceKey = key, Day = day };
    static MarketEngine Clone(MarketEngine engine) => new(engine.Config, MarketEngine.Deserialize(engine.Serialize(), engine.Config));
    public static void Run(Action<bool, string> check)
    {
        var engine = Engine(); var empty = engine.Serialize(); var scheduled = new[] { Future() };
        check(engine.GetSharedBrokerPhoneBooking() == null && engine.Serialize() == empty, "共用电话只读查询不会提前抽人物或创建预约");
        check(!engine.BookRandomBrokerByPhone(Array.Empty<ScheduledEvent>()).Success && !engine.BookRandomBrokerByPhone(new[] { Future(2), Future(6), new ScheduledEvent { EventId = "unmapped", Day = 3 } }).Success && engine.Serialize() == empty, "没有可售线索或超出到访后窗口时共用电话不消耗随机数");
        var seen = new HashSet<string>(); var jCount = 0; var isolated = true;
        for (var i = 1; i <= 256; i++)
        {
            var sample = Engine(unchecked(0x9E3779B97F4A7C15UL * (ulong)i)); sample.BookRandomBrokerByPhone(scheduled); var selected = sample.GetSharedBrokerPhoneBooking()!;
            seen.Add(selected.BrokerId); if (selected.BrokerId == BrokerIds.Jianqing) jCount++;
            isolated &= sample.State.Rumors.Count == 1 && (selected.BrokerId == BrokerIds.Wenzhou ? sample.State.AdditionalBrokers.Count == 0 : sample.State.Broker.PhoneBookings.Count == 0 && sample.State.AdditionalBrokers.Count == 1);
        }
        check(seen.SetEquals(new[] { BrokerIds.Wenzhou, BrokerIds.Jianqing }) && jCount >= 80 && jCount <= 180 && isolated, "同一电话可约到两人且固定种子样本近似对半并只登记被选人物");
        engine.BookRandomBrokerByPhone(scheduled); var chosen = engine.GetSharedBrokerPhoneBooking()!; var saved = engine.Serialize();
        check(engine.BookRandomBrokerByPhone(scheduled).Success && engine.Serialize() == saved && Clone(engine).GetSharedBrokerPhoneBooking()!.OfferId == chosen.OfferId && Clone(engine).Serialize() == saved, "预约确认后反复拨号及读档不重抽人物来源报价外观或随机流");
        check(engine.CancelBrokerPhoneBooking(chosen.BrokerId) && engine.GetSharedBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Cancelled, "共用电话预约可按已保存归属取消并保留归档");
        saved = engine.Serialize();
        check(!engine.BookRandomBrokerByPhone(new[] { Future(4, "another") }).Success && engine.Serialize() == saved && engine.State.Rumors.Count == 1, "新预约当天取消后不能换线索重拨来刷新人物");

        engine = Engine(); engine.BookRandomBrokerByPhone(scheduled); var beforeInvalidation = engine.State.RandomState;
        check(!engine.BookRandomBrokerByPhone(Array.Empty<ScheduledEvent>()).Success && engine.GetSharedBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Cancelled && engine.State.RandomState == beforeInvalidation, "基础事件当日取消会失效原预约且不借失效重新抽人物");
        saved = engine.Serialize();
        check(!engine.BookRandomBrokerByPhone(scheduled).Success && engine.Serialize() == saved, "当日失效后恢复原事件仍不能重新抽签预约");

        engine = Engine(); engine.BookBrokerByPhone(scheduled); var legacy = engine.GetBrokerPhoneBooking()!; saved = engine.Serialize();
        check(engine.BookRandomBrokerByPhone(scheduled).Success && engine.GetSharedBrokerPhoneBooking()!.OfferId == legacy.OfferId && engine.GetSharedBrokerPhoneBooking()!.BrokerId == BrokerIds.Wenzhou && engine.Serialize() == saved, "旧单闻舟电话预约由共享入口原样复用而不随机替换");
        engine.BookBrokerByPhone(scheduled, BrokerIds.Jianqing); engine.CancelBrokerPhoneBooking(BrokerIds.Jianqing); saved = engine.Serialize();
        check(engine.GetSharedBrokerPhoneBooking()!.OfferId == legacy.OfferId && engine.BookRandomBrokerByPhone(scheduled).Success && engine.Serialize() == saved, "任一人物现有有效预约优先于另一人物的取消归档且不重抽");

        engine = Engine(); engine.BookRandomBrokerByPhone(scheduled); chosen = engine.GetSharedBrokerPhoneBooking()!; engine.AdvanceDay(2);
        engine.MarkBrokerArrived(chosen.VisitId, chosen.BrokerId); saved = engine.Serialize();
        check(engine.GetSharedBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Arrived && !engine.BookRandomBrokerByPhone(new[] { Future(), Future(4, "next") }).Success && engine.Serialize() == saved, "任意情报商实际接待未结束时共用电话拒绝新增预约");
        engine.CloseBrokerVisit(chosen.VisitId, chosen.BrokerId);
        check(engine.BookRandomBrokerByPhone(new[] { Future(4, "next") }).Success && engine.GetSharedBrokerPhoneBooking()!.RequestDay == 2 && engine.GetSharedBrokerPhoneBooking()!.ArrivalDay == 3, "昨日预约人物接待并离开后当天可确认下一次明日随机到访");
        var next = engine.GetSharedBrokerPhoneBooking()!; engine.CancelBrokerPhoneBooking(next.BrokerId); saved = engine.Serialize();
        check(!engine.BookRandomBrokerByPhone(new[] { Future(4, "third") }).Success && engine.Serialize() == saved, "下一轮当天登记的预约结束后同样无法反复抽人");

        engine = Engine(); engine.BookRandomBrokerByPhone(scheduled); engine.AdvanceDay(3);
        check(engine.BookRandomBrokerByPhone(new[] { Future(5, "after-missed") }).Success && engine.GetSharedBrokerPhoneBooking()!.RequestDay == 3 && engine.GetSharedBrokerPhoneBooking()!.ArrivalDay == 4, "错过旧预约后只在新的登记日建立新一轮随机预约");
        var currentDayRandom = engine.State.RandomState; engine.BookRandomBrokerByPhone(Array.Empty<ScheduledEvent>()); saved = engine.Serialize();
        check(engine.GetSharedBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Cancelled && !engine.BookRandomBrokerByPhone(new[] { Future(5, "retry") }).Success && engine.State.RandomState == currentDayRandom && engine.Serialize() == saved, "旧预约过期后新建预约若当日又失效不能继续利用过期状态刷人物");

        engine = Engine(); engine.BookRandomBrokerByPhone(scheduled); chosen = engine.GetSharedBrokerPhoneBooking()!;
        engine.AdvanceDay(2); engine.MarkBrokerArrived(chosen.VisitId, chosen.BrokerId); engine.AdvanceDay(3);
        check(engine.BookRandomBrokerByPhone(new[] { Future(5, "after-orphan") }).Success && engine.GetSharedBrokerPhoneBooking()!.RequestDay == 3 && engine.GetBrokerState(chosen.BrokerId)!.PhoneBookings.Any(r => r.OfferId == chosen.OfferId && r.Status == BrokerPhoneBookingStatus.Expired), "过期Arrived档案先清理再判断接待状态避免原生Tick前残留访客阻塞后续预约");
    }
}
