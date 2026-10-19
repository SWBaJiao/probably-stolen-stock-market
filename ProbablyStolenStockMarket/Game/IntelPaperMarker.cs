using System;
using System.Security.Cryptography;
using System.Text;

namespace ProbablyStolenStockMarket.Game;

// 纸条仅翻译档案号前缀，哈希仍由原始报价编号生成，并兼容旧中文纸条。
internal static class IntelPaperMarker
{
    private static readonly string[] Prefixes = { "\n档案号：", "\nArchive no.: ", "\n記録番号：", "\n기록 번호: " };
    private static string Hash(string offerId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(offerId)))[..12];
    internal static string Canonical(string offerId) => Prefixes[0] + Hash(offerId);
    internal static bool Matches(string? sign, string offerId)
    {
        if (sign == null || string.IsNullOrEmpty(offerId)) return false;
        string hash = Hash(offerId);
        // 必须有完整的既定前缀，普通文档的任意哈希后缀不构成标记。
        foreach (var prefix in Prefixes)
            if (sign.EndsWith(prefix + hash, StringComparison.Ordinal)) return true;
        return false;
    }
}
