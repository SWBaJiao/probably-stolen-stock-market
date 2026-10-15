using System;
using System.Collections.Generic;
using System.Linq;

namespace MelonModTemplate.Market;

public sealed partial class MarketEngine
{
    public BrokerVisitView? TryPlanBrokerVisit(IReadOnlyList<ScheduledEvent> scheduled, bool manual = false)
    {
        if (_busy || State.LastDay < 0) return null;
        var visit = State.Broker;
        if (visit.IsPhoneBooking) return GetDueBrokerPhoneVisit(scheduled);
        if (visit.PlannedDay == State.LastDay)
        {
            if (State.Rumors.TryGetValue(visit.OfferId, out var sameOffer) && !sameOffer.Purchased && !StillScheduled(sameOffer, scheduled)) sameOffer.Expired = true;
            return visit.Departed ? null : GetBrokerVisit();
        }
        if (visit.VisitId.Length > 0) CloseBrokerVisit(visit.VisitId);
        if (!manual && visit.LastArrivalDay >= 0 && State.LastDay - visit.LastArrivalDay < Config.BrokerCooldownDays) return null;
        return CreateBrokerVisitAt(scheduled, State.LastDay, manual, false);
    }

    private BrokerVisitView? CreateBrokerVisitAt(IReadOnlyList<ScheduledEvent> scheduled, int plannedDay, bool manual, bool phone)
    {
        var visit = State.Broker;
        var candidates = scheduled.Where(e => e.Day > plannedDay && e.Day <= plannedDay + Config.RumorHorizon && !State.OccurredEvents.Contains(EventKey(e.EventId, e.OccurrenceKey)))
            .Select(e => (scheduled: e, rule: Config.Events.FirstOrDefault(r => r.EventId == e.EventId)))
            .Where(x => x.rule != null && x.rule.Impacts.Any(p => p.Value != 0 && Config.Companies.Any(c => c.Id == p.Key)))
            .OrderBy(x => x.scheduled.Day).ThenBy(x => x.scheduled.EventId, StringComparer.Ordinal).ThenBy(x => x.scheduled.OccurrenceKey, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) return null;
        var candidate = candidates[(int)(NextRandom() * candidates.Count)];
        var rule = candidate.rule!;
        var nonzero = rule.Impacts.Where(p => p.Value != 0 && Config.Companies.Any(c => c.Id == p.Key)).ToDictionary(p => p.Key, p => Math.Sign(p.Value));
        var sectors = Config.Sectors.Where(s => rule.Sectors.Contains(s.Id) && s.Weights.Keys.Any(nonzero.ContainsKey)).ToList();
        if (sectors.Count == 0) sectors = Config.Sectors.Where(s => s.Weights.Keys.Any(nonzero.ContainsKey)).ToList();
        var industryOffer = NextRandom() < .5m && sectors.Count > 0;
        var sector = sectors.Count > 0 ? sectors[(int)(NextRandom() * sectors.Count)] : null;
        Dictionary<string, int> directions;
        if (industryOffer) directions = nonzero.Where(p => sector!.Weights.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        else
        {
            var companies = nonzero.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            var company = companies[(int)(NextRandom() * companies.Count)];
            directions = new() { [company.Key] = company.Value };
            var companySectors = sectors.Where(s => s.Weights.ContainsKey(company.Key)).ToList();
            if (companySectors.Count == 0) companySectors = Config.Sectors.Where(s => s.Weights.ContainsKey(company.Key)).ToList();
            sector = companySectors.Count > 0 ? companySectors[(int)(NextRandom() * companySectors.Count)] : null;
        }
        var sources = Config.Sellers.Where(s => s.Sectors.Count == 0 || sector != null && s.Sectors.Contains(sector.Id)).Select(s => new SellerProfile { Key = "type:" + s.Identifier + "|" + s.Faction, Name = s.Identity, Identity = s.Identity, BaseReliability = s.BaseReliability, Sectors = s.Sectors.ToList(), IsGroupIdentity = true }).ToList();
        sources.Add(new SellerProfile { Key = "type:anonymous|", Name = "匿名消息人", Identity = "普通匿名来源", BaseReliability = .5m, IsGroupIdentity = true });
        var source = sources[(int)(NextRandom() * sources.Count)];
        var related = sector != null && source.Sectors.Contains(sector.Id);
        var prior = Math.Clamp(source.BaseReliability + (related ? .1m : 0m), .2m, .9m);
        var reputation = Reputation(source.Key);
        var reliability = Math.Clamp((prior * 10m + reputation.Correct) / (10m + reputation.Correct + reputation.Incorrect), .2m, .9m);
        var correct = NextRandom() < reliability;
        if (!correct) directions = directions.ToDictionary(p => p.Key, p => -p.Value);
        var id = "broker:" + State.SaveIdentity + ":" + (++visit.Sequence);
        var predictions = directions.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Config.Companies.First(c => c.Id == p.Key).Name + (p.Value > 0 ? "走势可能走强" : "走势可能走弱"));
        var offer = new RumorOffer
        {
            Id = id, SessionId = id, IsBrokerOffer = true, SellerKey = source.Key, SellerName = source.Name, Identity = source.Identity,
            IsGroupIdentity = true, Reliability = reliability, PriorReliability = prior, Price = Config.RumorPrice,
            DueDay = candidate.scheduled.Day, WindowEndDay = plannedDay + Config.RumorHorizon,
            EventId = candidate.scheduled.EventId, OccurrenceKey = candidate.scheduled.OccurrenceKey,
            PredictsOccurrence = correct, CompanyIds = directions.Keys.ToList(), CompanyDirections = directions,
            Industry = sector?.Name ?? "综合贸易", ProductKind = industryOffer ? "行业情报" : "公司情报",
            Content = "有人透露，未来" + Config.RumorHorizon + "日，" + (industryOffer ? "与" + (sector?.Name ?? "综合贸易") + "类商品相关的部分公司：" : "消息指向") + string.Join("；", predictions) + "。以上为来源传闻，不保证准确。"
        };
        if (visit.VisitId.Length > 0) CloseBrokerVisit(visit.VisitId);
        State.Rumors.Add(id, offer);
        visit.VisitId = id;
        visit.OfferId = id;
        visit.AppearanceKey = "broker-look-" + (int)(NextRandom() * 8m);
        visit.PlannedDay = plannedDay;
        visit.ArrivalDay = -1;
        visit.HasArrived = false;
        visit.Departed = false;
        visit.IsManual = manual;
        visit.IsPhoneBooking = phone;
        return GetBrokerVisit();
    }

