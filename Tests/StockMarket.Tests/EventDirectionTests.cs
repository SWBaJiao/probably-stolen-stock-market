using MelonModTemplate.Market;

static class EventDirectionTests
{
    const string WaterEvent = "water_treatment_breakdown";
    static CommodityPriceModifier[] NativeWater() => new[]
    {
        new CommodityPriceModifier { ItemType = "WATER", Percent = 100 },
        new CommodityPriceModifier { ItemType = "WATER_RATION", Percent = 100 },
        new CommodityPriceModifier { ItemType = "BREVAGE", Percent = 90 },
        new CommodityPriceModifier { ItemType = "WATER_PURIFICATION_SUPPLY", Percent = 80 }
    };
    static MarketEngine Engine()
    {
        var config = MarketConfig.CreateDefault(); config.DailyLimit = 0m; config.EventPreheatChance = 1m;
        var engine = new MarketEngine(config); engine.AdvanceDay(1); return engine;
    }
    static EventLifecycleState Life(MarketEngine engine) => engine.State.EventLifecycles.Values.Single();
    static MarketEngine OldWater(bool alreadyShocked)
    {
        var old = Engine(); old.State.RandomState = alreadyShocked ? 1UL : 8UL;
        old.Config.Events.Add(new EventRule { EventId = WaterEvent, Description = "供水危机", Sectors = new() { "water" }, Impacts = new() { ["water"] = .15m, ["harvest"] = .15m, ["logistics"] = .135m } });
        old.ActivateEvent(WaterEvent, "one", 1, true, 6);
        var current = MarketConfig.CreateDefault(); current.DailyLimit = 0m; current.EventPreheatChance = 1m;
        return new MarketEngine(current, MarketEngine.Deserialize(old.Serialize(), current));
    }
    static RumorOffer Quote(string id, bool purchased, bool correct = true, int direction = 1) => new()
    {
        Id = id, SessionId = id, EventId = WaterEvent, OccurrenceKey = "future", SellerKey = "old-source", SellerName = "旧来源", IsBrokerOffer = true, IsGroupIdentity = true,
        Reliability = .75m, PriorReliability = .75m, Price = 50, DueDay = 4, WindowEndDay = 4, Purchased = purchased, PurchaseDay = purchased ? 1 : -1, ObservationStartDay = purchased ? 2 : -1,
        PredictsOccurrence = correct, CompanyIds = new() { "water" }, CompanyDirections = new() { ["water"] = direction }, Content = "旧报价原文：清泉走势可能走强"
    };
    static MarketConfig OldDefaultConfig()
    {
        var config = MarketConfig.CreateDefault(); var revision = config.Events.Single(e => e.EventId == "rev_mobilization_secret");
        revision.Impacts = revision.Impacts.ToDictionary(p => p.Key, p => -p.Value); revision.ImpactSource = ""; revision.DirectionVersion = 0;
        return config;
    }
    public static void Run(Action<bool, string> check)
    {
        var engine = Engine(); var rule = EventImpactResolver.EnsureRule(engine, WaterEvent, NativeWater())!;
        check(rule.Impacts["water"] == -.15m && rule.Impacts["harvest"] == -.15m && rule.Impacts["logistics"] == -.135m && rule.ImpactSource == EventImpactResolver.DerivedPriceSource && rule.DirectionVersion == 2, "供水危机按真实饮水净水价格上涨反向利空交叉公司并保存规则来源版本");
        var discount = EventImpactResolver.EnsureRule(engine, "discount", new[] { new CommodityPriceModifier { ItemType = "FOOD", Percent = -20 } })!;
        check(discount.Impacts["harvest"] == .065m && discount.Impacts["commerce"] == .02m && discount.Impacts["logistics"] == .015m, "商品降价对应相关公司利好且保持行业分配权重");
        var mixed = EventImpactResolver.EnsureRule(engine, "mixed", new[] { new CommodityPriceModifier { ItemType = "MEDICAL", Percent = 100 }, new CommodityPriceModifier { ItemType = "ENERGY_CELL", Percent = -100 } })!;
        check(mixed.Impacts["medical"] < 0 && mixed.Impacts["energy"] > 0 && mixed.Impacts["logistics"] == .025m && mixed.Impacts.Values.All(v => Math.Abs(v) <= .15m), "同事件不同商品涨跌可令多家公司分化且共同物流影响先净合并再限幅");
        check(EventImpactResolver.EnsureRule(engine, "cancelled", new[] { new CommodityPriceModifier { ItemType = "WATER", Percent = 30 }, new CommodityPriceModifier { ItemType = "water", Percent = -30 } }) == null, "同商品正负修正抵消后不生成虚假方向规则");
        var custom = new EventRule { EventId = "custom", Impacts = new() { ["water"] = .08m } }; engine.Config.Events.Add(custom);
        check(ReferenceEquals(EventImpactResolver.EnsureRule(engine, "custom", NativeWater()), custom) && custom.Impacts["water"] == .08m && custom.DirectionVersion == 0, "显式自定义事件优先且不会被商品反向规则改写");
        engine.Config.Events.Add(new EventRule { EventId = "cached", ImpactSource = EventImpactResolver.DerivedPriceSource, DirectionVersion = 1, Impacts = new() { ["water"] = .1m } });
        check(EventImpactResolver.EnsureRule(engine, "cached", NativeWater())!.Impacts["water"] < 0 && engine.Config.Events.Count(e => e.EventId == "cached") == 1, "带旧来源版本的运行时缓存会被原生修正重建而不优先保留旧正向规则");

        engine = OldWater(false); var life = Life(engine); var factor = life.Companies["water"].CurrentFactor; var used = life.Companies["water"].ContinuationUsed;
        var shockDay = life.ShockDay; var seed = life.PreheatSeed; var retention = life.RetentionRatio; var random = engine.State.RandomState; var price = engine.State.Companies["water"].Price;
        check(!life.ShockApplied && price > 100m && life.Companies["water"].ReferenceInitialImpact > 0, "旧未首冲档保留已经发生的正向预热直至明确获取新原生映射");
        engine.State.Rumors["purchased"] = Quote("purchased", true);
        engine.State.Rumors["false-purchased"] = Quote("false-purchased", true, false, -1);
        engine.State.Rumors["unsold"] = Quote("unsold", false);
        var verified = Quote("verified", true); verified.Resolution = RumorResolution.True; engine.State.Rumors[verified.Id] = verified;
        var unrelated = Quote("unrelated", true); unrelated.EventId = "custom-event"; engine.State.Rumors[unrelated.Id] = unrelated;
        engine.State.Reputations["old-source"] = new SellerReputation { Correct = 2, Incorrect = 1 };
        EventImpactResolver.EnsureRule(engine, WaterEvent, NativeWater());
        check(life.Companies["water"].ReferenceInitialImpact == -.15m && life.Companies["water"].CurrentFactor == factor && life.Companies["water"].ContinuationUsed == used && engine.State.Companies["water"].Price == price, "明确改向仅修正尚未首冲的未来Reference而不回滚既有价格偏移或预算");
        check(life.ShockDay == shockDay && life.PreheatSeed == seed && life.RetentionRatio == retention && engine.State.RandomState == random && life.DirectionVersion == 2, "方向升级保留大跳日期种子随机保留及全局随机状态");
        check(engine.State.Rumors["purchased"].Resolution == RumorResolution.Unverified && engine.State.Rumors["false-purchased"].Resolution == RumorResolution.Unverified && engine.State.Rumors["unsold"].Expired && engine.State.Rumors["purchased"].Content == "旧报价原文：清泉走势可能走强", "旧真假未验证情报因方向升级改为无法验证且未购失效而不改已购正文");
        check(verified.Resolution == RumorResolution.True && unrelated.Resolution == RumorResolution.Pending && engine.State.Reputations["old-source"].Correct == 2 && engine.State.Reputations["old-source"].Incorrect == 1, "升级不重判已验证记录不影响其它事件也不扣来源信誉");
        var saved = engine.Serialize(); EventImpactResolver.EnsureRule(engine, WaterEvent, NativeWater());
        var cloneConfig = MarketConfig.Deserialize(engine.Config.Serialize()); var clone = new MarketEngine(cloneConfig, MarketEngine.Deserialize(saved, cloneConfig));
        check(engine.Serialize() == saved && clone.Serialize() == saved, "事件方向兼容处理重复Ensure及读档保持幂等");
        engine.AdvanceDay(shockDay - 1); price = engine.State.Companies["water"].Price; engine.AdvanceDay(shockDay);
        check(life.ShockApplied && engine.State.Companies["water"].Price == decimal.Round(price * .85m, 2, MidpointRounding.AwayFromZero), "旧已登记未大跳事件在原计划日按新负方向首冲一次");
        engine.ActivateEvent(WaterEvent, "future", engine.State.LastDay);
        check(engine.State.Reputations["old-source"].Correct == 2 && engine.State.Reputations["old-source"].Incorrect == 1, "后来原事件激活不会再次核验升级失效的旧线索或改变信誉");

        engine = OldWater(true); life = Life(engine); saved = engine.Serialize(); price = engine.State.Companies["water"].Price;
        factor = life.Companies["water"].CurrentFactor; var initial = life.Companies["water"].AppliedInitialImpact; retention = life.RetentionRatio; shockDay = life.ShockDay;
        EventImpactResolver.EnsureRule(engine, WaterEvent, NativeWater());
        check(life.ShockApplied && life.Companies["water"].ReferenceInitialImpact == .15m && life.Companies["water"].AppliedInitialImpact == initial && life.Companies["water"].CurrentFactor == factor && engine.State.Companies["water"].Price == price && life.RetentionRatio == retention && life.ShockDay == shockDay, "已经实际首冲的旧事件锁定完整历史快照而不反向撤销或再跳");
        engine.AdvanceDay(2);
        check(engine.State.Companies["water"].Price > price && life.Companies["water"].ReferenceInitialImpact == .15m, "已首冲旧实例继续自身原有延续直到结束避免强改历史生命周期");

        engine = OldWater(true);
        engine.Config.Events.Add(new EventRule { EventId = WaterEvent, Impacts = new() { ["water"] = .15m } });
        engine.State.RandomState = 8; engine.ActivateEvent(WaterEvent, "two", 1, true, 6); engine.Config.Events.RemoveAll(r => r.EventId == WaterEvent);
        var snapshots = engine.State.EventLifecycles.Values.ToDictionary(l => l.OccurrenceKey, l => (l.Companies["water"].CurrentFactor, l.Companies["water"].ContinuationUsed, l.ShockDay, l.PreheatSeed, l.RetentionRatio));
        EventImpactResolver.EnsureRule(engine, WaterEvent, NativeWater());
        var first = engine.State.EventLifecycles.Values.Single(l => l.OccurrenceKey == "one"); var second = engine.State.EventLifecycles.Values.Single(l => l.OccurrenceKey == "two");
        check(first.ShockApplied && !second.ShockApplied && first.Companies["water"].ReferenceInitialImpact == .15m && second.Companies["water"].ReferenceInitialImpact == -.15m && engine.State.EventLifecycles.Values.All(l => snapshots[l.OccurrenceKey] == (l.Companies["water"].CurrentFactor, l.Companies["water"].ContinuationUsed, l.ShockDay, l.PreheatSeed, l.RetentionRatio)), "同一事件多个实例按是否已首冲独立升级且都保留真实因子预算及随机计划");

        engine = OldWater(false); life = Life(engine); factor = life.Companies["water"].CurrentFactor; used = life.Companies["water"].ContinuationUsed;
        EventImpactResolver.EnsureRule(engine, WaterEvent, new[] { new CommodityPriceModifier { ItemType = "MEDICAL", Percent = 100 } });
        check(life.Companies["water"].ReferenceInitialImpact == 0m && life.Companies["water"].CurrentFactor == factor && life.Companies["water"].ContinuationUsed == used && life.Companies["medical"].CurrentFactor == 1m && life.Companies["medical"].ReferenceInitialImpact < 0, "新映射移除旧公司仅停止未来方向并保留其已发生偏移供恢复而新增公司从无偏移开始");

        engine = Engine(); EventImpactResolver.EnsureRule(engine, WaterEvent, NativeWater());
        var scheduled = new[] { new ScheduledEvent { EventId = WaterEvent, OccurrenceKey = "new", Day = 3 } };
        var visit = engine.TryPlanBrokerVisit(scheduled, true)!;
        var offer = engine.State.Rumors[visit.OfferId];
        check(offer.ImpactSource == EventImpactResolver.DerivedPriceSource && offer.DirectionVersion == 2 && offer.CompanyDirections.All(p => p.Value == (offer.PredictsOccurrence ? -1 : 1)), "新内幕记录当前规则版本且真假方向均基于商品涨价利空规则");

        var oldConfig = OldDefaultConfig(); var upgraded = MarketConfig.Deserialize(oldConfig.Serialize()); var revised = upgraded.Events.Single(e => e.EventId == "rev_mobilization_secret");
        check(revised.Impacts["defense"] == -.12m && revised.Impacts["industrial"] == -.07m && revised.Impacts["technology"] == -.05m && revised.ImpactSource == EventImpactResolver.BuiltinPriceSource && revised.DirectionVersion == 2 && !upgraded.UpgradeLegacyDefaultEventRules(), "完整旧默认动员事件配置在内存幂等升级为商品涨价利空且标记来源版本");
        oldConfig = OldDefaultConfig(); oldConfig.Events.Single(e => e.EventId == "rev_mobilization_secret").Impacts["defense"] = .13m;
        check(MarketConfig.Deserialize(oldConfig.Serialize()).Events.Single(e => e.EventId == "rev_mobilization_secret").Impacts["defense"] == .13m, "自定义过一个幅度的显式旧事件配置不会被默认升级覆盖");
        oldConfig = OldDefaultConfig(); oldConfig.Events.Single(e => e.EventId == "rev_mobilization_secret").Description = "自定义业务解释";
        check(MarketConfig.Deserialize(oldConfig.Serialize()).Events.Single(e => e.EventId == "rev_mobilization_secret").Impacts["defense"] == .12m, "自定义解释但保留旧数值的显式规则也完全保留");
        check(upgraded.Events.Single(e => e.EventId == "badNutrifruitHarvest").Impacts["harvest"] < 0 && upgraded.Events.Single(e => e.EventId == "bm_illegal_weapon_shipment").Impacts["defense"] > 0, "先前读取记录已经符合反向供需的另外两个默认事件保留原方向");
    }
}
