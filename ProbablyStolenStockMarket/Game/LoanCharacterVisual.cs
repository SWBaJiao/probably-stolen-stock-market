
namespace ProbablyStolenStockMarket.Game;

// 沈衡的独立像素立绘沿用原版人物的尺寸、锚点和最近邻采样路径。
internal sealed class LoanCharacterVisual : BrokerCharacterVisual
{
    internal const string SpriteKey = "probably_stolen_stock.shenheng";
    internal LoanCharacterVisual(ModLogger log) : base(log, SpriteKey, "shenheng.png", "沈衡") { }
}
