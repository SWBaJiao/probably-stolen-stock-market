using System;
using ProbablyStolenStockMarket.Configuration;
using ProbablyStolenStockMarket.Features;
using ProbablyStolenStockMarket.Game;
using ProbablyStolenStockMarket.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProbablyStolenStockMarket;

public sealed class ModEntry : IMod
{
    private readonly ModLogger _logger = new();
    private SceneHooks? _sceneHooks;
    private GameBridge? _market;
    private NativeStockMarketPanel? _panel;
    private HarmonyLib.Harmony? _marketHarmony;
    private ModRuntime? _runtime;
    private bool _enabled;
    private DateTime _lastError;

    public void Init(ModManifest manifest)
    {
        if (manifest == null || !string.Equals(manifest.ID, ModInfo.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("manifest.xml 中的 ID 必须与股市模组 ID 一致。");
        _logger.Msg($"{manifest.Name} {manifest.ModVersion} 原生模组已注册。");
    }

    public void OnEnable()
    {
        if (_enabled) return;
        _enabled = true;
        SceneManager.sceneLoaded += HandleSceneLoaded;
        SceneManager.sceneUnloaded += HandleSceneUnloaded;
        try
        {
            var root = new GameObject(ModInfo.Id + ".runtime");
            UnityEngine.Object.DontDestroyOnLoad(root);
            _runtime = root.AddComponent<ModRuntime>();
            _runtime.Owner = this;
        }
        catch { OnDisable(); throw; }
    }

    internal void StartRuntime()
    {
        if (!_enabled) return;
        try
        {
            // 加载器会在 OnEnable 返回后登记模组目录；下一帧再读取资源和配置。
            _sceneHooks = new SceneHooks(_logger);
            var config = MarketConfigLoader.Load(_logger);
            _marketHarmony = new HarmonyLib.Harmony("mod." + ModInfo.Id + ".runtime");
            _market = new GameBridge(_logger, config);
            _panel = new NativeStockMarketPanel(_market);
            _market.IsPanelVisible = () => _panel?.IsVisible == true;
            _market.OpenPanelRequested += HandleOpenPanel;
            _market.ClosePanelRequested += HandleClosePanel;
            _market.Initialize(_marketHarmony);
            HandleSceneLoaded(SceneManager.GetActiveScene(), LoadSceneMode.Single);
            _logger.Msg("股市和贷款已接入正式版原生加载器；投资记录随原版存档保存。");
        }
        catch (Exception ex)
        {
            _logger.Error("股市初始化失败，已撤销本模组钩子：" + ex);
            DisposeMarket();
            var loaded = ModLoader.Mods.Find(m => m.Manifest.ID == ModInfo.Id);
            if (loaded != null) loaded.LoadError = "股市初始化失败：" + ex.Message;
        }
    }

    public void OnDisable()
    {
        _enabled = false;
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneUnloaded -= HandleSceneUnloaded;
        if (_runtime != null)
        {
            _runtime.Owner = null;
            UnityEngine.Object.Destroy(_runtime.gameObject);
            _runtime = null;
        }
        DisposeMarket();
        _sceneHooks?.LogSummary();
        _sceneHooks = null;
    }

    internal void Tick()
    {
        if (!_enabled) return;
        try { GameLanguageAdapter.Update(); _market?.Tick(); _panel?.Tick(); }
        catch (Exception ex) { LogRuntimeError(ex); }
    }

    internal void DrawInput()
    {
        if (!_enabled) return;
        try
        {
            var input = Event.current;
            if (_market != null && input != null && input.type == EventType.KeyDown
                && input.keyCode == KeyCode.F8 && input.control)
            {
                // 仅补领道具，保留通过背包双击打开股市的主要交互。
                _logger.Msg("[股市道具] 手动补领：" + _market.RecoverTerminal());
                input.Use();
            }
        }
        catch (Exception ex) { _panel?.Hide(); LogRuntimeError(ex); }
    }

    private void HandleOpenPanel() => _panel?.Show();
    private void HandleClosePanel() => _panel?.Hide();

    private void DisposeMarket()
    {
        if (_market != null) _market.OpenPanelRequested -= HandleOpenPanel;
        if (_market != null) _market.ClosePanelRequested -= HandleClosePanel;
        try { _panel?.Dispose(); }
        finally
        {
            _panel = null;
            try { _market?.Dispose(); }
            finally
            {
                _market = null;
                // 只撤销本模组手工安装的补丁，加载器管理自己的自动补丁。
                if (_marketHarmony != null) _marketHarmony.UnpatchAll(_marketHarmony.Id);
                _marketHarmony = null;
            }
        }
    }

    private void LogRuntimeError(Exception ex)
    {
        if ((DateTime.UtcNow - _lastError).TotalSeconds < 10) return;
        _lastError = DateTime.UtcNow;
        _logger.Error("股市运行异常：" + ex);
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        _sceneHooks?.OnSceneInitialized(scene.buildIndex, scene.name);
        try { _market?.OnSceneChanged(scene.name); }
        catch (Exception ex) { LogRuntimeError(ex); }
    }

    private void HandleSceneUnloaded(Scene scene)
    {
        _panel?.Hide();
        _sceneHooks?.OnSceneUnloaded(scene.buildIndex, scene.name);
    }
}

// 使用普通 MonoBehaviour 驱动每帧逻辑，随模组禁用一起销毁。
public sealed class ModRuntime : MonoBehaviour
{
    internal ModEntry? Owner;
    private void Start() => Owner?.StartRuntime();
    private void Update() => Owner?.Tick();
    private void OnGUI() => Owner?.DrawInput();
}
