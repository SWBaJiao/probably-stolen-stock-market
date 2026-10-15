using System.IO;
using MelonLoader.Utils;

namespace MelonModTemplate;

public static class ModPaths
{
    // 使用程序集名与构建部署目录保持一致，文件由功能模块按需读取。
    public static string AssetsDirectory => Path.Combine(
        MelonEnvironment.UserDataDirectory,
        typeof(ModEntry).Assembly.GetName().Name!,
        "Assets");
}
