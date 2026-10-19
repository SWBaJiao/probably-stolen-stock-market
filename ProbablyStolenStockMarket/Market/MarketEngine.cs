using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProbablyStolenStockMarket.Market;

public sealed partial class MarketEngine
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public MarketConfig Config { get; }
    public MarketState State { get; private set; }
    private bool _busy;
    public Action<string>? Diagnostic { get; set; }

    public MarketEngine(MarketConfig config, MarketState? state = null)
    {
        config.Validate();
        Config = config;
        State = state ?? new MarketState();
        foreach (var c in config.Companies)
        {
            if (!State.Companies.ContainsKey(c.Id)) State.Companies[c.Id] = new CompanyState { Price = config.InitialPrice };
            if (!State.Positions.ContainsKey(c.Id)) State.Positions[c.Id] = new Position();
        }
        ValidateState(State, config);
        MigrateShockBudget(State);
        ReconcileEventDirections(State, config);
        MigrateTradeSignals(State);
        MigrateCandles(State, config);
        MigrateIntraday(State);
        EnsureStockCalendar(State.LastDay);
    }

    public string Serialize() => JsonSerializer.Serialize(State, JsonOptions);
    // 由存档适配层接入时统一应用；启用交易不会清除已有的资金异常冻结。
    public void ApplyStorageAccess(bool confirmedRoundTrip) => State.StorageVerified = confirmedRoundTrip || !Config.RequireSaveReloadVerification;
    public static MarketState Deserialize(string json, MarketConfig config)
    {
        var state = JsonSerializer.Deserialize<MarketState>(json, JsonOptions) ?? throw new ArgumentException("股市存档为空");
        ValidateState(state, config);
        MigrateShockBudget(state);
        ReconcileEventDirections(state, config);
        MigrateTradeSignals(state);
        MigrateCandles(state, config);
        MigrateIntraday(state);
        return state;
    }

    private static void ValidateState(MarketState state, MarketConfig config)
    {
        if (state.Version != 1 || state.LastDay < -1 || state.RandomState == 0 || state.Companies == null || state.Positions == null || state.Sessions == null || state.ProcessedEvents == null || state.OccurredEvents == null || state.Rumors == null || state.OfferedSessions == null || state.Reputations == null || state.Ledger == null) throw new ArgumentException("股市存档版本或结构无效");
        if (state.Companies.Any(x => x.Value == null || x.Value.Price < .01m || x.Value.Price > 10000000m || x.Value.DailyRetailImpact < 0 || x.Value.DailyRetailImpact > 1m || x.Value.History == null || x.Value.History.Any(p => p == null || p.Price < .01m)) || state.Positions.Any(x => x.Value == null || x.Value.Quantity < 0 || x.Value.Cost < 0 || !state.Companies.ContainsKey(x.Key))) throw new ArgumentException("股市存档行情或持仓无效");
        if (state.Companies.Values.Any(c => c.Candles == null || c.Candles.Any(k => !IsValidCandle(k, state.LastDay)) || c.Candles.Select(k => k.Day).Distinct().Count() != c.Candles.Count || !c.Candles.Select(k => k.Day).SequenceEqual(c.Candles.Select(k => k.Day).OrderBy(d => d)))) throw new ArgumentException("股市存档日 K 线无效");
        if (state.Companies.Values.Any(c => c.IntradayDay < -1 || c.IntradayDay > state.LastDay || c.IntradayPoints == null || c.IntradayPoints.Any(p => p == null || p.Day != c.IntradayDay || p.Day < 0 || p.Price < .01m || p.Price > 10000000m))) throw new ArgumentException("股市存档当日分时样本无效");
        if (state.Sessions.Any(x => x.Value == null || x.Value.TradeIds == null || x.Value.Signals == null || x.Value.Signals.Keys.Any(k => !state.Companies.ContainsKey(k))) || state.Reputations.Any(x => x.Value == null || x.Value.Correct < 0 || x.Value.Incorrect < 0) || state.Rumors.Any(x => x.Value == null || x.Value.ImpactSource == null || x.Value.DirectionVersion < 0 || x.Value.DirectionVersion > EventImpactResolver.CurrentDirectionVersion || x.Key != x.Value.Id || x.Value.Price < 0 || x.Value.Reliability < (x.Value.IsBrokerOffer && x.Value.BrokerId == BrokerIds.Jianqing ? 0m : .2m) || x.Value.Reliability > (x.Value.IsBrokerOffer && x.Value.BrokerId == BrokerIds.Jianqing ? 1m : .9m) || x.Value.CompanyIds == null)) throw new ArgumentException("股市存档成交或情报无效");
        if (state.TradeSettlementVersion < 0 || state.TradeSettlementVersion > 1 || state.TradeSignalsByDay == null || state.TradeSettlementVersion == 0 && state.TradeSignalsByDay.Count > 0 || state.TradeSignalsByDay.Any(d => d.Key < 0 || d.Key > state.LastDay || d.Value == null || d.Value.Keys.Any(id => !state.Companies.ContainsKey(id)))) throw new ArgumentException("股市存档每日成交信号无效");
        ValidateBrokerState(state);
        ValidateEventLifecycles(state);
        ValidateStockState(state);
        // 持仓公司不能被配置删除，否则无法出售已有股份。
        if (state.Positions.Any(x => x.Value.Quantity > 0 && !config.Companies.Any(c => c.Id == x.Key))) throw new ArgumentException("公司配置删除了仍有持仓的公司");
    }

    private static bool IsValidCandle(DailyCandle? candle, int lastDay)
    {
        if (candle == null || candle.Day < 0 || candle.Day > lastDay || candle.Close < .01m || candle.Close > 10000000m) return false;
        var known = new[] { candle.Open, candle.High, candle.Low }.Where(p => p.HasValue).Select(p => p!.Value).ToArray();
        if (known.Any(p => p < .01m || p > 10000000m)) return false;
        if (!candle.IsPartial && known.Length != 3) return false;
        if (candle.High.HasValue && (candle.High.Value < candle.Close || candle.Open.HasValue && candle.High.Value < candle.Open.Value)) return false;
        if (candle.Low.HasValue && (candle.Low.Value > candle.Close || candle.Open.HasValue && candle.Low.Value > candle.Open.Value)) return false;
        return !candle.High.HasValue || !candle.Low.HasValue || candle.High.Value >= candle.Low.Value;
    }

    private static void MigrateCandles(MarketState state, MarketConfig config)
    {
        foreach (var company in state.Companies.Values)
        {
            if (company.Candles.Count > 0) continue;
            // 旧采样没有变价前的开盘值，也不能证明日内极值；只迁移已知收盘样本。
            foreach (var day in company.History.Where(p => p.Day >= 0 && p.Day <= state.LastDay).GroupBy(p => p.Day).OrderBy(g => g.Key))
                company.Candles.Add(new DailyCandle { Day = day.Key, Close = day.Last().Price, IsPartial = true });
            if (state.LastDay >= 0)
            {
                var current = company.Candles.LastOrDefault();
                if (current == null || current.Day != state.LastDay) company.Candles.Add(new DailyCandle { Day = state.LastDay, Close = company.Price, IsPartial = true });
                else current.Close = company.Price;
            }
            TrimCandles(company, config.HistoryLimit);
        }
    }

    private static void MigrateIntraday(MarketState state)
    {
        if (state.LastDay < 0) return;
        foreach (var company in state.Companies.Values)
        {
            if (company.IntradayDay == state.LastDay && company.IntradayPoints.Count > 0) continue;
            company.IntradayDay = state.LastDay;
            company.IntradayIsPartial = true;
            // 旧档只恢复当天实际保留的采样，既不借用旧日，也不补造缺失的日初路径。
            company.IntradayPoints = company.History.Where(p => p.Day == state.LastDay)
                .Select(p => new PricePoint { Day = p.Day, Price = p.Price, Reason = p.Reason }).ToList();
            if (company.IntradayPoints.Count == 0 || company.IntradayPoints.Last().Price != company.Price)
                company.IntradayPoints.Add(new PricePoint { Day = state.LastDay, Price = company.Price, Reason = "接入时当前价" });
        }
    }

    private static void MigrateTradeSignals(MarketState state)
    {
        if (state.TradeSettlementVersion == 1) return;
        if (state.LastDay >= 0)
        {
            var pending = new Dictionary<string, decimal>();
            // 旧档只有未结束会话尚未影响过行情；无法还原逐笔日期，统一归入已知当前日一次。
            foreach (var session in state.Sessions.Values.Where(s => !s.Closed))
                foreach (var signal in session.Signals) pending[signal.Key] = pending.GetValueOrDefault(signal.Key) + signal.Value;
            if (pending.Count > 0) state.TradeSignalsByDay[state.LastDay] = pending;
        }
        state.TradeSettlementVersion = 1;
    }

    public Dictionary<string, decimal> GetWeights(IEnumerable<string> types)
    {
        var tags = types.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = Config.Sectors.Where(s => s.ItemTypes.Any(tags.Contains)).ToList();
        var result = new Dictionary<string, decimal>();
        foreach (var sector in matches)
            foreach (var w in sector.Weights) result[w.Key] = result.GetValueOrDefault(w.Key) + w.Value / matches.Count;
        if (matches.Count == 0) Diagnostic?.Invoke("未映射商品类型：" + string.Join(",", tags));
        return result;
    }

    public bool RecordTrade(string sessionId, string tradeId, IEnumerable<string> types, decimal signedValue)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(tradeId) || signedValue == 0) return false;
        if (State.LastDay < 0) { Diagnostic?.Invoke("游戏日期尚未接入，跳过无法归日的成交信号。"); return false; }
        if (!State.Sessions.TryGetValue(sessionId, out var session)) State.Sessions[sessionId] = session = new CustomerSession();
        if (session.Closed || !session.TradeIds.Add(tradeId)) return false;
        if (!State.TradeSignalsByDay.TryGetValue(State.LastDay, out var daily)) State.TradeSignalsByDay[State.LastDay] = daily = new Dictionary<string, decimal>();
        foreach (var w in GetWeights(types))
        {
            var signal = Math.Clamp(signedValue, -int.MaxValue, int.MaxValue) * w.Value;
            session.Signals[w.Key] = session.Signals.GetValueOrDefault(w.Key) + signal;
            daily[w.Key] = daily.GetValueOrDefault(w.Key) + signal;
        }
        return true;
    }

    public bool CloseCustomer(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        if (!State.Sessions.TryGetValue(sessionId, out var session)) State.Sessions[sessionId] = session = new CustomerSession();
        if (session.Closed) return false;
        session.Closed = true;
        foreach (var rumor in State.Rumors.Values.Where(x => x.SessionId == sessionId && !x.Purchased)) rumor.Expired = true;
        if (session.Signals.Any(x => x.Value != 0)) Log("customer", "顾客会话已封存；成交按原成交日汇总，在次日行情统一结算。");
        return true;
    }

    public bool AdvanceDay(int day)
    {
        if (day < 0 || day <= State.LastDay) return false;
        // 首次接入只对当前一天推进，不模拟接入前的历史。
        var firstObservation = State.LastDay < 0;
        var first = firstObservation ? day : State.LastDay + 1;
        if ((long)day - first > 10000) throw new ArgumentException("游戏日期跨度异常");
        for (var d = first; d <= day; d++)
        {
            State.TradeSignalsByDay.TryGetValue(d - 1, out var previousTrades);
            State.LastDay = d;
            EnsureStockCalendar(d);
            StartStockEvents(d);
            foreach (var company in Config.Companies)
            {
                var s = State.Companies[company.Id];
                EnsureCandle(s, d, firstObservation);
                BeginIntraday(s, d, firstObservation);
                s.DailyRetailImpact = 0;
                var noise = (NextRandom() * 2m - 1m) * Config.DailyLimit * .75m;
                var reversion = (GetEventAdjustedAnchor(company.Id) / s.Price - 1m) * .01m;
                var signal = previousTrades?.GetValueOrDefault(company.Id) ?? 0m;
                var magnitude = Math.Abs(signal);
                var ratio = magnitude == 0 ? 0m : magnitude >= Config.SignalScale ? 1m / (1m + Config.SignalScale / magnitude) : (magnitude / Config.SignalScale) / (1m + magnitude / Config.SignalScale);
                var tradeInfluence = Config.TradeDailyLimit * Math.Sign(signal) * ratio;
                var baseline = Math.Clamp(noise + reversion, -Config.DailyLimit, Config.DailyLimit);
                var total = Math.Clamp(noise + reversion + tradeInfluence, -Config.DailyLimit, Config.DailyLimit);
                s.DailyRetailImpact = Math.Abs(total - baseline);
                var reason = $"每日行情；前一日（第 {d - 1} 日）成交结算影响 {total - baseline:+0.00%;-0.00%;0.00%}，合计日变动 {total:+0.00%;-0.00%;0.00%}";
                MovePrice(company.Id, total, reason, Config.DailyLimit);
            }
            AdvanceEventLifecycles(d);
            foreach (var oldDay in State.TradeSignalsByDay.Keys.Where(k => k < d).ToArray()) State.TradeSignalsByDay.Remove(oldDay);
        }
        Log("day", $"第 {day} 日行情已更新。");
        return true;
    }

    // 缺少结束元数据时不会猜测事件时长，须由 EndEvent 或权威活动快照结束。
    public bool ActivateEvent(string eventId, string occurrenceKey, int day, bool isPublic = true, int? endExclusive = null, bool isPermanent = false)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(occurrenceKey)) return false;
        if (State.LastDay < 0) { Diagnostic?.Invoke("游戏日期尚未接入，跳过无法归日的事件激活。"); return false; }
        var key = EventKey(eventId, occurrenceKey);
        var observedDay = State.LastDay >= 0 ? State.LastDay : Math.Max(0, day);
        var native = new ActiveMarketEvent { EventId = eventId, OccurrenceKey = occurrenceKey, StartDay = day, ExpectedEndDayExclusive = endExclusive, IsPermanent = isPermanent, IsPublic = isPublic };
        if (!State.ProcessedEvents.Add(key))
        {
            // 重复回调不重放冲击；旧档仍在生效的事件只接续今后的影响。
            if (!State.EventLifecycles.ContainsKey(key)) RegisterEventLifecycle(native, observedDay, null, true);
            return false;
        }
        State.OccurredEvents.Add(key);
        var rule = Config.Events.FirstOrDefault(x => x.EventId == eventId);
        State.ObservedEventDirections[key] = rule == null ? new() : rule.Impacts.Where(p => p.Value != 0 && State.Companies.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => Math.Sign(p.Value));
        if (rule != null)
        {
            RegisterEventLifecycle(native, observedDay, null, false);
            AdvanceEventLifecycles(observedDay);
            // 零方向规则仍保留原有已生效采样语义，不创建无意义的生命周期。
            foreach (var impact in rule.Impacts.Where(p => p.Value == 0m))
                MovePrice(impact.Key, 0m, isPublic ? rule.Description : "市场异动");
            if (isPublic) Log("event", string.IsNullOrEmpty(rule.Description) ? "公开事件已生效，关联公司行情可能受影响。" : rule.Description);
        }
        else Diagnostic?.Invoke("未映射事件标识：" + eventId);
        ResolveOccurredRumors();
        return true;
    }

    public SellerProfile MatchSeller(string identifier, string faction, string name, bool personalIdentity = false)
    {
        var rule = Config.Sellers.FirstOrDefault(x => x.Identifier == identifier && x.Identifier.Length > 0) ?? Config.Sellers.FirstOrDefault(x => x.Identifier.Length == 0 && x.Faction == faction && faction.Length > 0);
        return new SellerProfile { Key = personalIdentity ? "person:" + identifier : "type:" + identifier + ":" + faction, Name = name, Identity = rule?.Identity ?? "普通顾客", BaseReliability = rule?.BaseReliability ?? .5m, Sectors = rule?.Sectors.ToList() ?? new(), IsGroupIdentity = !personalIdentity };
    }

    public RumorView? CreateRumorOffer(string sessionId, SellerProfile seller, IReadOnlyList<ScheduledEvent> scheduled)
    {
        if (State.Rumors.TryGetValue(sessionId, out var existing)) return View(existing);
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(seller.Key) || State.Sessions.TryGetValue(sessionId, out var session) && session.Closed || !State.OfferedSessions.Add(sessionId)) return null;
        var candidates = scheduled.Where(e => e.Day > State.LastDay && e.Day <= State.LastDay + Config.RumorHorizon && !State.OccurredEvents.Contains(EventKey(e.EventId, e.OccurrenceKey)))
            .Select(e => (scheduled: e, rule: FindScheduledRule(e)))
            .Where(x => x.rule != null && x.rule.Impacts.Count > 0 && (seller.Sectors.Count == 0 || x.rule.Sectors.Any(seller.Sectors.Contains)))
            .OrderBy(x => x.scheduled.Day).ThenBy(x => x.scheduled.EventId, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) return null;
        var selected = candidates[(int)(NextRandom() * candidates.Count)];
        var related = selected.rule!.Sectors.Any(seller.Sectors.Contains);
        var prior = Math.Clamp(seller.BaseReliability + (related ? .1m : 0m), .2m, .9m);
        var rep = Reputation(seller.Key);
        var reliability = Math.Clamp((prior * 10m + rep.Correct) / (10m + rep.Correct + rep.Incorrect), .2m, .9m);
        var predictsOccurrence = NextRandom() < reliability;
        var companies = selected.rule.Impacts.Keys.ToList();
        var names = string.Join("、", companies.Select(id => Config.Companies.First(x => x.Id == id).Name));
        var offer = new RumorOffer { ImpactSource = selected.rule.ImpactSource, DirectionVersion = selected.rule.DirectionVersion, Id = sessionId, SessionId = sessionId, SellerKey = seller.Key, SellerName = seller.Name, Identity = seller.Identity, IsGroupIdentity = seller.IsGroupIdentity, Reliability = reliability, PriorReliability = prior, Price = Config.RumorPrice, DueDay = selected.scheduled.Day, WindowEndDay = State.LastDay + Config.RumorHorizon, EventId = selected.scheduled.EventId, OccurrenceKey = selected.scheduled.OccurrenceKey, PredictsOccurrence = predictsOccurrence, CompanyIds = companies, Content = $"有人透露，未来数日{BuildRumorHint(selected.rule, predictsOccurrence)}；{names} 或受波及。" };
        State.Rumors.Add(sessionId, offer);
        return View(offer);
    }

    public IReadOnlyList<RumorView> GetRumors() => State.Rumors.Values.Where(x => x.Purchased || !x.Expired).Select(View).ToList();
    public string BuildRumorHint(EventRule rule, bool correct)
    {
        // 真伪使用同样的陈述形式，不通过固定的否定句或真实事件标题泄露答案。
        var configured = correct ? rule.TrueHint : rule.FalseHint;
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var sectors = string.Join("、", Config.Sectors.Where(s => rule.Sectors.Contains(s.Id)).Select(s => s.Name));
        if (sectors.Length == 0) sectors = "相关行业";
        var direction = rule.Impacts.Values.Sum();
        if (direction == 0) direction = rule.Impacts.Values.FirstOrDefault(v => v != 0);
        var rising = direction >= 0;
        if (!correct) rising = !rising;
        return $"{sectors}相关公司行情可能{(rising ? "走强" : "走弱")}";
    }
    public void ResolveRumors(IReadOnlyList<ScheduledEvent> stillScheduled)
    {
        ResolveOccurredRumors();
        foreach (var rumor in State.Rumors.Values.Where(x => x.Purchased && x.Resolution == RumorResolution.Pending))
        {
            // 未实际发生的计划事件只能记为未验证，取消不惩罚售卖者。
            if (State.LastDay > rumor.DueDay && !stillScheduled.Any(e => e.EventId == rumor.EventId && e.OccurrenceKey == rumor.OccurrenceKey)) rumor.Resolution = RumorResolution.Unverified;
        }
    }

    private void ResolveOccurredRumors()
    {
        foreach (var rumor in State.Rumors.Values.Where(x => x.Purchased && x.Resolution == RumorResolution.Pending && State.OccurredEvents.Contains(EventKey(x.EventId, x.OccurrenceKey))))
        {
            var correct = rumor.PredictsOccurrence;
            if (rumor.IsBrokerOffer)
            {
                if (!State.ObservedEventDirections.TryGetValue(EventKey(rumor.EventId, rumor.OccurrenceKey), out var observed) || rumor.CompanyDirections.Any(p => !observed.ContainsKey(p.Key)))
                {
                    rumor.Resolution = RumorResolution.Unverified;
                    continue;
                }
                correct = rumor.CompanyDirections.All(p => observed[p.Key] == p.Value);
            }
            rumor.Resolution = correct ? RumorResolution.True : RumorResolution.False;
            var rep = Reputation(rumor.SellerKey);
            if (correct) rep.Correct++; else rep.Incorrect++;
        }
    }
    private RumorView View(RumorOffer offer)
    {
        var rep = Reputation(offer.SellerKey);
        return new RumorView { PublicNews = IntelPublicNews(offer), VerificationSummary = IntelVerificationSummary(offer), BrokerId = offer.BrokerId, Id = offer.Id, SellerName = offer.SellerName, Identity = offer.Identity, IsGroupIdentity = offer.IsGroupIdentity, Price = offer.Price, Reliability = offer.Reliability, Correct = rep.Correct, Incorrect = rep.Incorrect, Purchased = offer.Purchased, Expired = offer.Expired, DueDay = offer.Purchased ? offer.WindowEndDay : 0, Content = offer.Purchased ? offer.Content : "购买后查看线索", CompanyIds = offer.Purchased ? offer.CompanyIds.ToList() : new(), Resolution = offer.Purchased ? offer.Resolution : RumorResolution.Pending };
    }
    private SellerReputation Reputation(string key)
    {
        if (!State.Reputations.TryGetValue(key, out var r)) State.Reputations[key] = r = new SellerReputation();
        return r;
    }

    public OrderQuote QuoteOrder(string companyId, OrderSide side, int quantity, int cash)
    {
        var quote = new OrderQuote { CompanyId = companyId, Side = side, Quantity = quantity };
        if (!State.StorageVerified || State.TradingFrozen) { quote.Message = State.TradingFrozen ? State.FreezeReason : "请先保存并重新读取存档，验证投资状态持久化。"; return quote; }
        if (side != OrderSide.Buy && side != OrderSide.Sell || quantity <= 0 || !State.Companies.TryGetValue(companyId, out var company) || !Config.Companies.Any(c => c.Id == companyId)) { quote.Message = "公司或交易数量无效。"; return quote; }
        quote.UnitPrice = company.Price;
        var gross = company.Price * quantity;
        quote.Fee = Math.Max(Config.MinimumFee, gross * Config.FeeRate);
        var total = side == OrderSide.Buy ? -decimal.Ceiling(gross + quote.Fee) : decimal.Floor(gross - quote.Fee);
        if (total < int.MinValue || total > int.MaxValue || total == int.MinValue) { quote.Message = "订单金额超出游戏资金范围。"; return quote; }
        quote.CashDelta = (int)total;
        if (side == OrderSide.Sell && total <= 0) { quote.Message = "卖出收入不足以支付手续费，请增加数量。"; return quote; }
        if (side == OrderSide.Sell && State.Positions[companyId].Quantity < quantity) { quote.Message = "可卖持仓不足。"; return quote; }
        if ((long)cash + quote.CashDelta < 0 || (long)cash + quote.CashDelta > int.MaxValue) { quote.Message = "余额不足或资金超出上限。"; return quote; }
        if (side == OrderSide.Buy && (long)State.Positions[companyId].Quantity + quantity > int.MaxValue) { quote.Message = "持仓数量超出上限。"; return quote; }
        quote.Success = true; quote.Message = "报价有效"; return quote;
    }

    public OperationResult PlaceOrder(string companyId, OrderSide side, int quantity, IWallet wallet, Action persist)
    {
        var quote = QuoteOrder(companyId, side, quantity, wallet.Cash);
        if (!quote.Success) return OperationResult.Fail(quote.Message);
        return Transact(wallet, quote.CashDelta, persist, () =>
        {
            var position = State.Positions[companyId];
            if (side == OrderSide.Buy) { position.Quantity += quantity; position.Cost -= quote.CashDelta; }
            else { var releasedCost = position.Cost * quantity / position.Quantity; position.Quantity -= quantity; position.Cost -= releasedCost; position.RealizedProfit += quote.CashDelta - releasedCost; }
            Log(side == OrderSide.Buy ? "buy" : "sell", $"{(side == OrderSide.Buy ? "买入" : "卖出")} {Config.Companies.First(c => c.Id == companyId).Name} {quantity} 股", companyId, quantity, quote.CashDelta);
        });
    }
    public OperationResult BuyRumor(string id, IWallet wallet, Action persist)
    {
        if (!State.StorageVerified || State.TradingFrozen) return OperationResult.Fail("存档尚未验证，或交易已冻结。");
        if (!State.Rumors.TryGetValue(id, out var offer) || offer.IsBrokerOffer || offer.Purchased || offer.Expired) return OperationResult.Fail("该情报不可购买。");
        if (wallet.Cash < offer.Price) return OperationResult.Fail("余额不足。");
        return Transact(wallet, -offer.Price, persist, () => { offer.Purchased = true; Log("rumor", $"向 {offer.SellerName} 购买内幕", "", 0, -offer.Price); ResolveOccurredRumors(); });
    }

    private OperationResult Transact(IWallet wallet, int delta, Action persist, Action mutate)
    {
        if (_busy) return OperationResult.Fail("上一笔交易尚未完成。");
        _busy = true;
        string? snapshot = null;
        var initialCash = 0;
        var cashTouched = false;
        try
        {
            snapshot = Serialize();
            initialCash = wallet.Cash;
            mutate();
            // 先确认完整状态可序列化，再接触原版资金。
            _ = Serialize();
            cashTouched = true;
            wallet.ChangeCash(delta);
            if ((long)wallet.Cash != (long)initialCash + delta) throw new InvalidOperationException("游戏资金变更结果不符");
            persist();
            return OperationResult.Ok("交易成功。");
        }
        catch (Exception ex)
        {
            if (snapshot == null) return OperationResult.Fail("交易准备失败：" + ex.Message);
            State = Deserialize(snapshot, Config);
            try
            {
                if (cashTouched)
                {
                    var adjustment = (long)initialCash - wallet.Cash;
                    if (adjustment < int.MinValue || adjustment > int.MaxValue) throw new InvalidOperationException("资金补偿超出范围");
                    if (adjustment != 0) wallet.ChangeCash((int)adjustment);
                    if (wallet.Cash != initialCash) throw new InvalidOperationException("资金补偿未完成");
                }
                persist();
            }
            catch (Exception rollback)
            {
                State.TradingFrozen = true;
                State.FreezeReason = "资金或存档补偿失败，请停止交易并检查日志：" + rollback.Message;
                try { persist(); } catch { /* 保留内存冻结状态，避免继续下单。 */ }
            }
            return OperationResult.Fail("交易失败并已尝试恢复：" + ex.Message + (State.TradingFrozen ? "；" + State.FreezeReason : ""));
        }
        finally { _busy = false; }
    }

    private void MovePrice(string id, decimal change, string reason, decimal? maxAbsoluteChange = null)
    {
        var c = State.Companies[id];
        var candle = State.LastDay >= 0 ? EnsureCandle(c, State.LastDay, false) : null;
        if (State.LastDay >= 0 && (c.IntradayDay != State.LastDay || c.IntradayPoints.Count == 0)) BeginIntraday(c, State.LastDay, true);
        var price = Math.Clamp(decimal.Round(c.Price * (1m + change), 2, MidpointRounding.AwayFromZero), .01m, 10000000m);
        if (maxAbsoluteChange.HasValue)
        {
            var lower = Math.Max(.01m, decimal.Ceiling(c.Price * (1m - maxAbsoluteChange.Value) * 100m) / 100m);
            var upper = Math.Min(10000000m, decimal.Floor(c.Price * (1m + maxAbsoluteChange.Value) * 100m) / 100m);
            price = lower <= upper ? Math.Clamp(price, lower, upper) : c.Price;
        }
        c.Price = price;
        if (candle != null)
        {
            candle.Close = c.Price;
            if (candle.High.HasValue) candle.High = Math.Max(candle.High.Value, c.Price);
            if (candle.Low.HasValue) candle.Low = Math.Min(candle.Low.Value, c.Price);
        }
        c.History.Add(new PricePoint { Day = State.LastDay, Price = c.Price, Reason = reason });
        if (State.LastDay >= 0) c.IntradayPoints.Add(new PricePoint { Day = State.LastDay, Price = c.Price, Reason = reason });
        if (c.History.Count > Config.HistoryLimit) c.History.RemoveRange(0, c.History.Count - Config.HistoryLimit);
    }
    private DailyCandle EnsureCandle(CompanyState company, int day, bool partial)
    {
        var existing = company.Candles.LastOrDefault();
        if (existing != null && existing.Day == day) return existing;
        var candle = new DailyCandle { Day = day, Close = company.Price, IsPartial = partial, Open = partial ? null : company.Price, High = partial ? null : company.Price, Low = partial ? null : company.Price };
        company.Candles.Add(candle);
        TrimCandles(company, Config.HistoryLimit);
        return candle;
    }
    private static void TrimCandles(CompanyState company, int limit)
    {
        if (company.Candles.Count > limit) company.Candles.RemoveRange(0, company.Candles.Count - limit);
    }
    private static void BeginIntraday(CompanyState company, int day, bool partial)
    {
        company.IntradayDay = day;
        company.IntradayIsPartial = partial;
        company.IntradayPoints.Clear();
        company.IntradayPoints.Add(new PricePoint { Day = day, Price = company.Price, Reason = partial ? "首次接入基准" : "开盘基准" });
    }
    private void Log(string kind, string text, string company = "", int quantity = 0, int cashDelta = 0)
    {
        State.Ledger.Add(new LedgerEntry { Day = State.LastDay, Kind = kind, Description = text, CompanyId = company, Quantity = quantity, CashDelta = cashDelta });
        if (State.Ledger.Count > Config.LedgerLimit) State.Ledger.RemoveRange(0, State.Ledger.Count - Config.LedgerLimit);
    }
    private decimal NextRandom()
    {
        // 显式保存随机状态，保证读档后不会重新抽取行情和情报。
        var x = State.RandomState;
        x ^= x << 13; x ^= x >> 7; x ^= x << 17;
        State.RandomState = x;
        return (x >> 11) / 9007199254740992m;
    }
    private static string EventKey(string eventId, string occurrenceKey) => eventId.Length + ":" + eventId + ":" + occurrenceKey;
}
