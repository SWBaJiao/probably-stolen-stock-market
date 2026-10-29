namespace ProbablyStolenStockMarket.Game;

public sealed partial class GameBridge
{
    private IntelCardAdapter? _intelCard;
    internal bool IntelCardDelivered { get => _save.IntelCardDelivered; set => _save.IntelCardDelivered = value; }
    internal int IntelCardItemId { get => _save.IntelCardItemId; set => _save.IntelCardItemId = value; }
    internal bool IntelCardUncertain { get => _save.IntelCardUncertain; set => _save.IntelCardUncertain = value; }
    internal bool IsIntelCard(GameItem item) => _intelCard?.IsOwnCard(item) == true;
    internal void PrepareIntelCardRead(GameItem item) => _intelCard?.PrepareRead(item);
}
