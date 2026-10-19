using System.IO;
using MelonLoader.Utils;

namespace ProbablyStolenStockMarket;

public static class ModPaths
{
    // 旧路径仅用于升级兼容，后续写入统一使用正式目录。
    public static string DataDirectory => Path.Combine(MelonEnvironment.UserDataDirectory, "ProbablyStolenStockMarket");
    public static string LegacyDataDirectory => Path.Combine(MelonEnvironment.UserDataDirectory, "MelonModTemplate");
    public static string AssetsDirectory => Path.Combine(DataDirectory, "Assets");

    public static string AssetPath(params string[] parts)
    {
        var relative = Path.Combine(parts);
        var current = Path.Combine(AssetsDirectory, relative);
        if (File.Exists(current)) return current;
        var legacy = Path.Combine(LegacyDataDirectory, "Assets", relative);
        return File.Exists(legacy) ? legacy : current;
    }
}
