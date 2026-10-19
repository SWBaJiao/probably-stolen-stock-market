using ProbablyStolenStockMarket.Market;
using System.Text.Json.Nodes;

static class BrokerIntroductionTests
{
    static MarketEngine Engine()
    {
        var config = TestConfig.Create();
        config.Events.Clear();
        config.Events.Add(new EventRule { EventId = "intro-event", Sectors = new() { "water" }, Impacts = new() { ["water"] = .1m } });
        var engine = new MarketEngine(config);
        engine.ApplyStorageAccess(false);
        engine.AdvanceDay(1);
        return engine;
    }
    static ScheduledEvent Future(int day = 3, string key = "intro-one") => new() { EventId = "intro-event", OccurrenceKey = key, Day = day };
    public static void Run(Action<bool, string> check)
    {
        var engine = Engine();
        check(!engine.State.Broker.HasMetBroker && !engine.MarkBrokerIntroduced("missing-visit"), "没有实际访客时不标记首次见过闻舟");
        engine.BookBrokerByPhone(new[] { Future() });
        var booking = engine.GetBrokerPhoneBooking()!;
        var booked = engine.Serialize();
        check(!engine.State.Broker.HasMetBroker && !engine.MarkBrokerIntroduced(booking.VisitId) && engine.Serialize() == booked, "电话预约及提前到达的介绍回调不会消耗首次见面");
        var restored = new MarketEngine(engine.Config, MarketEngine.Deserialize(booked, engine.Config));
        check(!restored.State.Broker.HasMetBroker && restored.GetBrokerPhoneBooking()!.OfferId == booking.OfferId, "仅拨过电话的存档恢复后仍保留首次见面状态");
        restored.AdvanceDay(2);
        var due = restored.GetDueBrokerPhoneVisit(new[] { Future() })!;
        restored.MarkBrokerArrived(due.VisitId);
        var beforeIntroduction = restored.Serialize();
        var expected = JsonNode.Parse(beforeIntroduction)!;
        expected["Broker"]!["HasMetBroker"] = true;
        check(restored.MarkBrokerIntroduced(due.VisitId) && restored.State.Broker.HasMetBroker && restored.Serialize() == expected.ToJsonString(MarketEngine.JsonOptions), "实际当前访客完成介绍只改变见面标记不改变预约信誉或行情");
        var introduced = restored.Serialize();
        check(restored.MarkBrokerIntroduced(due.VisitId) && restored.Serialize() == introduced, "当前访客重复介绍回调幂等且不改变随机状态");
        var familiar = new MarketEngine(restored.Config, MarketEngine.Deserialize(introduced, restored.Config));
        check(familiar.State.Broker.HasMetBroker && familiar.Serialize() == introduced, "首次见面完成标记准确保存并恢复");
        familiar.CloseBrokerVisit(due.VisitId);
        var departed = familiar.Serialize();
        check(!familiar.MarkBrokerIntroduced(due.VisitId) && familiar.Serialize() == departed, "已遣散访客的延迟介绍回调不能再修改状态");
        familiar.BookBrokerByPhone(new[] { Future(4, "intro-two") });
        check(familiar.State.Broker.HasMetBroker && familiar.GetBrokerPhoneBooking()!.OfferId != booking.OfferId, "后续新预约不会清除已经认识闻舟的持久标记");
        var oldJson = JsonNode.Parse(introduced)!;
        oldJson["Broker"]!.AsObject().Remove("HasMetBroker");
        var legacy = new MarketEngine(restored.Config, MarketEngine.Deserialize(oldJson.ToJsonString(), restored.Config));
        check(!legacy.State.Broker.HasMetBroker && legacy.State.Broker.HasArrived, "旧 schema 缺首次见面字段时默认未认识且不从到访记录猜测");

        var stale = Engine();
        stale.BookBrokerByPhone(new[] { Future() });
        var oldId = stale.GetBrokerPhoneBooking()!.VisitId;
        stale.AdvanceDay(2); stale.MarkBrokerArrived(oldId); stale.CloseBrokerVisit(oldId);
        stale.BookBrokerByPhone(new[] { Future(4, "new-client") });
        var newId = stale.GetBrokerPhoneBooking()!.VisitId;
        stale.AdvanceDay(3); stale.MarkBrokerArrived(newId);
        var newVisitSnapshot = stale.Serialize();
        check(!stale.State.Broker.HasMetBroker && !stale.MarkBrokerIntroduced(oldId) && stale.Serialize() == newVisitSnapshot && stale.MarkBrokerIntroduced(newId), "旧访客回调不能替新访客完成介绍且当前匹配访客仍可首次认识");
    }
}
