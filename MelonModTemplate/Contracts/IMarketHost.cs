using MelonModTemplate.Market;

namespace MelonModTemplate.Contracts;

// 面板只通过此接口提交操作，资金和游戏对象始终由适配层处理。
public interface IMarketHost
{
    MarketEngine? Engine { get; }
    bool CanTrade { get; }
    int Cash { get; }
    string Status { get; }
    string SellerDescription { get; }
    string SellerReputation { get; }
    string OfferDescription { get; }
    string? OfferId { get; }
    int? OfferCost { get; }
    bool CanBuyRumor { get; }
    string PlaceOrder(string companyId, int quantity, bool buy);
    string BuyRumor(string? expectedOfferId = null);
    string RecoverTerminal();
}
