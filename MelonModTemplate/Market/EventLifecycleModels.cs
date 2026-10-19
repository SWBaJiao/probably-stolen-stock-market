using System.Collections.Generic;

namespace MelonModTemplate.Market;

public sealed class ActiveMarketEvent
{
    public string EventId { get; set; } = "";
    public string OccurrenceKey { get; set; } = "";
    public int StartDay { get; set; }
    public int? ExpectedEndDayExclusive { get; set; }
    public bool IsPermanent { get; set; }
    public bool IsPublic { get; set; } = true;
}
public sealed class EventCompanyEffect
{
    public decimal ReferenceInitialImpact { get; set; }
    public decimal AppliedInitialImpact { get; set; }
    public decimal CurrentFactor { get; set; } = 1m;
    public decimal RetainedTargetFactor { get; set; } = 1m;
    public decimal ContinuationUsed { get; set; }
    public decimal RecoveryCarry { get; set; }
    public bool RecoveryComplete { get; set; }
}
public sealed class EventLifecycleState
{
    public bool StockOwned { get; set; }
    public string ImpactSource { get; set; } = "";
    // 表示已完成的方向兼容处理版本；已经首冲的真实历史快照不会随之重写。
    public int DirectionVersion { get; set; }
    public string EventId { get; set; } = "";
    public string OccurrenceKey { get; set; } = "";
    public int StartDay { get; set; }
    public int FirstObservedDay { get; set; }
    public int LastAdvanceDay { get; set; }
    public int ActiveConfirmedThroughDay { get; set; } = -1;
    public int? ExpectedEndDayExclusive { get; set; }
    public int LastActiveDay { get; set; } = -1;
    public bool NativeActive { get; set; } = true;
    public bool IsPermanent { get; set; }
    public bool IsPublic { get; set; }
    public string PublicDescription { get; set; } = "";
    public decimal PreheatChance { get; set; }
    public int ShockPlanVersion { get; set; }
    public int ShockDay { get; set; }
    public bool ShockApplied { get; set; }
    public bool ShockCancelled { get; set; }
    public ulong PreheatSeed { get; set; }
    public bool LegacyAdopted { get; set; }
    public bool RecoveryTargetFrozen { get; set; }
    public decimal RetentionRatio { get; set; }
    public Dictionary<string, EventCompanyEffect> Companies { get; set; } = new();
}