    public OperationResult BookBrokerByPhone(IReadOnlyList<ScheduledEvent> scheduled)
    {
        if (_busy || State.LastDay < 0) return OperationResult.Fail("当前无法登记情报商预约。");
        if (State.Broker.HasArrived && !State.Broker.Departed)
        {
            var current = State.Broker.PhoneBookings.LastOrDefault();
            if (State.Broker.IsPhoneBooking && current?.Status == BrokerPhoneBookingStatus.Arrived && current.ArrivalDay == State.LastDay) return OperationResult.Ok("本次预约的情报商已经到访。");
            return OperationResult.Fail("当前情报商尚未离开，请先完成本次接待。");
        }
        RefreshPhoneBooking(scheduled);
        var latest = State.Broker.PhoneBookings.LastOrDefault();
        if (latest != null && (latest.Status == BrokerPhoneBookingStatus.Booked || latest.Status == BrokerPhoneBookingStatus.Arrived)) return OperationResult.Ok("已预约第 " + latest.ArrivalDay + " 日到访。");
        if (latest != null && latest.RequestDay == State.LastDay) return OperationResult.Fail("今日登记的预约已经结束或失效，请改日再联系。");
        if (State.Broker.HasArrived && !State.Broker.Departed) return OperationResult.Fail("当前情报商尚未离开，请先完成本次接待。");
        var visit = CreateBrokerVisitAt(scheduled, State.LastDay + 1, false, true);
        if (visit == null) return OperationResult.Fail("明日到访后没有可售的未来情报，暂时无法预约。");
        State.Broker.PhoneBookings.Add(new BrokerPhoneBookingRecord { VisitId = visit.VisitId, OfferId = visit.OfferId, RequestDay = State.LastDay, ArrivalDay = State.LastDay + 1, Status = BrokerPhoneBookingStatus.Booked });
        return OperationResult.Ok("已预约第 " + (State.LastDay + 1) + " 日到访。");
    }

    public BrokerPhoneBookingView? GetBrokerPhoneBooking()
    {
        var record = State.Broker.PhoneBookings.LastOrDefault();
        if (record == null || !State.Rumors.TryGetValue(record.OfferId, out var offer)) return null;
        return new BrokerPhoneBookingView { VisitId = record.VisitId, OfferId = record.OfferId, RequestDay = record.RequestDay, ArrivalDay = record.ArrivalDay, Status = record.Status, SourceName = offer.SellerName, SourceIdentity = offer.Identity, ProductKind = offer.Industry + "类·" + offer.ProductKind };
    }

