using System;
using System.Collections.Generic;

namespace MelonModTemplate.Market;

public interface IWallet { int Cash { get; } void ChangeCash(int delta); }
public enum OrderSide { Buy, Sell }
public enum RumorResolution { Pending, True, False, Unverified }
public sealed class CompanyDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}
public sealed class SectorDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> ItemTypes { get; set; } = new();
    public Dictionary<string, decimal> Weights { get; set; } = new();
}
public sealed class EventRule
{
    public string EventId { get; set; } = "";
    public string Description { get; set; } = "";
    public string TrueHint { get; set; } = "";
    public string FalseHint { get; set; } = "";
    public Dictionary<string, decimal> Impacts { get; set; } = new();
    public List<string> Sectors { get; set; } = new();
}
public sealed class SellerProfile
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Identity { get; set; } = "普通顾客";
    public decimal BaseReliability { get; set; } = 0.50m;
    public List<string> Sectors { get; set; } = new();
    public bool IsGroupIdentity { get; set; }
}
public sealed class SellerRule
{
    public string Identifier { get; set; } = "";
    public string Faction { get; set; } = "";
    public string Identity { get; set; } = "普通顾客";
    public decimal BaseReliability { get; set; } = .50m;
    public List<string> Sectors { get; set; } = new();
}
public sealed class ScheduledEvent
{
    public string EventId { get; set; } = "";
    public string OccurrenceKey { get; set; } = "";
    public int Day { get; set; }
}
public sealed class PricePoint { public int Day { get; set; } public decimal Price { get; set; } public string Reason { get; set; } = ""; }
public sealed class DailyCandle
{
    public int Day { get; set; }
    // 旧档和首次接入当天缺少完整日内记录，未知的开高低保持为空。
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal Close { get; set; }
    public bool IsPartial { get; set; }
}
public sealed class CompanyState
{
    public int EventDriftDay { get; set; } = -1;
    public decimal EventDriftRatio { get; set; } = 1m;
    public decimal PersistentEventFactor { get; set; } = 1m;
    public decimal Price { get; set; } = 100m;
    public decimal DailyRetailImpact { get; set; }
    public List<PricePoint> History { get; set; } = new();
    public List<DailyCandle> Candles { get; set; } = new();
    public int IntradayDay { get; set; } = -1;
    // 当前日顺序样本独立保存，不受历史采样数量限制。
    public List<PricePoint> IntradayPoints { get; set; } = new();
    public bool IntradayIsPartial { get; set; }
}
public sealed class Position { public int Quantity { get; set; } public decimal Cost { get; set; } public decimal RealizedProfit { get; set; } }
public sealed class LedgerEntry
{
    public int Day { get; set; }
    public string Kind { get; set; } = "";
    public string Description { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public int Quantity { get; set; }
    public int CashDelta { get; set; }
}
public sealed class CustomerSession
{
    public HashSet<string> TradeIds { get; set; } = new();
    public Dictionary<string, decimal> Signals { get; set; } = new();
    public bool Closed { get; set; }
}
public sealed class SellerReputation { public int Correct { get; set; } public int Incorrect { get; set; } }
public sealed class RumorOffer
{
    public bool IsBrokerOffer { get; set; }
    public bool CalendarEligible { get; set; }
    public int PurchaseDay { get; set; } = -1;
    public int ObservationStartDay { get; set; } = -1;
    public string Industry { get; set; } = "";
    public string ProductKind { get; set; } = "";
    public Dictionary<string, int> CompanyDirections { get; set; } = new();
    public IntelPaperState PaperState { get; set; }
    public int PaperItemId { get; set; } = -1;
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string SellerKey { get; set; } = "";
    public string SellerName { get; set; } = "";
    public string Identity { get; set; } = "";
    public decimal Reliability { get; set; }
    public decimal PriorReliability { get; set; }
    public bool IsGroupIdentity { get; set; }
    public int Price { get; set; }
    public int DueDay { get; set; }
    public int WindowEndDay { get; set; }
    public string EventId { get; set; } = "";
    public string OccurrenceKey { get; set; } = "";
    public string Content { get; set; } = "";
    public bool PredictsOccurrence { get; set; }
    public bool Purchased { get; set; }
    public bool Expired { get; set; }
    public RumorResolution Resolution { get; set; }
    public List<string> CompanyIds { get; set; } = new();
}
// 界面仅接收投影，未购买时不暴露事件与真假依据。
public sealed class RumorView
{
    public string Id { get; set; } = "";
    public string SellerName { get; set; } = "";
    public string Identity { get; set; } = "";
    public bool IsGroupIdentity { get; set; }
    public int Price { get; set; }
    public decimal Reliability { get; set; }
    public int Correct { get; set; }
    public int Incorrect { get; set; }
    public int DueDay { get; set; }
    public bool Purchased { get; set; }
    public bool Expired { get; set; }
    public string Content { get; set; } = "购买后查看线索";
    public List<string> CompanyIds { get; set; } = new();
    public RumorResolution Resolution { get; set; }
}
public sealed class MarketState
{
    public Dictionary<string, EventLifecycleState> EventLifecycles { get; set; } = new();
    public Dictionary<string, decimal> EventRetentions { get; set; } = new();
    public HashSet<string> CompletedEventLifecycles { get; set; } = new();
    public BrokerVisitState Broker { get; set; } = new();
    public int Version { get; set; } = 1;
    public string SaveIdentity { get; set; } = "";
    public int LastDay { get; set; } = -1;
    public int TradeSettlementVersion { get; set; }
    public Dictionary<int, Dictionary<string, decimal>> TradeSignalsByDay { get; set; } = new();
    public ulong RandomState { get; set; } = 123456789;
    public bool StorageVerified { get; set; }
    public bool TradingFrozen { get; set; }
    public string FreezeReason { get; set; } = "";
    public bool TerminalGranted { get; set; }
    public Dictionary<string, CompanyState> Companies { get; set; } = new();
    public Dictionary<string, Position> Positions { get; set; } = new();
    public Dictionary<string, CustomerSession> Sessions { get; set; } = new();
    public HashSet<string> ProcessedEvents { get; set; } = new();
    public HashSet<string> OccurredEvents { get; set; } = new();
    public Dictionary<string, Dictionary<string, int>> ObservedEventDirections { get; set; } = new();
    public Dictionary<string, RumorOffer> Rumors { get; set; } = new();
    public HashSet<string> OfferedSessions { get; set; } = new();
    public Dictionary<string, SellerReputation> Reputations { get; set; } = new();
    public List<LedgerEntry> Ledger { get; set; } = new();
}
public sealed class OrderQuote
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public OrderSide Side { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Fee { get; set; }
    public int CashDelta { get; set; }
}
public sealed class OperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public static OperationResult Ok(string message) => new() { Success = true, Message = message };
    public static OperationResult Fail(string message) => new() { Message = message };
}
