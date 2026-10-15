using MelonModTemplate.Market;

if (args.Length == 2 && args[0] == "--export-config")
{
    File.WriteAllText(args[1], MarketConfig.CreateDefault().Serialize());
    return;
}

var count = 0;
void Check(bool ok, string description)
{
    if (!ok) throw new Exception("失败：" + description);
    Console.WriteLine("通过：" + description);
    count++;
}
MarketEngine Engine()
{
    var c = MarketConfig.CreateDefault();
    c.Events.Add(new EventRule { EventId = "test-water", Description = "净水设施故障", Sectors = new() { "water" }, Impacts = new() { ["water"] = -.1m, ["industrial"] = .05m } });
    return new MarketEngine(c) { State = { StorageVerified = true } };
}

var engine = Engine();
Check(engine.Config.Companies.Count == 10, "默认十家公司");
var w = engine.GetWeights(new[] { "medical", "chemical", "medical" });
Check(w.Values.Sum() == 1m && w["medical"] == .45m && w["materials"] == .25m, "多分类平均且标签去重，权重总和为一");
Check(engine.GetWeights(new[] { "unknown" }).Count == 0, "未知商品不猜测行业");
Check(engine.GetWeights(new[] { "MATERIAL", "BASIC_MATERIAL" })["industrial"] == .55m && engine.GetWeights(new[] { "CHEMICAL_SUPPLIES" })["materials"] == .5m, "真实工业材料与化学品类型分别映射且同分类不重复");
Check(engine.GetWeights(new[] { "ENERGY_CELL" })["energy"] == .65m && engine.GetWeights(new[] { "LUXURY_ITEM" })["commerce"] == .75m, "真实下划线类型正确映射");
engine.RecordTrade("one", "t1", new[] { "medical" }, 1000m);
Check(!engine.RecordTrade("one", "t1", new[] { "medical" }, 1000m), "重复成交回调不计入");
Check(engine.State.Companies["medical"].Price == 100m, "遣散前不调整行情");
engine.CloseCustomer("one");
var price = engine.State.Companies["medical"].Price;
Check(price > 100m && engine.State.Companies["commerce"].Price > 100m && engine.State.Companies["logistics"].Price > 100m, "卖出同时利好多家公司");
Check(!engine.CloseCustomer("one") && engine.State.Companies["medical"].Price == price, "遣散重复回调不结算");
engine.RecordTrade("two", "t2", new[] { "medical" }, -1000m);
engine.CloseCustomer("two");
Check(engine.State.Companies["medical"].Price < price, "玩家买入造成利空");
engine.RecordTrade("three", "t3", new[] { "water" }, 1000m);
engine.RecordTrade("three", "t4", new[] { "water" }, -1000m);
engine.CloseCustomer("three");
Check(engine.State.Companies["water"].Price == 100m, "同类反向成交完全抵消");
for (int i = 0; i < 20; i++) { engine.RecordTrade("cap" + i, "x", new[] { "food" }, 100000m); engine.CloseCustomer("cap" + i); }
Check(engine.State.Companies["harvest"].DailyRetailImpact == .08m, "零售日累计绝对影响封顶");
engine.AdvanceDay(1);
var snapshot = engine.Serialize();
var clone = new MarketEngine(engine.Config, MarketEngine.Deserialize(snapshot, engine.Config));
Check(!engine.AdvanceDay(1), "同一天不重复结算");
engine.AdvanceDay(2); clone.AdvanceDay(2);
Check(engine.Serialize() == clone.Serialize(), "行情随机状态读档后保持一致");
var beforeWater = engine.State.Companies["water"].Price;
var beforeIndustrial = engine.State.Companies["industrial"].Price;
engine.ActivateEvent("test-water", "2/1", 2);
Check(engine.State.Companies["water"].Price < beforeWater && engine.State.Companies["industrial"].Price > beforeIndustrial, "事件可导致关联公司分化");
Check(!engine.ActivateEvent("test-water", "2/1", 2), "同次事件重复回调去重");

