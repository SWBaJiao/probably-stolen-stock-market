using System.Collections.Generic;

namespace ProbablyStolenStockMarket.Market;

public static class BrokerIds
{
    public const string Wenzhou = "wenzhou";
    public const string Jianqing = "jianqing";
    public const string JianqingSellerKey = "npc:jianqing";
}

public enum IntelPaperState { Pending, Delivered, Uncertain }
public enum BrokerPhoneBookingStatus { Booked, Arrived, Completed, Cancelled, Expired }
public sealed class BrokerPhoneBookingRecord
{
    public string BrokerId { get; set; } = BrokerIds.Wenzhou;
    public string VisitId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public int RequestDay { get; set; }
    public int ArrivalDay { get; set; }
    public BrokerPhoneBookingStatus Status { get; set; }
}
public sealed class BrokerPhoneBookingView
{
    public string BrokerId { get; set; } = BrokerIds.Wenzhou;
    public string VisitId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public int RequestDay { get; set; }
    public int ArrivalDay { get; set; }
    public BrokerPhoneBookingStatus Status { get; set; }
    public string SourceName { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string ProductKind { get; set; } = "";
}
public sealed class BrokerVisitState
{
    public string BrokerId { get; set; } = BrokerIds.Wenzhou;
    public long Sequence { get; set; }
    public int LastArrivalDay { get; set; } = -1;
    public string VisitId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string AppearanceKey { get; set; } = "";
    public int PlannedDay { get; set; } = -1;
    public int ArrivalDay { get; set; } = -1;
    public bool HasArrived { get; set; }
    public bool HasMetBroker { get; set; }
    public bool Departed { get; set; }
    public bool IsManual { get; set; }
    public bool IsPhoneBooking { get; set; }
    public List<BrokerPhoneBookingRecord> PhoneBookings { get; set; } = new();
}
// 报价投影只提供来源和产品类别，不提供未购买的公司与方向。
public sealed class BrokerVisitView
{
    public string BrokerId { get; set; } = BrokerIds.Wenzhou;
    public string VisitId { get; set; } = "";
    public string OfferId { get; set; } = "";
    public string AppearanceKey { get; set; } = "";
    public int PlannedDay { get; set; }
    public int ArrivalDay { get; set; }
    public bool HasArrived { get; set; }
    public bool Departed { get; set; }
    public bool IsManual { get; set; }
    public bool IsPhoneBooking { get; set; }
    public string SourceName { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string ProductKind { get; set; } = "";
    public int Price { get; set; }
    public bool Purchased { get; set; }
    public bool Expired { get; set; }
}
// 纸条、日历和投资面板只能读取已购投影，不接触真实事件与内部真假标记。
public sealed class IntelRecord
{
    public string PublicNews { get; set; } = "";
    public string VerificationSummary { get; set; } = "";
    public string BrokerId { get; set; } = BrokerIds.Wenzhou;
    public string OfferId { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public decimal Reliability { get; set; }
    public bool IsGroupIdentity { get; set; }
    public int Correct { get; set; }
    public int Incorrect { get; set; }
    public string Industry { get; set; } = "";
    public string Content { get; set; } = "";
    public Dictionary<string, int> CompanyDirections { get; set; } = new();
    public int PurchaseDay { get; set; }
    public int ObservationStartDay { get; set; }
    public int ObservationEndDay { get; set; }
    public RumorResolution Status { get; set; }
    public IntelPaperState PaperState { get; set; }
    public int PaperItemId { get; set; } = -1;
    public bool CalendarEligible { get; set; }
}
