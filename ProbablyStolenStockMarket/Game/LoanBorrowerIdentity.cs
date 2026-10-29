using System;
using System.Reflection;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Game;

// 只在签约时读取当前 Steam 昵称；接口不可用时不主动初始化 Steam。
internal static class LoanBorrowerIdentity
{
    internal static string ReadName()
    {
        try
        {
            var friends = Type.GetType("Steamworks.SteamFriends, com.rlabrecque.steamworks.net", false);
            var getter = friends?.GetMethod("GetPersonaName", BindingFlags.Public | BindingFlags.Static,
                null, Type.EmptyTypes, null);
            return LoanContractDocument.NormalizeBorrowerName(getter?.Invoke(null, null) as string);
        }
        catch { return LoanContractDocument.DefaultBorrowerName; }
    }
}
