using System;

namespace ProbablyStolenStockMarket.Game;

// 与原版现金一起写入 modData，不使用独立文件保存持仓。
internal sealed class AdapterSave
{
    public int Version { get; set; } = 1;
    public string SaveIdentity { get; set; } = "";
    public string MarketJson { get; set; } = "";
    public string ProbeNonce { get; set; } = Guid.NewGuid().ToString("N");
    public bool WrittenBySave { get; set; }
    public int SavedCash { get; set; }
    public int TerminalItemId { get; set; } = -1;
    public bool TerminalPlacementPending { get; set; }
    public string TerminalInventoryPath { get; set; } = "";
    public TerminalAccountState TerminalAccount { get; set; } = new();
    public bool IntelCardDelivered { get; set; }
    public int IntelCardItemId { get; set; } = -1;
    public bool IntelCardUncertain { get; set; }
    public long SessionSequence { get; set; }
    public long TradeSequence { get; set; }
    public string SessionId { get; set; } = "";
    public string ClientKey { get; set; } = "";
    public bool SessionClosed { get; set; }
}
