using MelonModTemplate.Market;
using System.Text.Json;
using System.Text.Json.Nodes;

static class MultiBrokerTests
{
    const string J = BrokerIds.Jianqing;
    static MarketEngine Engine(ulong seed = 917UL, decimal reverse = .7m)
    {
        var config = TestConfig.Create(); config.DailyLimit = 0m; config.EventShockWindowDays = 1; config.JianqingReverseProbability = reverse;
        config.Events.Clear(); config.Sellers.Clear();
        config.Events.Add(new EventRule { EventId = "dual-event", Description = "双来源事件", Sectors = new() { "water" }, Impacts = new() { ["water"] = -.1m, ["harvest"] = .05m } });
        config.Sellers.Add(new SellerRule { Identifier = "expert", Identity = "供水专家", BaseReliability = .9m, Sectors = new() { "water" } });
        var engine = new MarketEngine(config); engine.State.SaveIdentity = "multi-save"; engine.ApplyStorageAccess(false); engine.AdvanceDay(1); engine.State.RandomState = seed;
        return engine;
    }
    static ScheduledEvent Future(int day = 3, string occurrence = "one") => new() { EventId = "dual-event", OccurrenceKey = occurrence, Day = day };
    static MarketEngine Clone(MarketEngine engine)
    {
        var config = MarketConfig.Deserialize(engine.Config.Serialize()); return new MarketEngine(config, MarketEngine.Deserialize(engine.Serialize(), config));
    }
    static string Snapshot(object value) => JsonSerializer.Serialize(value, MarketEngine.JsonOptions);
    sealed class Wallet : IWallet { public int Cash { get; private set; } = 1000; public void ChangeCash(int delta) => Cash += delta; }
    public static void Run(Action<bool, string> check)
    {
        var engine = Engine(); var empty = engine.Serialize(); var scheduled = new[] { Future() };
        check(engine.GetBrokerState(J) == null && engine.GetBrokerVisit(J) == null && engine.GetBrokerPhoneBooking(J) == null && engine.GetDueBrokerPhoneVisit(scheduled, J) == null && engine.Serialize() == empty, "查询未预约剑清酱不会建立空状态或生成访客");
        check(!engine.BookBrokerByPhone(Array.Empty<ScheduledEvent>(), J).Success && !engine.BookBrokerByPhone(scheduled, "unknown").Success && engine.Serialize() == empty, "无未来线索或未知NPC预约不改变任何人物状态与随机流");
        engine.BookBrokerByPhone(scheduled); var w = engine.GetBrokerPhoneBooking()!;
        var oldW = Snapshot(engine.State.Broker); var oldQuote = Snapshot(engine.State.Rumors[w.OfferId]);
        check(engine.BookBrokerByPhone(scheduled, J).Success && Snapshot(engine.State.Broker) == oldW && Snapshot(engine.State.Rumors[w.OfferId]) == oldQuote, "预约剑清酱不会覆盖闻舟的来访报价预约或外观");
        var j = engine.GetBrokerPhoneBooking(J)!;
        check(w.BrokerId == BrokerIds.Wenzhou && j.BrokerId == J && w.OfferId != j.OfferId && w.OfferId.Contains("broker:wenzhou:") && j.OfferId.Contains("broker:jianqing:") && engine.State.Broker.Sequence == 1 && engine.GetBrokerState(J)!.Sequence == 1, "两个NPC各自首次序号相同仍拥有全局唯一且带归属的报价ID");
        check(engine.GetBrokerVisit(J)!.AppearanceKey == "jianqing-default" && engine.SetBrokerAppearance(j.VisitId, "jianqing-portrait", J) && engine.SetBrokerAppearance(w.VisitId, "wenzhou-portrait") && engine.GetBrokerVisit(J)!.AppearanceKey == "jianqing-portrait" && engine.GetBrokerVisit()!.AppearanceKey == "wenzhou-portrait", "人物外观分别持久化且剑清酱使用独立固定外观入口");
        var booked = engine.Serialize();
        check(engine.BookBrokerByPhone(scheduled, J).Success && engine.BookBrokerByPhone(scheduled).Success && engine.Serialize() == booked && Clone(engine).Serialize() == booked, "双NPC预约重拨与读档均复用原报价真假来源及外观");
        engine.AdvanceDay(2);
        check(engine.GetDueBrokerPhoneVisit(scheduled)!.BrokerId == BrokerIds.Wenzhou && engine.GetDueBrokerPhoneVisit(scheduled, J)!.BrokerId == J && !engine.MarkBrokerArrived(w.VisitId, J) && !engine.MarkBrokerArrived(j.VisitId), "次日电话调度分别返回归属访客且错误NPC回调不能到访");
        check(engine.MarkBrokerArrived(j.VisitId, J) && engine.MarkBrokerIntroduced(j.VisitId, J) && engine.GetBrokerState(J)!.HasMetBroker && !engine.State.Broker.HasMetBroker && !engine.State.Broker.HasArrived, "剑清酱实际到访与初次介绍只改变她自己的状态");
        engine.MarkBrokerArrived(w.VisitId); engine.MarkBrokerIntroduced(w.VisitId);
        var wallet = new Wallet(); var before = engine.Serialize();
        check(!engine.BuyBrokerOffer(j.OfferId, scheduled, wallet, () => { }).Success && !engine.BuyBrokerOffer(w.OfferId, scheduled, wallet, () => { }, J).Success && wallet.Cash == 1000 && engine.Serialize() == before, "不能借另一NPC的购买入口买错报价或扣款");
        var attempts = 0;
        var failed = engine.BuyBrokerOffer(j.OfferId, scheduled, wallet, () => { if (attempts++ == 0) throw new IOException("双NPC保存失败"); }, J);
        check(!failed.Success && wallet.Cash == 1000 && engine.Serialize() == before && engine.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Arrived && engine.GetBrokerPhoneBooking(J)!.Status == BrokerPhoneBookingStatus.Arrived, "购买失败完整恢复两个人物状态资金报价首次介绍与预约状态");
        OperationResult? nested = null;
        var purchased = engine.BuyBrokerOffer(j.OfferId, scheduled, wallet, () => nested = engine.BuyBrokerOffer(w.OfferId, scheduled, wallet, () => { }), J);
        check(purchased.Success && nested != null && !nested.Success && wallet.Cash == 950 && engine.GetBrokerPhoneBooking(J)!.Status == BrokerPhoneBookingStatus.Completed && engine.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Arrived && !engine.State.Rumors[w.OfferId].Purchased, "全局购买重入锁覆盖两个NPC且成功只完成实际卖家的预约");
        var jIntel = engine.GetIntelRecords().Single();
        check(jIntel.BrokerId == J && jIntel.SourceName == "剑清酱" && !jIntel.IsGroupIdentity && jIntel.Reliability == .3m && jIntel.ObservationStartDay == 3 && jIntel.ObservationEndDay == 5 && jIntel.PaperState == IntelPaperState.Pending, "剑清酱纸条日历投影保留人物归属个人来源固定可信度与实际观察窗");
        check(engine.SetIntelPaperState(j.OfferId, IntelPaperState.Uncertain, 721) && engine.SetIntelPaperState(j.OfferId, IntelPaperState.Delivered, 721) && !engine.SetIntelPaperState(j.OfferId, IntelPaperState.Pending), "剑清酱沿用全局唯一报价纸条交付入口且已交付不能重复发放");
        check(engine.BuyBrokerOffer(w.OfferId, scheduled, wallet, () => { }).Success && wallet.Cash == 900 && engine.GetIntelRecords().Count == 2 && engine.GetIntelRecords().Select(r => r.BrokerId).ToHashSet().SetEquals(new[] { BrokerIds.Wenzhou, J }), "合并档案同时保留两位NPC的独立情报与日历归属");
        check(Clone(engine).Serialize() == engine.Serialize() && engine.GetRumors().Single(r => r.Id == j.OfferId).BrokerId == J, "多NPC已购档案外观预约介绍与纸条状态可完整保存恢复");
        var wSource = engine.State.Rumors[w.OfferId].SellerKey; var jTruth = engine.State.Rumors[j.OfferId].PredictsOccurrence; var wTruth = engine.State.Rumors[w.OfferId].PredictsOccurrence;
        engine.AdvanceDay(3); engine.ActivateEvent("dual-event", "one", 3); engine.ActivateEvent("dual-event", "one", 3);
        check(engine.State.Reputations[BrokerIds.JianqingSellerKey].Correct == (jTruth ? 1 : 0) && engine.State.Reputations[BrokerIds.JianqingSellerKey].Incorrect == (jTruth ? 0 : 1) && engine.State.Reputations[wSource].Correct == (wTruth ? 1 : 0) && engine.State.Reputations[wSource].Incorrect == (wTruth ? 0 : 1) && wSource != BrokerIds.JianqingSellerKey, "同一事件分别验证两份情报且信誉只更新各自来源一次");

        engine = Engine(); engine.BookBrokerByPhone(scheduled); engine.BookBrokerByPhone(scheduled, J); oldW = Snapshot(engine.State.Broker);
        check(engine.CancelBrokerPhoneBooking(J) && engine.GetBrokerPhoneBooking(J)!.Status == BrokerPhoneBookingStatus.Cancelled && engine.GetBrokerPhoneBooking()!.Status == BrokerPhoneBookingStatus.Booked && Snapshot(engine.State.Broker) == oldW && !engine.BookBrokerByPhone(scheduled, J).Success, "取消剑清酱预约不影响闻舟且同日不能取消重抽报价");
        engine.AdvanceDay(2); check(engine.GetDueBrokerPhoneVisit(scheduled) != null && engine.GetDueBrokerPhoneVisit(scheduled, J) == null, "单独取消后另一个NPC仍可按原预约正常次日到访");

        engine = Engine(); var fixedVisit = engine.TryPlanBrokerVisit(scheduled, true, J)!; var fixedOffer = engine.State.Rumors[fixedVisit.OfferId];
        check(fixedOffer.Reliability == .3m && fixedOffer.PriorReliability == .3m && fixedOffer.CompanyDirections.All(p => p.Value == Math.Sign(engine.Config.Events[0].Impacts[p.Key]) * (fixedOffer.PredictsOccurrence ? 1 : -1)) && Clone(engine).Serialize() == engine.Serialize(), "剑清酱按固定三成可信度生成逐公司真假方向且报价读档不变");
        engine.MarkBrokerArrived(fixedVisit.VisitId, J); engine.BuyBrokerOffer(fixedVisit.OfferId, scheduled, new Wallet(), () => { }, J); engine.AdvanceDay(3); engine.ActivateEvent("dual-event", "one", 3);
        var fixedReputation = engine.State.Reputations[BrokerIds.JianqingSellerKey];
        check(fixedReputation.Correct == (fixedOffer.PredictsOccurrence ? 1 : 0) && fixedReputation.Incorrect == (fixedOffer.PredictsOccurrence ? 0 : 1) && engine.State.Reputations.Keys.All(k => k == BrokerIds.JianqingSellerKey), "真假情报实际验证后只记剑清酱个人对应的信誉记录");
        var rich = Engine(); var poor = Engine();
        rich.State.Reputations[BrokerIds.JianqingSellerKey] = new SellerReputation { Correct = 10000 };
        poor.State.Reputations[BrokerIds.JianqingSellerKey] = new SellerReputation { Incorrect = 10000 };
        poor.Config.Sellers.Clear(); poor.Config.Sellers.Add(new SellerRule { Identifier = "another", Identity = "陌生来源", BaseReliability = .2m });
        var richVisit = rich.TryPlanBrokerVisit(scheduled, true, J)!; var poorVisit = poor.TryPlanBrokerVisit(scheduled, true, J)!;
        var richOffer = rich.State.Rumors[richVisit.OfferId]; var poorOffer = poor.State.Rumors[poorVisit.OfferId];
        check(richOffer.Reliability == .3m && poorOffer.Reliability == .3m && richOffer.PredictsOccurrence == poorOffer.PredictsOccurrence && Snapshot(richOffer.CompanyDirections) == Snapshot(poorOffer.CompanyDirections) && richOffer.SellerKey == BrokerIds.JianqingSellerKey && poorOffer.SellerKey == BrokerIds.JianqingSellerKey, "极端正确错误历史及不同外部来源池均不改变剑清酱固定七成反向概率");
        poor.Config.JianqingReverseProbability = 0m; before = poor.Serialize();
        check(poor.TryPlanBrokerVisit(scheduled, true, J)!.OfferId == poorVisit.OfferId && poor.Serialize() == before && Clone(poor).State.Rumors[poorVisit.OfferId].Reliability == .3m, "修改配置重拨及读档均不刷新本次访问已保存的真假报价");
        var violations = 0; var multipleCompanies = 0; var reversed = 0;
        for (var i = 1; i <= 512; i++)
        {
            var sample = Engine(seed: unchecked(0x9E3779B97F4A7C15UL * (ulong)i)); var visit = sample.TryPlanBrokerVisit(scheduled, true, J)!; var quote = sample.State.Rumors[visit.OfferId];
            if (quote.CompanyDirections.Count > 1) multipleCompanies++;
            if (!quote.PredictsOccurrence) reversed++;
            if (quote.Reliability != .3m || quote.PriorReliability != .3m || quote.CompanyDirections.Any(p => p.Value != Math.Sign(sample.Config.Events[0].Impacts[p.Key]) * (quote.PredictsOccurrence ? 1 : -1))) violations++;
        }
        check(violations == 0 && multipleCompanies > 0 && multipleCompanies < 512 && reversed >= 300 && reversed <= 410, "固定独立种子下真假均可达且约七成反向并保持逐公司方向一致");
        foreach (var reverse in new[] { 0m, 1m })
        {
            var boundary = Engine(reverse: reverse); var boundaryVisit = boundary.TryPlanBrokerVisit(scheduled, true, J)!; var boundaryQuote = boundary.State.Rumors[boundaryVisit.OfferId];
            check(boundaryQuote.PredictsOccurrence == (reverse == 0m) && boundaryQuote.Reliability == 1m - reverse && boundaryQuote.CompanyDirections.All(p => p.Value == Math.Sign(boundary.Config.Events[0].Impacts[p.Key]) * (reverse == 0m ? 1 : -1)) && Clone(boundary).Serialize() == boundary.Serialize(), $"反向概率{reverse}端点严格生成对应方向并可保存恢复");
        }
        var invalid = 0;
        foreach (var reverse in new[] { -.01m, 1.01m }) try { var config = TestConfig.Create(); config.JianqingReverseProbability = reverse; config.Validate(); } catch (ArgumentException) { invalid++; }
        check(invalid == 2, "反向概率配置拒绝小于零或大于一的值");
        var removedSetting = JsonNode.Parse(TestConfig.Create().Serialize())!; removedSetting["JianqingTruthProbability"] = .95m;
        check(!MarketConfig.Deserialize(removedSetting.ToJsonString()).Serialize().Contains("JianqingTruthProbability"), "未发布的旧概率配置额外字段忽略且不再导出");
        var cancelledIntel = Engine(); var cancelledVisit = cancelledIntel.TryPlanBrokerVisit(scheduled, true, J)!;
        cancelledIntel.MarkBrokerArrived(cancelledVisit.VisitId, J); cancelledIntel.BuyBrokerOffer(cancelledVisit.OfferId, scheduled, new Wallet(), () => { }, J);
        var reputationBeforeCancel = Snapshot(cancelledIntel.State.Reputations); cancelledIntel.AdvanceDay(4); cancelledIntel.ResolveRumors(Array.Empty<ScheduledEvent>());
        check(cancelledIntel.State.Rumors[cancelledVisit.OfferId].Resolution == RumorResolution.Unverified && Snapshot(cancelledIntel.State.Reputations) == reputationBeforeCancel, "剑清酱线索所涉事件取消仍记无法验证且不扣个人信誉");
        var unknownIntel = Engine(); var unknownVisit = unknownIntel.TryPlanBrokerVisit(scheduled, true, J)!;
        unknownIntel.MarkBrokerArrived(unknownVisit.VisitId, J); unknownIntel.BuyBrokerOffer(unknownVisit.OfferId, scheduled, new Wallet(), () => { }, J);
        var reputationBeforeUnknown = Snapshot(unknownIntel.State.Reputations); unknownIntel.Config.Events[0].Impacts.Clear(); unknownIntel.AdvanceDay(3); unknownIntel.ActivateEvent("dual-event", "one", 3);
        check(unknownIntel.State.Rumors[unknownVisit.OfferId].Resolution == RumorResolution.Unverified && Snapshot(unknownIntel.State.Reputations) == reputationBeforeUnknown, "事件事实缺少可验证公司方向时反向线索也不被强判失实");

        engine = Engine(); engine.BookBrokerByPhone(scheduled); w = engine.GetBrokerPhoneBooking()!;
        var oldId = "broker:legacy-save:1"; var legacy = JsonNode.Parse(engine.Serialize().Replace(w.OfferId, oldId))!;
        legacy.AsObject().Remove("AdditionalBrokers"); legacy["Broker"]!.AsObject().Remove("BrokerId");
        foreach (var booking in legacy["Broker"]!["PhoneBookings"]!.AsArray()) booking!.AsObject().Remove("BrokerId");
        foreach (var quote in legacy["Rumors"]!.AsObject()) quote.Value!.AsObject().Remove("BrokerId");
        var upgraded = new MarketEngine(engine.Config, MarketEngine.Deserialize(legacy.ToJsonString(), engine.Config));
        check(upgraded.State.Broker.BrokerId == BrokerIds.Wenzhou && upgraded.State.AdditionalBrokers.Count == 0 && upgraded.GetBrokerPhoneBooking()!.OfferId == oldId && upgraded.State.Rumors[oldId].BrokerId == BrokerIds.Wenzhou && upgraded.GetBrokerState(J) == null, "旧schema缺多NPC字段时仅恢复原闻舟且保留旧全局报价ID");
        upgraded.AdvanceDay(2); upgraded.MarkBrokerArrived(oldId);
        check(upgraded.BuyBrokerOffer(oldId, scheduled, new Wallet(), () => { }).Success && upgraded.BookBrokerByPhone(new[] { Future(4, "second") }, J).Success && upgraded.GetBrokerPhoneBooking(J)!.ArrivalDay == 3, "旧闻舟报价仍可用默认接口购买并可另外预约剑清酱");
        var corrupt = JsonNode.Parse(upgraded.Serialize())!; corrupt["AdditionalBrokers"]![J]!["BrokerId"] = BrokerIds.Wenzhou;
        var rejected = false; try { MarketEngine.Deserialize(corrupt.ToJsonString(), upgraded.Config); } catch (ArgumentException) { rejected = true; }
        check(rejected, "存档中额外NPC键和状态人物归属不一致时拒绝加载");
        corrupt = JsonNode.Parse(upgraded.Serialize())!; var jOfferId = upgraded.GetBrokerPhoneBooking(J)!.OfferId; corrupt["Rumors"]![jOfferId]!["BrokerId"] = BrokerIds.Wenzhou;
        rejected = false; try { MarketEngine.Deserialize(corrupt.ToJsonString(), upgraded.Config); } catch (ArgumentException) { rejected = true; }
        check(rejected, "存档报价被错误归入另一NPC时拒绝恢复跨人物购买链");
    }
}
