using System;
using UnityEngine;

namespace ProbablyStolenStockMarket.Game;

// 保留贷款名片原有接口和资源标识，通用渲染实例与其他名片独立保存、恢复和释放。
internal sealed class LoanCardVisual : IDisposable
{
    private readonly BusinessCardVisual _visual;

    public LoanCardVisual(ModLogger log)
    {
        _visual = new BusinessCardVisual(log, "ProbablyStolenStockMarket/loan_card",
            "ProbablyStolenStockMarket/loan_card_legacy", "loan_business_card", "loan_business_card.png", "贷款名片图像");
    }

    public void Apply(GameItem item) => _visual.Apply(item);
    internal static bool TryResolve(GameItemElement item, string name, out Sprite? sprite)
        => BusinessCardVisual.TryResolve(item, name, out sprite);
    public void RestoreForSave() => _visual.RestoreForSave();
    public void ResumeAfterSave() => _visual.ResumeAfterSave();
    public void ResetTracking() => _visual.ResetTracking();
    public void Dispose() => _visual.Dispose();
}
