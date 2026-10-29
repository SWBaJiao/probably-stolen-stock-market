using System;
using ProbablyStolenStockMarket.Localization;
using UnityEngine.Localization.Settings;

namespace ProbablyStolenStockMarket.Game;

/// <summary>读取原版 Unity Localization 的当前语言，不使用系统区域设置。</summary>
public static class GameLanguageAdapter
{
    private const long PollIntervalMilliseconds = 250;
    private static long _nextPoll;

    /// <summary>在游戏主线程调用；异步语言初始化完成后才读取结果。</summary>
    public static void Update()
    {
        var now = NativeGameAccess.Milliseconds;
        if (now < _nextPoll) return;
        _nextPoll = now + PollIntervalMilliseconds;

        try
        {
            // 同步 SelectedLocale 可能等待资源初始化，使用异步句柄避免阻塞游戏帧。
            var operation = LocalizationSettings.SelectedLocaleAsync;
            if (!operation.IsDone) return;
            var locale = operation.Result;
            if (locale == null) return;
            Localizer.Language = Localizer.ResolveLanguage(locale.Identifier.Code);
        }
        catch
        {
            // 主菜单或场景切换期间原版对象可能尚未可读，下一次轮询会重新检查。
        }
    }
}
