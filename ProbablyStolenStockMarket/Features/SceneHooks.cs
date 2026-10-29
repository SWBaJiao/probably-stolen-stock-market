using System;

namespace ProbablyStolenStockMarket.Features;

// 在此模块扩展场景相关功能；入口统一负责订阅与解绑。
public sealed class SceneHooks
{
    private readonly ModLogger _logger;

    public int InitializedCount { get; private set; }
    public int UnloadedCount { get; private set; }
    public string LastInitializedSceneName { get; private set; } = string.Empty;

    public SceneHooks(ModLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void OnSceneInitialized(int buildIndex, string sceneName)
    {
        InitializedCount++;
        LastInitializedSceneName = sceneName;
        _logger.Msg($"场景初始化 #{InitializedCount}：{sceneName}（索引 {buildIndex}）");
    }

    public void OnSceneUnloaded(int buildIndex, string sceneName)
    {
        UnloadedCount++;
        _logger.Msg($"场景卸载 #{UnloadedCount}：{sceneName}（索引 {buildIndex}）");
    }

    public void LogSummary()
    {
        _logger.Msg($"本次运行记录：初始化 {InitializedCount} 次，卸载 {UnloadedCount} 次，最后初始化场景：{LastInitializedSceneName}");
    }
}
