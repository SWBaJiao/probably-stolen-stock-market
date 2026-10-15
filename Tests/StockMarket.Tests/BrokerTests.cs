using MelonModTemplate.Market;
using System.Text.Json;
using System.Text.Json.Nodes;

static class BrokerTests
{
    static MarketEngine Engine(ulong? seed = null)
    {
        var config = MarketConfig.CreateDefault();
        config.Sellers.Clear();
        config.Events.Clear();
        config.Events.Add(new EventRule { EventId = "broker-private-event", Description = "不可泄露的真正事件标题", Sectors = new() { "weapons" }, Impacts = new() { ["defense"] = .1m, ["commerce"] = -.1m, ["technology"] = 0m } });
        var engine = new MarketEngine(config);
        engine.State.SaveIdentity = "test-save";
        engine.State.StorageVerified = true;
        engine.AdvanceDay(1);
        if (seed.HasValue) engine.State.RandomState = seed.Value;
        return engine;
    }
    static ScheduledEvent Future(int day = 2) => new() { EventId = "broker-private-event", OccurrenceKey = "occurrence-one", Day = day };
    static (MarketEngine engine, BrokerVisitView visit) Planned(ulong? seed = null)
    {
        var engine = Engine(seed);
        return (engine, engine.TryPlanBrokerVisit(new[] { Future() })!);
    }
    static (MarketEngine engine, BrokerVisitView visit) Find(string kind, bool correct)
    {
        for (ulong seed = 1; seed <= 10000; seed++)
        {
            var p = Planned(seed);
            if (p.engine.State.Rumors[p.visit.OfferId].ProductKind == kind && p.engine.State.Rumors[p.visit.OfferId].PredictsOccurrence == correct) return p;
        }
        throw new Exception("未找到所需情报分支");
    }
    public static void Run(Action<bool, string> check)
    {
        var empty = Engine();
        var noPlan = empty.Serialize();
        check(empty.TryPlanBrokerVisit(Array.Empty<ScheduledEvent>()) == null && empty.Serialize() == noPlan, "情报商无未来线索不计划且不消耗随机状态");
        check(empty.TryPlanBrokerVisit(new[] { Future(1), Future(5), new ScheduledEvent { EventId = "unmapped", OccurrenceKey = "u", Day = 2 } }) == null, "当天事件、窗口外事件和未映射事件不能触发到访");
        empty.Config.Events[0].Impacts = new() { ["defense"] = 0m };
        check(empty.TryPlanBrokerVisit(new[] { Future() }) == null, "没有非零方向的事件不出售伪预测");

        var p = Planned();
        check(p.visit != null && p.engine.State.Broker.LastArrivalDay == -1 && !p.visit.HasArrived, "计划到访不提前启动实际到访冷却");
        var savedPlan = p.engine.Serialize();
        var same = p.engine.TryPlanBrokerVisit(new[] { Future() });
        check(same!.OfferId == p.visit!.OfferId && p.engine.Serialize() == savedPlan, "同日重复计划保持报价来源真假与外观完全稳定");
        var copy = new MarketEngine(p.engine.Config, MarketEngine.Deserialize(savedPlan, p.engine.Config));
        check(copy.GetBrokerVisit()!.AppearanceKey == p.visit.AppearanceKey && copy.Serialize() == savedPlan, "读档保留专属情报商计划与报价");
        var publicOffer = JsonSerializer.Serialize(p.visit, MarketEngine.JsonOptions);
        check(!publicOffer.Contains("安盾") && !publicOffer.Contains("环宇") && !publicOffer.Contains("方向") && !publicOffer.Contains("broker-private-event") && !publicOffer.Contains("真正事件") && p.engine.GetIntelRecords().Count == 0, "付款前仅公开来源产品类别且无可读情报记录");
        check(!p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, new Wallet(), () => { }).Success && !p.engine.BuyRumor(p.visit.OfferId, new Wallet(), () => { }).Success, "未实际到访不可购买且旧购买接口不能绕过专属校验");
        check(p.engine.SetBrokerAppearance(p.visit.VisitId, "known-portrait") && p.engine.GetBrokerVisit()!.AppearanceKey == "known-portrait", "实际外观选择可稳定写入到访状态");
        check(p.engine.MarkBrokerArrived(p.visit.VisitId) && p.engine.MarkBrokerArrived(p.visit.VisitId) && p.engine.State.Broker.LastArrivalDay == 1, "实际到访幂等并从当前游戏日开始冷却");
        p.engine.CloseBrokerVisit(p.visit.VisitId);
        p.engine.AdvanceDay(2);
        check(p.engine.TryPlanBrokerVisit(new[] { Future(4) }) == null, "实际到访后第一天仍处于冷却");
        p.engine.AdvanceDay(3);
        check(p.engine.TryPlanBrokerVisit(new[] { Future(4) }) == null, "实际到访后第二天仍处于冷却");
        p.engine.AdvanceDay(4);
        check(p.engine.TryPlanBrokerVisit(new[] { Future(5) }) != null, "实际到访满三天且有未来线索时才再次计划");
        var missed = Planned(); missed.engine.AdvanceDay(2);
        var nextPlan = missed.engine.TryPlanBrokerVisit(new[] { Future(3) });
        check(nextPlan != null && nextPlan.VisitId != missed.visit.VisitId && missed.engine.State.Broker.LastArrivalDay == -1, "未能实际到访的计划过日可重约且不占冷却");