engine = Engine();
var wallet = new Wallet { Cash = 1000 };
Check(engine.QuoteOrder("medical", OrderSide.Buy, 1, wallet.Cash).CashDelta == -101, "买入最低手续费与向上取整");
Check(!engine.QuoteOrder("medical", OrderSide.Buy, 100, wallet.Cash).Success, "拒绝余额不足");
Check(!engine.QuoteOrder("medical", OrderSide.Sell, 1, wallet.Cash).Success, "拒绝超持仓卖出");
Check(engine.PlaceOrder("medical", OrderSide.Buy, 3, wallet, () => { }).Success && wallet.Cash == 698 && engine.State.Positions["medical"].Quantity == 3, "买入资金和含费用成本正确");
Check(engine.PlaceOrder("medical", OrderSide.Sell, 1, wallet, () => { }).Success && wallet.Cash == 797 && engine.State.Positions["medical"].Quantity == 2 && engine.State.Positions["medical"].RealizedProfit < 0, "卖出扣费与按比例释放成本");
var cashBefore = wallet.Cash;
var positionBefore = engine.State.Positions["medical"].Quantity;
var saves = 0;
var failure = engine.PlaceOrder("medical", OrderSide.Buy, 1, wallet, () => { if (saves++ == 0) throw new IOException("模拟持久化失败"); });
Check(!failure.Success && wallet.Cash == cashBefore && engine.State.Positions["medical"].Quantity == positionBefore && !engine.State.TradingFrozen, "保存失败同时回滚资金和持仓并重写旧状态");
failure = engine.PlaceOrder("medical", OrderSide.Buy, 1, wallet, () => throw new IOException("持续写入失败"));
Check(!failure.Success && engine.State.TradingFrozen, "补偿保存失败冻结交易");
engine = Engine();
wallet = new Wallet { Cash = 1000 };
wallet.OnChange = delta => { wallet.Cash += delta; throw new Exception("扣款后接口异常"); };
failure = engine.PlaceOrder("medical", OrderSide.Buy, 1, wallet, () => { });
Check(!failure.Success && wallet.Cash == 1000, "扣款后抛错仍能按实际余额补偿");
engine = Engine();
wallet = new Wallet { Cash = 1000 };
OperationResult? nested = null;
engine.PlaceOrder("medical", OrderSide.Buy, 1, wallet, () => nested = engine.PlaceOrder("medical", OrderSide.Buy, 1, wallet, () => { }));
Check(nested != null && !nested.Success && engine.State.Positions["medical"].Quantity == 1, "交易回调重入被拒绝");
engine.State.StorageVerified = false;
Check(!engine.PlaceOrder("medical", OrderSide.Buy, 1, wallet, () => { }).Success, "存档未验证时拒绝真实资金交易");