    public BrokerVisitView? GetDueBrokerPhoneVisit(IReadOnlyList<ScheduledEvent> scheduled)
    {
        if (_busy || !State.Broker.IsPhoneBooking) return null;
        RefreshPhoneBooking(scheduled);
        var record = State.Broker.PhoneBookings.LastOrDefault();
        if (record == null || record.VisitId != State.Broker.VisitId || record.ArrivalDay != State.LastDay || record.Status != BrokerPhoneBookingStatus.Booked && record.Status != BrokerPhoneBookingStatus.Arrived) return null;
        var view = GetBrokerVisit();
        return view == null || view.Expired || view.Purchased ? null : view;
    }

    public bool CancelBrokerPhoneBooking()
    {
        if (_busy) return false;
        var record = State.Broker.PhoneBookings.LastOrDefault();
        if (record == null || record.VisitId != State.Broker.VisitId || record.Status != BrokerPhoneBookingStatus.Booked) return false;
        record.Status = BrokerPhoneBookingStatus.Cancelled;
        CloseBrokerVisit(record.VisitId);
        return true;
    }

    private void RefreshPhoneBooking(IReadOnlyList<ScheduledEvent> scheduled)
    {
        var record = State.Broker.PhoneBookings.LastOrDefault();
        if (record == null || record.VisitId != State.Broker.VisitId || record.Status != BrokerPhoneBookingStatus.Booked && record.Status != BrokerPhoneBookingStatus.Arrived) return;
        if (State.LastDay > record.ArrivalDay) record.Status = BrokerPhoneBookingStatus.Expired;
        else if (!State.Rumors.TryGetValue(record.OfferId, out var offer) || !StillScheduledAt(offer, scheduled, record.ArrivalDay)) record.Status = BrokerPhoneBookingStatus.Cancelled;
        else return;
        CloseBrokerVisit(record.VisitId);
    }

    public bool MarkBrokerArrived(string visitId)
    {
        var visit = State.Broker;
        if (visit.VisitId != visitId || visit.Departed || visit.PlannedDay != State.LastDay || !State.Rumors.TryGetValue(visit.OfferId, out var offer) || offer.Expired) return false;
        if (visit.HasArrived) return true;
        var booking = visit.PhoneBookings.LastOrDefault();
        if (visit.IsPhoneBooking && (booking == null || booking.VisitId != visitId || booking.ArrivalDay != State.LastDay || booking.Status != BrokerPhoneBookingStatus.Booked)) return false;
        if (!visit.IsManual && !visit.IsPhoneBooking && visit.LastArrivalDay >= 0 && State.LastDay - visit.LastArrivalDay < Config.BrokerCooldownDays) return false;
        visit.HasArrived = true;
        visit.ArrivalDay = State.LastDay;
        visit.LastArrivalDay = State.LastDay;
        if (visit.IsPhoneBooking) booking!.Status = BrokerPhoneBookingStatus.Arrived;
        return true;
    }

    public bool MarkBrokerIntroduced(string visitId)
    {
        var visit = State.Broker;
        if (_busy || visit.VisitId != visitId || !visit.HasArrived || visit.Departed) return false;
        visit.HasMetBroker = true;
        return true;
    }

    public bool SetBrokerAppearance(string visitId, string appearanceKey)
    {
        if (State.Broker.VisitId != visitId || State.Broker.Departed || string.IsNullOrWhiteSpace(appearanceKey) || appearanceKey.Length > 256) return false;
        State.Broker.AppearanceKey = appearanceKey;
        return true;
    }

    public bool CloseBrokerVisit(string visitId)
    {
        var visit = State.Broker;
        if (visit.VisitId != visitId || visit.Departed) return false;
        visit.Departed = true;
        var booking = visit.PhoneBookings.LastOrDefault();
        if (visit.IsPhoneBooking && booking != null && booking.VisitId == visitId && (booking.Status == BrokerPhoneBookingStatus.Booked || booking.Status == BrokerPhoneBookingStatus.Arrived)) booking.Status = BrokerPhoneBookingStatus.Completed;
        if (State.Rumors.TryGetValue(visit.OfferId, out var offer) && !offer.Purchased) offer.Expired = true;
        return true;
    }