        p = Planned(); p.engine.MarkBrokerArrived(p.visit.VisitId);
        var beforeFailure = p.engine.Serialize();
        check(!p.engine.BuyBrokerOffer(p.visit.OfferId, Array.Empty<ScheduledEvent>(), new Wallet(), () => { }).Success && p.engine.Serialize() == beforeFailure, "原事件已取消的报价拒绝扣款且不改变状态");
        check(!p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future(5) }, new Wallet(), () => { }).Success, "排期变更超出窗口时旧报价不可购买");
        p.engine.State.StorageVerified = false;
        check(!p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, new Wallet(), () => { }).Success, "专属情报购买受存档验证门控");
        p.engine.State.StorageVerified = true;
        check(!p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, new Wallet { Cash = 49 }, () => { }).Success, "专属报价余额不足时拒绝下单");
        var wallet = new Wallet(); var saves = 0;
        var failed = p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, wallet, () => { if (saves++ == 0) throw new IOException("模拟情报保存失败"); });
        check(!failed.Success && wallet.Cash == 1000 && p.engine.Serialize() == beforeFailure && p.engine.GetIntelRecords().Count == 0, "专属购买持久化失败完整回滚资金纸条日历与到访状态");
        OperationResult? nested = null;
        var purchase = p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, wallet, () => nested = p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, wallet, () => { }));
        var intel = p.engine.GetIntelRecords().Single();
        check(purchase.Success && nested != null && !nested.Success && wallet.Cash == 950 && intel.CalendarEligible && intel.PurchaseDay == 1 && intel.ObservationStartDay == 2 && intel.ObservationEndDay == 4 && intel.PaperState == IntelPaperState.Pending && intel.PaperItemId == -1, "成功购买只扣一次并建立固定三日观察窗及待交付纸条");
        check(!p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, wallet, () => { }).Success && wallet.Cash == 950, "重复购买专属报价不重复扣款");
        check(p.engine.SetIntelPaperState(intel.OfferId, IntelPaperState.Uncertain) && p.engine.SetIntelPaperState(intel.OfferId, IntelPaperState.Delivered, 123) && !p.engine.SetIntelPaperState(intel.OfferId, IntelPaperState.Pending) && p.engine.GetIntelRecords()[0].PaperItemId == 123, "纸条交付可由不确定核实到已交付且不能降级重发");
        check(!p.engine.SetIntelPaperState(intel.OfferId, IntelPaperState.Delivered, 456) && p.engine.GetIntelRecords()[0].PaperItemId == 123, "已交付纸条标识不可被另一个道具覆盖");
        check(!p.engine.ResetUndeliveredPaper(intel.OfferId, 123), "明确已交付的纸条不可通过失败恢复入口重发");
        check(intel.Reliability == .5m && intel.IsGroupIdentity && intel.Correct == 0 && intel.Incorrect == 0 && p.visit.ProductKind.Contains("武器、防护装备"), "纸条提供来源可信度与群组记录且未购商品展示安全行业类别");
        intel.CompanyDirections.Clear();
        check(p.engine.GetIntelRecords()[0].CompanyDirections.Count > 0, "已购投影方向集合不会反向修改持久化情报");
        var visible = JsonSerializer.Serialize(p.engine.GetIntelRecords()[0], MarketEngine.JsonOptions);
        check(!visible.Contains("broker-private-event") && !visible.Contains("真正事件") && !visible.Contains("PredictsOccurrence") && !visible.Contains("OccurrenceKey"), "已购纸条日历投影也不包含内部事件或真假依据");

        foreach (var kind in new[] { "行业情报", "公司情报" }) foreach (var correct in new[] { true, false })
        {
            p = Find(kind, correct);
            var offer = p.engine.State.Rumors[p.visit.OfferId];
            var rule = p.engine.Config.Events[0];
            check(offer.ProductKind == kind && offer.CompanyDirections.Count == (kind == "行业情报" ? 2 : 1) && offer.CompanyDirections.All(d => d.Value == Math.Sign(rule.Impacts[d.Key]) * (correct ? 1 : -1)) && offer.Content.StartsWith("有人透露，未来3日，") && offer.Content.Contains(kind == "行业情报" ? "与武器、防护装备类商品相关的部分公司：" : "消息指向") && offer.Content.EndsWith("以上为来源传闻，不保证准确。") && !offer.Content.Contains("不会") && !offer.Content.Contains("真正事件") && !offer.Content.Contains("2"), $"{kind}{(correct ? "真" : "假")}按范围和逐公司方向陈述且不泄露事件日期");
            p.engine.MarkBrokerArrived(p.visit.VisitId); p.engine.BuyBrokerOffer(offer.Id, new[] { Future() }, new Wallet(), () => { });
            p.engine.AdvanceDay(2); p.engine.ActivateEvent(Future().EventId, Future().OccurrenceKey, 2); p.engine.ActivateEvent(Future().EventId, Future().OccurrenceKey, 2);
            var rep = p.engine.State.Reputations[offer.SellerKey];
            check(rep.Correct == (correct ? 1 : 0) && rep.Incorrect == (correct ? 0 : 1) && !p.engine.State.Reputations.ContainsKey(p.visit.VisitId), $"{kind}{(correct ? "真" : "假")}验证仅影响真实来源信誉一次");
        }

        foreach (var initiallyCorrect in new[] { true, false })
        {
            p = Find("行业情报", initiallyCorrect);
            var offer = p.engine.State.Rumors[p.visit.OfferId];
            p.engine.MarkBrokerArrived(p.visit.VisitId); p.engine.BuyBrokerOffer(offer.Id, new[] { Future() }, new Wallet(), () => { });
            p.engine.Config.Events[0].Impacts = p.engine.Config.Events[0].Impacts.ToDictionary(d => d.Key, d => -d.Value);
            p.engine.AdvanceDay(2); p.engine.ActivateEvent(Future().EventId, Future().OccurrenceKey, 2);
            check(p.engine.GetIntelRecords().Single().Status == (initiallyCorrect ? RumorResolution.False : RumorResolution.True), $"原本{(initiallyCorrect ? "真" : "假")}的预测按事件实际变向结果核验而非内部标签");
        }
        p = Find("行业情报", true);
        p.engine.MarkBrokerArrived(p.visit.VisitId); p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, new Wallet(), () => { });
        p.engine.Config.Events.Clear();
        p.engine.AdvanceDay(2); p.engine.ActivateEvent(Future().EventId, Future().OccurrenceKey, 2);
        var unverifiedRecord = p.engine.GetIntelRecords().Single();
        check(unverifiedRecord.Status == RumorResolution.Unverified && unverifiedRecord.Correct == 0 && unverifiedRecord.Incorrect == 0, "事件发生但实际影响未知时不惩罚来源信誉");
        var observedSave = p.engine.Serialize();
        check(new MarketEngine(p.engine.Config, MarketEngine.Deserialize(observedSave, p.engine.Config)).Serialize() == observedSave, "实际观察方向和未验证结论随档保存");

        var unrelatedSourceSeen = false; var relatedSourceSeen = false; var wrongCompanyIndustry = false;
        for (ulong seed = 1; seed <= 64; seed++)
        {
            var sourceEngine = Engine(seed);
            sourceEngine.Config.Events[0].Sectors = new() { "water", "medical" };
            sourceEngine.Config.Events[0].Impacts = new() { ["water"] = .1m, ["medical"] = -.1m };
            sourceEngine.Config.Sellers.Add(new SellerRule { Identifier = "miner", Faction = "miners", Identity = "矿工来源", BaseReliability = .75m, Sectors = new() { "minerals" } });
            sourceEngine.Config.Sellers.Add(new SellerRule { Identifier = "water-worker", Faction = "water-faction", Identity = "水务来源", BaseReliability = .75m, Sectors = new() { "water" } });
            sourceEngine.Config.Sellers.Add(new SellerRule { Identifier = "medical-worker", Faction = "medical-faction", Identity = "医疗来源", BaseReliability = .75m, Sectors = new() { "medical" } });
            var visit = sourceEngine.TryPlanBrokerVisit(new[] { Future() })!;
            var quote = sourceEngine.State.Rumors[visit.OfferId];
            unrelatedSourceSeen |= quote.SellerName == "矿工来源" || quote.SellerName == "水务来源" && quote.Industry != "饮用水、水处理用品" || quote.SellerName == "医疗来源" && quote.Industry != "药品、医疗耗材";
            relatedSourceSeen |= quote.SellerKey == "type:water-worker|water-faction" || quote.SellerKey == "type:medical-worker|medical-faction";
            if (quote.ProductKind == "公司情报") wrongCompanyIndustry |= quote.CompanyDirections.ContainsKey("water") && quote.Industry != "饮用水、水处理用品" || quote.CompanyDirections.ContainsKey("medical") && quote.Industry != "药品、医疗耗材";
        }
        check(!unrelatedSourceSeen && relatedSourceSeen, "来源仅选择披露行业从业者或泛来源并保留角色阵营信誉键");
        check(!wrongCompanyIndustry, "单公司商品先确定目标再选择对应行业不发生医疗水务错配");
        var legacyFactionUsed = false; var factionlessKeptSeparate = false;
        for (ulong seed = 1; seed <= 64; seed++)
        {
            var reputationEngine = Engine(seed);
            reputationEngine.Config.Sellers.Add(new SellerRule { Identifier = "legacy-source", Faction = "old-faction", Identity = "旧阵营来源", BaseReliability = .75m, Sectors = new() { "weapons" } });
            reputationEngine.Config.Sellers.Add(new SellerRule { Identifier = "legacy-source", Identity = "未注明阵营来源", BaseReliability = .75m, Sectors = new() { "weapons" } });
            reputationEngine.State.Reputations["type:legacy-source|old-faction"] = new SellerReputation { Correct = 10 };
            reputationEngine.State.Reputations["type:legacy-source|"] = new SellerReputation { Incorrect = 10 };
            var reputationVisit = reputationEngine.TryPlanBrokerVisit(new[] { Future() })!;
            var reputationOffer = reputationEngine.State.Rumors[reputationVisit.OfferId];
            if (reputationOffer.SellerKey == "type:legacy-source|old-faction") legacyFactionUsed |= reputationOffer.Reliability == .9m;
            if (reputationOffer.SellerKey == "type:legacy-source|") factionlessKeptSeparate |= reputationOffer.Reliability == .425m;
        }
        check(legacyFactionUsed && factionlessKeptSeparate, "新来源接续旧 pipe 阵营信誉键且空阵营信誉保持独立");
        p = Planned(); p.engine.MarkBrokerArrived(p.visit.VisitId);
        p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, new Wallet(), () => { });
        p.engine.SetIntelPaperState(p.visit.OfferId, IntelPaperState.Uncertain, 789);
        check(!p.engine.SetIntelPaperState(p.visit.OfferId, IntelPaperState.Pending) && !p.engine.ResetUndeliveredPaper(p.visit.OfferId, 788) && p.engine.ResetUndeliveredPaper(p.visit.OfferId, 789) && p.engine.GetIntelRecords()[0].PaperState == IntelPaperState.Pending && p.engine.GetIntelRecords()[0].PaperItemId == -1, "只有已核实销毁的相同临时纸条标识可恢复待交付");

        p = Planned(); p.engine.MarkBrokerArrived(p.visit.VisitId);
        p.engine.ActivateEvent(Future().EventId, Future().OccurrenceKey, 1);
        check(!p.engine.BuyBrokerOffer(p.visit.OfferId, new[] { Future() }, new Wallet(), () => { }).Success, "事件已发生即使排程快照尚存也不可再购买");
        p = Planned(); p.engine.TryPlanBrokerVisit(Array.Empty<ScheduledEvent>());
        check(p.engine.GetBrokerVisit()!.Expired && p.engine.TryPlanBrokerVisit(new[] { Future() })!.OfferId == p.visit.OfferId, "同日失效报价不重新抽取商品或真假");

        var legacy = Engine();
        var old = legacy.CreateRumorOffer("old-visit", new SellerProfile { Key = "old-source" }, new[] { Future() })!;
        legacy.BuyRumor(old.Id, new Wallet(), () => { });
        var oldJson = JsonNode.Parse(legacy.Serialize())!; oldJson.AsObject().Remove("Broker");
        var restored = new MarketEngine(legacy.Config, MarketEngine.Deserialize(oldJson.ToJsonString(), legacy.Config));
        check(restored.State.Version == 1 && restored.GetRumors().Single().Purchased && restored.GetIntelRecords().Count == 0 && !restored.State.Rumors[old.Id].CalendarEligible, "旧 schema 情报继续归档且不会补发纸条或日历提醒");
    }
    sealed class Wallet : IWallet
    {
        public int Cash { get; set; } = 1000;
        public void ChangeCash(int delta) => Cash = checked(Cash + delta);
    }
}