engine = Engine(); engine.AdvanceDay(1);
wallet = new Wallet { Cash = 1000 };
var future = new List<ScheduledEvent> { new() { EventId = "test-water", OccurrenceKey = "3/1", Day = 3 } };
var seller = new SellerProfile { Key = "seller-one", Name = "顾客甲", Identity = "水处理从业者", BaseReliability = .75m, Sectors = new() { "water" } };
var offer = engine.CreateRumorOffer("visit", seller, future)!;
Check(offer.Content == "购买后查看线索" && offer.DueDay == 0 && offer.CompanyIds.Count == 0, "未购情报投影隐藏内容及事件信息");
var persistedOffer = engine.Serialize();
var loaded = new MarketEngine(engine.Config, MarketEngine.Deserialize(persistedOffer, engine.Config));
Check(loaded.CreateRumorOffer("visit", seller, future)!.Id == offer.Id && loaded.Serialize() == persistedOffer, "读档与重复交谈不重抽报价");
Check(engine.BuyRumor(offer.Id, wallet, () => { }).Success && wallet.Cash == 950, "付费购买情报");
Check(!engine.BuyRumor(offer.Id, wallet, () => { }).Success && wallet.Cash == 950, "重复购买不重复扣款");
Check(engine.GetRumors()[0].Content != "购买后查看线索" && engine.GetRumors()[0].CompanyIds.Count == 2, "购买后可见一条情报对应多家公司");
var expected = engine.State.Rumors[offer.Id].PredictsOccurrence;
engine.ActivateEvent("test-water", "3/1", 3);
engine.ActivateEvent("test-water", "3/1", 3);
Check(engine.GetRumors()[0].Resolution == (expected ? RumorResolution.True : RumorResolution.False) && engine.State.Reputations[seller.Key].Correct + engine.State.Reputations[seller.Key].Incorrect == 1, "按事件事实验证并仅更新一次信誉");
engine = Engine(); engine.AdvanceDay(1);
offer = engine.CreateRumorOffer("cancelled", seller, future)!;
engine.BuyRumor(offer.Id, new Wallet { Cash = 1000 }, () => { });
engine.ResolveRumors(Array.Empty<ScheduledEvent>());
Check(engine.GetRumors()[0].Resolution == RumorResolution.Pending, "计划暂时出队不抢先判定取消");
engine.AdvanceDay(4);
engine.ResolveRumors(Array.Empty<ScheduledEvent>());
Check(engine.GetRumors()[0].Resolution == RumorResolution.Unverified && engine.State.Reputations[seller.Key].Correct + engine.State.Reputations[seller.Key].Incorrect == 0, "取消事件记为未验证且不惩罚信誉");
future[0].Day = 6;
Check(engine.CreateRumorOffer("empty", seller, Array.Empty<ScheduledEvent>()) == null && engine.CreateRumorOffer("empty", seller, future) == null, "无关联未来事件无报价且同次来访不刷新");
engine.CreateRumorOffer("leave", seller, future); engine.CloseCustomer("leave");
Check(engine.GetRumors().All(x => x.Id != "leave"), "遣散后未购报价失效");
var invalid = false;
try { MarketEngine.Deserialize("{\"Version\":99}", engine.Config); } catch (ArgumentException) { invalid = true; }
Check(invalid, "未知存档版本拒绝加载");
invalid = false;
try { var c = MarketConfig.CreateDefault(); c.Sectors[0].Weights["harvest"] = .9m; c.Validate(); } catch (ArgumentException) { invalid = true; }
Check(invalid, "配置权重异常拒绝启用");
engine = Engine();
engine.ActivateEvent("test-water", "secret", 1, false);
Check(engine.State.Ledger.All(x => x.Kind != "event") && engine.State.Companies["water"].History.All(x => x.Reason == "市场异动"), "隐藏事件影响价格但不公开原因");
engine.State.Companies["water"].Price = .01m;
engine.State.Positions["water"].Quantity = 1;
Check(!engine.QuoteOrder("water", OrderSide.Sell, 1, 1000).Success, "拒绝手续费超过卖出收入的订单");
invalid = false;
try { var c = MarketConfig.CreateDefault(); c.Events.Add(null!); c.Validate(); } catch (ArgumentException) { invalid = true; }
Check(invalid, "配置含空事件时清晰拒绝启用");
engine = Engine();
var other = Engine();
engine.RecordTrade("visit-private", "trade", new[] { "food" }, 1000m); engine.CloseCustomer("visit-private");
Check(other.State.Companies["harvest"].Price == 100m && other.State.Sessions.Count == 0, "不同存档引擎之间无共享状态");

foreach (var truth in new[] { true, false })
{
    MarketEngine? branch = null;
    for (ulong seed = 1; seed <= 10000; seed++)
    {
        var candidate = Engine();
        candidate.State.LastDay = 1;
        candidate.State.RandomState = seed;
        candidate.CreateRumorOffer("branch", seller, new[] { new ScheduledEvent { EventId = "test-water", OccurrenceKey = "3/branch", Day = 3 } });
        if (candidate.State.Rumors["branch"].PredictsOccurrence == truth) { branch = candidate; break; }
    }
    Check(branch != null, $"可以生成{(truth ? "真" : "假")}内幕分支");
    branch!.BuyRumor("branch", new Wallet { Cash = 1000 }, () => { });
    var branchView = branch.GetRumors().Single();
    Check(branchView.Content.StartsWith("有人透露，未来数日") && branchView.Content.EndsWith("或受波及。") && !branchView.Content.Contains("不会") && !branchView.Content.Contains("落空") && !branchView.Content.Contains("test-water") && !branchView.Content.Contains("净水设施故障") && !branchView.Content.Contains("3") && branchView.DueDay == 4, $"{(truth ? "真" : "假")}内幕同模板隐藏事件标题、标识和真实日期");
    branch.ActivateEvent("test-water", "3/branch", 3);
    branch.ActivateEvent("test-water", "3/branch", 3);
    branch.ResolveRumors(Array.Empty<ScheduledEvent>());
    var reputation = branch.State.Reputations[seller.Key];
    Check(branch.GetRumors().Single().Resolution == (truth ? RumorResolution.True : RumorResolution.False) && reputation.Correct == (truth ? 1 : 0) && reputation.Incorrect == (truth ? 0 : 1), $"{(truth ? "真" : "假")}内幕验证方向正确且信誉仅计一次");
}

