using System;
using System.IO;

namespace ProbablyStolenStockMarket;

public static class ModPaths
{
    public static string DataDirectory => ModHelper.GetModDataFolder(ModInfo.Id);
    public static string AssetsDirectory => Path.Combine(ModHelper.GetModFolder(ModInfo.Id)
        ?? throw new InvalidOperationException("股市模组尚未注册安装目录。"), "Assets");

    public static string AssetPath(params string[] parts) => Path.Combine(AssetsDirectory, Path.Combine(parts));
    private static string RelativeAsset(params string[] parts) => "Assets/" + Path.Combine(parts).Replace('\\', '/');
    public static bool AssetExists(params string[] parts) => ModHelper.ModFileExists(ModInfo.Id, RelativeAsset(parts));
    public static byte[] ReadAssetBytes(params string[] parts) => ModHelper.ReadModFileBytes(ModInfo.Id, RelativeAsset(parts))
        ?? throw new InvalidOperationException("缺少股市模组资源：" + RelativeAsset(parts));
    public static string ReadAssetText(params string[] parts) => ModHelper.ReadModFileText(ModInfo.Id, RelativeAsset(parts))
        ?? throw new InvalidOperationException("缺少股市模组资源：" + RelativeAsset(parts));
}