    public BrokerVisitView? GetBrokerVisit()
    {
        var v = State.Broker;
        if (v.VisitId.Length == 0 || !State.Rumors.TryGetValue(v.OfferId, out var offer)) return null;
        return new BrokerVisitView { VisitId = v.VisitId, OfferId = v.OfferId, AppearanceKey = v.AppearanceKey, PlannedDay = v.PlannedDay, ArrivalDay = v.ArrivalDay, HasArrived = v.HasArrived, Departed = v.Departed, IsManual = v.IsManual, IsPhoneBooking = v.IsPhoneBooking, SourceName = offer.SellerName, SourceIdentity = offer.Identity, ProductKind = offer.Industry + "类·" + offer.ProductKind, Price = offer.Price, Purchased = offer.Purchased, Expired = offer.Expired || v.Departed || (v.IsPhoneBooking ? State.LastDay > v.PlannedDay : v.PlannedDay != State.LastDay) };
    }

    public OperationResult BuyBrokerOffer(string offerId, IReadOnlyList<ScheduledEvent> scheduled, IWallet wallet, Action persist)
    {
        if (_busy) return OperationResult.Fail("上一笔交易尚未完成。");
        if (!State.StorageVerified || State.TradingFrozen) return OperationResult.Fail("存档尚未验证，或交易已冻结。");
        var visit = State.Broker;
        if (visit.OfferId != offerId || !visit.HasArrived || visit.Departed || visit.ArrivalDay != State.LastDay || visit.PlannedDay != State.LastDay) return OperationResult.Fail("情报商已离开或尚未到访。");
        if (!State.Rumors.TryGetValue(offerId, out var offer) || !offer.IsBrokerOffer || offer.Purchased || offer.Expired || !StillScheduled(offer, scheduled)) return OperationResult.Fail("这条情报已购买或不再适用于未来观察窗口。");
        if (wallet.Cash < offer.Price) return OperationResult.Fail("余额不足。");
        return Transact(wallet, -offer.Price, persist, () =>
        {
            offer.Purchased = true;
            offer.CalendarEligible = true;
            offer.PurchaseDay = State.LastDay;
            offer.ObservationStartDay = State.LastDay + 1;
            offer.WindowEndDay = State.LastDay + Config.RumorHorizon;
            offer.PaperState = IntelPaperState.Pending;
            offer.PaperItemId = -1;
            var booking = State.Broker.PhoneBookings.LastOrDefault();
            if (State.Broker.IsPhoneBooking && booking != null && booking.VisitId == State.Broker.VisitId) booking.Status = BrokerPhoneBookingStatus.Completed;
            Log("rumor", "通过情报商购买 " + offer.SellerName + " 的" + offer.ProductKind, "", 0, -offer.Price);
        });
    }

    private bool StillScheduled(RumorOffer offer, IReadOnlyList<ScheduledEvent> scheduled) => StillScheduledAt(offer, scheduled, State.LastDay);
    private bool StillScheduledAt(RumorOffer offer, IReadOnlyList<ScheduledEvent> scheduled, int baselineDay) =>
        !State.OccurredEvents.Contains(EventKey(offer.EventId, offer.OccurrenceKey)) && scheduled.Any(e => e.EventId == offer.EventId && e.OccurrenceKey == offer.OccurrenceKey && e.Day == offer.DueDay && e.Day > baselineDay && e.Day <= baselineDay + Config.RumorHorizon);

    public IReadOnlyList<IntelRecord> GetIntelRecords() => State.Rumors.Values.Where(r => r.IsBrokerOffer && r.Purchased && r.CalendarEligible).OrderBy(r => r.PurchaseDay).ThenBy(r => r.Id, StringComparer.Ordinal).Select(r => new IntelRecord { OfferId = r.Id, SourceName = r.SellerName, SourceIdentity = r.Identity, Reliability = r.Reliability, IsGroupIdentity = r.IsGroupIdentity, Correct = State.Reputations.GetValueOrDefault(r.SellerKey)?.Correct ?? 0, Incorrect = State.Reputations.GetValueOrDefault(r.SellerKey)?.Incorrect ?? 0, Industry = r.Industry, Content = r.Content, CompanyDirections = new Dictionary<string, int>(r.CompanyDirections), PurchaseDay = r.PurchaseDay, ObservationStartDay = r.ObservationStartDay, ObservationEndDay = r.WindowEndDay, Status = r.Resolution, PaperState = r.PaperState, PaperItemId = r.PaperItemId, CalendarEligible = r.CalendarEligible }).ToList();