decimal Reliability(decimal baseline, string[] sectors, int correct = 0, int incorrect = 0)
{
    var e = Engine(); e.State.LastDay = 1;
    e.State.Reputations["source"] = new SellerReputation { Correct = correct, Incorrect = incorrect };
    return e.CreateRumorOffer("reliability", new SellerProfile { Key = "source", BaseReliability = baseline, Sectors = sectors.ToList() }, new[] { new ScheduledEvent { EventId = "test-water", OccurrenceKey = "3/r", Day = 3 } })!.Reliability;
}
var baseConfidence = Reliability(.75m, Array.Empty<string>());
var relatedConfidence = Reliability(.75m, new[] { "water" });
Check(baseConfidence == .75m && relatedConfidence == .85m, "身份基准加相关行业十个百分点");
Check(Reliability(.75m, new[] { "water" }, correct: 1) > relatedConfidence && Reliability(.75m, new[] { "water" }, incorrect: 1) < relatedConfidence, "历史正确与错误分别提升和降低后续可信度");
Check(Reliability(.9m, new[] { "water" }, correct: 100) == .9m && Reliability(.2m, Array.Empty<string>(), incorrect: 100) == .2m, "内幕可信度始终限制在百分之二十至九十");
var configuredHints = MarketConfig.CreateDefault();
var hintEngine = new MarketEngine(configuredHints);
Check(configuredHints.Events.All(rule => !string.IsNullOrWhiteSpace(rule.TrueHint) && !string.IsNullOrWhiteSpace(rule.FalseHint) && rule.TrueHint != rule.FalseHint && !hintEngine.BuildRumorHint(rule, true).Contains(rule.Description) && !hintEngine.BuildRumorHint(rule, false).Contains(rule.Description)), "默认事件提供相反供需的模糊陈述且不复制真实事件标题");
var fallbackRule = new EventRule { EventId = "private-test", Description = "绝密事件标题", Sectors = new() { "medical" }, Impacts = new() { ["medical"] = .1m } };
Check(hintEngine.BuildRumorHint(fallbackRule, true) == "药品、医疗耗材相关公司行情可能走强" && hintEngine.BuildRumorHint(fallbackRule, false) == "药品、医疗耗材相关公司行情可能走弱", "缺少自定义线索时真假均使用同句式行业方向预测");

engine = Engine();
engine.State.Companies["water"].Price = 100.25m;
engine.State.Positions["water"].Quantity = 2;
var buyFractional = engine.QuoteOrder("water", OrderSide.Buy, 2, 1000);
var sellFractional = engine.QuoteOrder("water", OrderSide.Sell, 2, 1000);
Check(buyFractional.Success && buyFractional.Fee == 1.0025m && buyFractional.CashDelta == -202 && sellFractional.Success && sellFractional.CashDelta == 199, "非整数价格买入向上取整、卖出扣费后向下取整");

