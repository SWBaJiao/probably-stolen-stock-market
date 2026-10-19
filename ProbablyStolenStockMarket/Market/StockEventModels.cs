using System.Collections.Generic;

namespace ProbablyStolenStockMarket.Market;

public sealed class StockEventTemplate
{
    public string Id { get; set; } = "";
    public string PrimaryCompanyId { get; set; } = "";
    public int Polarity { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Sectors { get; set; } = new();
    public Dictionary<string, decimal> Impacts { get; set; } = new();
}

public sealed class StockEventOccurrence
{
    public string Id { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string PrimaryCompanyId { get; set; } = "";
    public int Polarity { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public List<string> Sectors { get; set; } = new();
    public Dictionary<string, decimal> Impacts { get; set; } = new();
    public Dictionary<string, decimal> ActualImpacts { get; set; } = new();
    public decimal Intensity { get; set; }
    public int StartDay { get; set; }
    public int ShockDay { get; set; }
    public int EndDayExclusive { get; set; }
    public ulong PreheatSeed { get; set; }
    public decimal PreheatChance { get; set; }
    public decimal RetentionRatio { get; set; }
    public bool Activated { get; set; }
    public bool Published { get; set; }
    public int PublicDay { get; set; } = -1;
    public bool Cancelled { get; set; }
    public bool Completed { get; set; }
}

public sealed class StockEventCalendar
{
    public string SaveIdentity { get; set; } = "";
    public string SaveToken { get; set; } = "";
    public int Version { get; set; } = 1;
    public bool Initialized { get; set; }
    public ulong RandomState { get; set; }
    public long Sequence { get; set; }
    public int NextShockDay { get; set; } = -1;
    public int PlannedThroughDay { get; set; } = -1;
    public int PositiveCount { get; set; }
    public int NegativeCount { get; set; }
    public Dictionary<string, int> CompanyUseCounts { get; set; } = new();
    public Dictionary<string, int> TemplateLastShockDay { get; set; } = new();
    public Dictionary<string, StockEventOccurrence> Occurrences { get; set; } = new();
}

// 只投影已经公开的事件；计划日期、随机种子和未公开内容不进入新闻接口。
public sealed class StockNewsView
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int PublicDay { get; set; }
    public string Stage { get; set; } = "";
    public Dictionary<string, int> CompanyDirections { get; set; } = new();
    public Dictionary<string, decimal> ActualImpacts { get; set; } = new();
}