    public bool SetIntelPaperState(string offerId, IntelPaperState status, int itemId = -1)
    {
        if (_busy || !Enum.IsDefined(typeof(IntelPaperState), status) || !State.Rumors.TryGetValue(offerId, out var offer) || !offer.IsBrokerOffer || !offer.Purchased || !offer.CalendarEligible || itemId < -1) return false;
        if (offer.PaperState == IntelPaperState.Delivered && status != IntelPaperState.Delivered) return false;
        if (offer.PaperState == IntelPaperState.Delivered && itemId != offer.PaperItemId) return false;
        if (offer.PaperState == IntelPaperState.Uncertain && status == IntelPaperState.Pending) return false;
        if (status == IntelPaperState.Delivered && itemId < 0) return false;
        offer.PaperState = status;
        offer.PaperItemId = itemId;
        return true;
    }

    // 仅在适配层已经确认物品从未插入背包且已销毁后调用；未知结果不能使用此恢复入口。
    public bool ResetUndeliveredPaper(string offerId, int expectedItemId)
    {
        if (_busy || expectedItemId < 0 || !State.Rumors.TryGetValue(offerId, out var offer) || !offer.IsBrokerOffer || !offer.Purchased || !offer.CalendarEligible || offer.PaperState != IntelPaperState.Uncertain || offer.PaperItemId != expectedItemId) return false;
        offer.PaperState = IntelPaperState.Pending;
        offer.PaperItemId = -1;
        return true;
    }

    private static void ValidateBrokerState(MarketState state)
    {
        var broker = state.Broker;
        if (state.ObservedEventDirections == null || state.ObservedEventDirections.Values.Any(d => d == null || d.Any(p => !state.Companies.ContainsKey(p.Key) || p.Value != 1 && p.Value != -1))) throw new ArgumentException("情报观察事件存档无效");
        if (broker == null || broker.Sequence < 0 || broker.LastArrivalDay < -1 || broker.LastArrivalDay > state.LastDay || broker.PlannedDay < -1 || (long)broker.PlannedDay > state.LastDay + (broker.IsPhoneBooking ? 1L : 0L) || broker.ArrivalDay < -1 || broker.ArrivalDay > state.LastDay || broker.VisitId == null || broker.OfferId == null || broker.AppearanceKey == null || broker.HasArrived && broker.ArrivalDay < 0 || broker.PhoneBookings == null) throw new ArgumentException("情报商到访存档无效");
        if (broker.PhoneBookings.Any(b => b == null || string.IsNullOrWhiteSpace(b.VisitId) || string.IsNullOrWhiteSpace(b.OfferId) || b.RequestDay < 0 || b.RequestDay > state.LastDay || (long)b.ArrivalDay != (long)b.RequestDay + 1 || !Enum.IsDefined(typeof(BrokerPhoneBookingStatus), b.Status) || !state.Rumors.TryGetValue(b.OfferId, out var savedOffer) || !savedOffer.IsBrokerOffer) || broker.PhoneBookings.Select(b => b.VisitId).Distinct().Count() != broker.PhoneBookings.Count) throw new ArgumentException("情报商电话预约存档无效");
        var latestBooking = broker.PhoneBookings.LastOrDefault();
        if (broker.IsPhoneBooking && (latestBooking == null || latestBooking.VisitId != broker.VisitId || latestBooking.OfferId != broker.OfferId || latestBooking.ArrivalDay != broker.PlannedDay)) throw new ArgumentException("情报商电话预约与到访不一致");
        if (broker.OfferId.Length > 0 && (!state.Rumors.TryGetValue(broker.OfferId, out var quote) || !quote.IsBrokerOffer)) throw new ArgumentException("情报商报价存档无效");
        foreach (var offer in state.Rumors.Values)
        {
            if (offer.CompanyDirections == null || !Enum.IsDefined(typeof(IntelPaperState), offer.PaperState) || offer.PaperItemId < -1) throw new ArgumentException("情报纸条存档无效");
            if (!offer.IsBrokerOffer) continue;
            if (offer.CompanyDirections.Count == 0 || offer.CompanyDirections.Any(p => !state.Companies.ContainsKey(p.Key) || p.Value != 1 && p.Value != -1) || offer.CalendarEligible && !offer.Purchased || offer.Purchased && (offer.PurchaseDay < 0 || offer.ObservationStartDay != offer.PurchaseDay + 1 || offer.WindowEndDay < offer.ObservationStartDay) || offer.PaperState == IntelPaperState.Delivered && offer.PaperItemId < 0) throw new ArgumentException("已购情报存档无效");
        }
    }
}