engine = Engine();
wallet = new Wallet { Cash = 1000, OnChange = _ => { } };
failure = engine.PlaceOrder("water", OrderSide.Buy, 1, wallet, () => { });
Check(!failure.Success && wallet.Cash == 1000 && engine.State.Positions["water"].Quantity == 0 && !engine.State.TradingFrozen, "钱包拒绝变更时拒单且恢复原状态");
engine = Engine();
wallet = new Wallet { Cash = 1000 };
var walletCalls = 0;
wallet.OnChange = delta =>
{
    if (walletCalls++ == 0) { wallet.Cash += delta / 2; throw new InvalidOperationException("模拟部分扣款"); }
    wallet.Cash += delta;
};
failure = engine.PlaceOrder("water", OrderSide.Buy, 1, wallet, () => { });
Check(!failure.Success && wallet.Cash == 1000 && engine.State.Positions["water"].Quantity == 0 && !engine.State.TradingFrozen, "部分扣款后抛错按实际余额恢复，不遗留持仓");
engine = Engine();
wallet = new Wallet { Cash = 1000 };
walletCalls = 0;
wallet.OnChange = delta => { if (walletCalls++ == 0) wallet.Cash += delta; };
failure = engine.PlaceOrder("water", OrderSide.Buy, 1, wallet, () => throw new IOException("模拟保存失败"));
Check(!failure.Success && wallet.Cash == 899 && engine.State.TradingFrozen && engine.State.Positions["water"].Quantity == 0 && !engine.PlaceOrder("water", OrderSide.Buy, 1, wallet, () => { }).Success, "钱包拒绝补偿时冻结所有后续订单");

engine = Engine();
var derived = EventImpactResolver.EnsureRule(engine, "structured-event", new[] { new CommodityPriceModifier { ItemType = "MEDICAL", Percent = 100 }, new CommodityPriceModifier { ItemType = "ENERGY_CELL", Percent = -100 } });
Check(derived != null && derived.Impacts.Count >= 5 && derived.Impacts["medical"] > 0 && derived.Impacts["energy"] < 0 && derived.Impacts.Values.All(x => Math.Abs(x) <= engine.Config.StructuredEventLimit), "真实商品价格修正为未知事件生成正负交叉影响并限制幅度");
var explicitRule = engine.Config.Events.Single(x => x.EventId == "test-water");
var resolved = EventImpactResolver.EnsureRule(engine, "test-water", new[] { new CommodityPriceModifier { ItemType = "MEDICAL", Percent = 100 } });
Check(ReferenceEquals(resolved, explicitRule) && resolved.Impacts["water"] == -.1m && !resolved.Impacts.ContainsKey("medical"), "显式事件规则优先于自动推导且不被覆盖");
Check(EventImpactResolver.EnsureRule(engine, "unknown-type", new[] { new CommodityPriceModifier { ItemType = "not-real-type", Percent = 100 } }) == null && EventImpactResolver.EnsureRule(engine, "zero-change", new[] { new CommodityPriceModifier { ItemType = "MEDICAL", Percent = 0 } }) == null, "未知商品类型和零价格修正均不生成事件规则");

