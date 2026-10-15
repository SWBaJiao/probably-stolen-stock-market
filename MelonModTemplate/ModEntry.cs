using System;
using HarmonyLib;
using MelonLoader;
using MelonModTemplate.Configuration;
using MelonModTemplate.Features;
using MelonModTemplate.Game;
using MelonModTemplate.UI;

namespace MelonModTemplate;

public sealed class ModEntry : MelonMod
{
    private SceneHooks? _sceneHooks;
    private GameBridge? _market;
    private NativeStockMarketPanel? _panel;
    private HarmonyLib.Harmony? _marketHarmony;
    private bool _subscribed;
    private DateTime _lastError;

    public override void OnInitializeMelon()
    {
        if (_subscribed)
            return;

        _sceneHooks ??= new SceneHooks(LoggerInstance);
        SubscribeHooks();
        LoggerInstance.Msg($"{ModInfo.Name} {ModInfo.Version} 已初始化，ID：{ModInfo.Id}");
        try
        {
            var config = MarketConfigLoader.Load(LoggerInstance);
            _marketHarmony = new HarmonyLib.Harmony(ModInfo.Id);
            _market = new GameBridge(LoggerInstance, config);
            _panel = new NativeStockMarketPanel(_market);
            _market.IsPanelVisible = () => _panel?.IsVisible == true;
            _market.OpenPanelRequested += HandleOpenPanel;
            _market.Initialize(_marketHarmony);
            LoggerInstance.Msg("股市功能已初始化；进入存档后请检查 [股市交易] 与 [股市探针]，交易权限按本机配置生效。");
        }
        catch (Exception ex)
        {
            LoggerInstance.Error("股市初始化失败，已撤销本模组钩子：" + ex);
            DisposeMarket();
        }
    }

    public override void OnDeinitializeMelon()
    {
        if (!_subscribed)
            return;

        UnsubscribeHooks();
        DisposeMarket();
        _sceneHooks?.LogSummary();
        _sceneHooks = null;
        LoggerInstance.Msg("模组已关闭，场景钩子已解绑。");
    }

    public override void OnUpdate()
    {
        try { _market?.Tick(); _panel?.Tick(); }
        catch (Exception ex) { LogRuntimeError(ex); }
    }

    public override void OnGUI()
    {
        try
        {
            var input = UnityEngine.Event.current;
            if (_market != null && input != null && input.type == UnityEngine.EventType.KeyDown
                && input.keyCode == UnityEngine.KeyCode.F8 && input.control)
            {
                // 仅补领道具，保留通过背包双击打开股市的主要交互。
                LoggerInstance.Msg("[股市道具] 手动补领：" + _market.RecoverTerminal());
                input.Use();
            }
        }
        catch (Exception ex)
        {
            _panel?.Hide();
            LogRuntimeError(ex);
        }
    }

    private void HandleOpenPanel() => _panel?.Show();

    private void DisposeMarket()
    {
        if (_market != null) _market.OpenPanelRequested -= HandleOpenPanel;
        _panel?.Dispose();
        _panel = null;
        _market?.Dispose();
        _market = null;
        // 只清理本模组拥有的补丁，不影响其他 MelonLoader 模组。
        _marketHarmony?.UnpatchSelf();
        _marketHarmony = null;
    }

    private void LogRuntimeError(Exception ex)
    {
        if ((DateTime.UtcNow - _lastError).TotalSeconds < 10) return;
        _lastError = DateTime.UtcNow;
        LoggerInstance.Error("股市运行异常：" + ex);
    }

    private void SubscribeHooks()
    {
        if (_subscribed)
            return;

        // 只订阅具名处理函数，不再覆写同名场景回调，避免重复执行功能。
        MelonEvents.OnSceneWasInitialized.Subscribe(HandleSceneInitialized);
        try
        {
            MelonEvents.OnSceneWasUnloaded.Subscribe(HandleSceneUnloaded);
            _subscribed = true;
        }
        catch
        {
            // 第二个订阅失败时撤销第一个，避免留下部分注册的钩子。
            MelonEvents.OnSceneWasInitialized.Unsubscribe(HandleSceneInitialized);
            throw;
        }
    }

    private void UnsubscribeHooks()
    {
        if (!_subscribed)
            return;

        MelonEvents.OnSceneWasInitialized.Unsubscribe(HandleSceneInitialized);
        MelonEvents.OnSceneWasUnloaded.Unsubscribe(HandleSceneUnloaded);
        _subscribed = false;
    }

    private void HandleSceneInitialized(int buildIndex, string sceneName)
    {
        _sceneHooks?.OnSceneInitialized(buildIndex, sceneName);
        try { _market?.OnSceneChanged(sceneName); }
        catch (Exception ex) { LogRuntimeError(ex); }
    }

    private void HandleSceneUnloaded(int buildIndex, string sceneName)
    {
        _panel?.Hide();
        _sceneHooks?.OnSceneUnloaded(buildIndex, sceneName);
    }
}