engine = Engine();
engine.Config.DailyLimit = 0m;
engine.Config.Events.Add(new EventRule { EventId = "candle-rise", Description = "上行事件", Impacts = new() { ["water"] = .1m } });
engine.Config.Events.Add(new EventRule { EventId = "candle-drop", Description = "下行事件", Impacts = new() { ["water"] = -.2m } });
Check(engine.State.Companies.Values.All(c => c.Candles.Count == 0), "未接入游戏日期时不虚构前日 K 线");
engine.AdvanceDay(7);
var candles = engine.State.Companies["water"].Candles;
Check(candles.Count == 1 && candles[0].Day == 7 && candles[0].IsPartial && candles[0].Open == null && candles[0].High == null && candles[0].Low == null, "首次盘中接入当天标记不完整且不伪造开高低");
engine.AdvanceDay(8);
var currentCandle = candles.Single(c => c.Day == 8);
Check(!currentCandle.IsPartial && currentCandle.Open == 100m && currentCandle.High == 100m && currentCandle.Low == 100m && currentCandle.Close == 100m, "正常新日变价前用前收创建完整 OHLC");
engine.ActivateEvent("candle-rise", "8/up", 8);
engine.ActivateEvent("candle-drop", "8/down", 8);
engine.RecordTrade("candle-customer", "candle-trade", new[] { "WATER" }, 1000m);
engine.CloseCustomer("candle-customer");
Check(currentCandle.Open == 100m && currentCandle.High == 110m && currentCandle.Low == 88m && currentCandle.Close == engine.State.Companies["water"].Price && currentCandle.Close > 88m && candles.Count == 2, "同日事件与顾客变价累积真实高低收且不重置开盘");
var beforeDuplicate = engine.Serialize();
engine.AdvanceDay(8); engine.ActivateEvent("candle-rise", "8/up", 8); engine.CloseCustomer("candle-customer");
Check(engine.Serialize() == beforeDuplicate, "重复日事件和遣散回调不重复更新日 K 线");
var closeDay8 = currentCandle.Close;
engine.AdvanceDay(9);
Check(candles.Single(c => c.Day == 9).Open == closeDay8 && candles.Single(c => c.Day == 8).Close == closeDay8, "跨日开盘承接前收且前日 K 线保持封存");
var candleSaved = engine.Serialize();
var reloadedCandles = new MarketEngine(engine.Config, MarketEngine.Deserialize(candleSaved, engine.Config));
Check(reloadedCandles.Serialize() == candleSaved && !reloadedCandles.AdvanceDay(9), "完整日 K 线存档恢复后不重新结算当天");
reloadedCandles.ActivateEvent("candle-rise", "9/up", 9);
var continuedCandle = reloadedCandles.State.Companies["water"].Candles.Single(c => c.Day == 9);
Check(continuedCandle.Open == closeDay8 && continuedCandle.High == continuedCandle.Close && continuedCandle.Low == closeDay8 && reloadedCandles.State.Companies["water"].Candles.Count == 3, "读档后当日价格继续更新原有 OHLC");
reloadedCandles.Config.HistoryLimit = 2;
reloadedCandles.AdvanceDay(10);
Check(reloadedCandles.State.Companies.Values.All(c => c.Candles.Count == 2 && c.Candles[0].Day == 9 && c.Candles[1].Day == 10), "日 K 线按日期保留有限数量");

var legacy = new MarketState { LastDay = 4, Companies = new() { ["water"] = new CompanyState { Price = 130m, History = new() { new PricePoint { Day = 2, Price = 110m }, new PricePoint { Day = 2, Price = 90m }, new PricePoint { Day = 4, Price = 120m }, new PricePoint { Day = 4, Price = 130m } } } } };
var oldJson = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(legacy, MarketEngine.JsonOptions))!;
oldJson["Companies"]!["water"]!.AsObject().Remove("Candles");
var migrated = new MarketEngine(engine.Config, MarketEngine.Deserialize(oldJson.ToJsonString(), engine.Config));
var oldCandles = migrated.State.Companies["water"].Candles;
Check(migrated.State.Version == 1 && oldCandles.Count == 2 && oldCandles[0].Day == 2 && oldCandles[0].Close == 90m && oldCandles[1].Day == 4 && oldCandles[1].Close == 130m && oldCandles.All(c => c.IsPartial && c.Open == null && c.High == null && c.Low == null), "旧 schema 按已知日末采样迁移且缺日开高低保持未知");
var migratedSnapshot = migrated.Serialize();
Check(new MarketEngine(engine.Config, MarketEngine.Deserialize(migratedSnapshot, engine.Config)).Serialize() == migratedSnapshot, "旧档迁移保存后再次读取不重复生成 K 线");
migrated.AdvanceDay(5);
Check(oldCandles.Last().Day == 5 && oldCandles.Last().Open == 130m && !oldCandles.Last().IsPartial && oldCandles.Take(oldCandles.Count - 1).All(c => c.IsPartial), "迁移后下一正常日开始完整记录且不将旧样本冒充完整数据");
invalid = false;
try
{
    var corrupt = System.Text.Json.Nodes.JsonNode.Parse(migrated.Serialize())!;
    corrupt["Companies"]!["water"]!["Candles"]!.AsArray().Last()!["High"] = .01m;
    MarketEngine.Deserialize(corrupt.ToJsonString(), engine.Config);
}
catch (ArgumentException) { invalid = true; }
Check(invalid, "日 K 线高价低于已知开收时拒绝损坏存档");
Console.WriteLine($"全部通过：{count} 项断言。");

sealed class Wallet : IWallet
{
    public int Cash { get; set; }
    public Action<int>? OnChange { get; set; }
    public void ChangeCash(int delta) { if (OnChange != null) OnChange(delta); else Cash = checked(Cash + delta); }
}
