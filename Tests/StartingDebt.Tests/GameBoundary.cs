using ProbablyStolenStockMarket.Market;

// 仅替代 Unity 与原版宿主的外部边界，初始化顺序与回滚均执行实际适配源文件。
public sealed class PlayerStore
{
    public enum StoreState { OPEN, CLOSED }
    public static PlayerStore? Instance;
    public static bool IsInstanceExist() => Instance != null;
    public Dictionary<string, string>? modData = new();
    public NewGameData.StartType startType;
    public string startScenarioId = "";
    public int saveSlotId = -1;
    public string runID = "";
    public int runNumber;
    public int playerCash;
    public StoreState storeState = StoreState.OPEN;
    public int GetCash() => playerCash;
}

public static class NewGameData
{
    public enum StartType { CLASSIC, MODDED }
}

public static class StoreStation
{
    public static int Day;
    public static int GetDayCounter() => Day;
}

public sealed class StartingScenario
{
    public enum TagTone { Neutral, Danger }
    private static readonly Dictionary<string, StartingScenario> Entries = new();
    public string id = "", source = "";
    public bool skipIntro;
    public Func<string> name = () => "", description = () => "", tag = () => "";
    public Func<string>[] effects = Array.Empty<Func<string>>();
    public Action onStart = () => { };
    public TagTone tagTone;
    public static StartingScenario? Get(string id) => Entries.GetValueOrDefault(id);
    internal static void Add(StartingScenario scenario) => Entries[scenario.id] = scenario;
    internal static void RemoveSource(string source)
    {
        foreach (var key in Entries.Where(p => p.Value.source == source).Select(p => p.Key).ToArray()) Entries.Remove(key);
    }
}

public static class ModHelper
{
    public static void AddStartingScenario(string modId, string id, Func<string> name, Func<string> description,
        Func<string>[] effects, Action onStart, Func<string> tag, StartingScenario.TagTone tagTone, bool skipIntro)
        => StartingScenario.Add(new StartingScenario
        {
            id = id, source = modId, name = name, description = description, effects = effects,
            onStart = onStart, tag = tag, tagTone = tagTone, skipIntro = skipIntro
        });
    public static void RemoveStartingScenarios(string modId) => StartingScenario.RemoveSource(modId);
}

public sealed class FakePanel
{
    public bool activeSelf, activeInHierarchy;
}

public sealed class GenericConfirmationUIManager
{
    public static GenericConfirmationUIManager? Instance;
    public FakePanel panel = new();
    internal Action? Confirm, Cancel;
    public void OpenPanel(string body, string yes, string no, Action? confirm, Action? cancel)
    {
        Confirm = confirm; Cancel = cancel;
        panel!.activeSelf = panel.activeInHierarchy = true;
    }
    public void ClosePanel() => panel!.activeSelf = panel.activeInHierarchy = false;
}

namespace ProbablyStolenStockMarket.Game
{
    internal static class GameLanguageAdapter { internal static void Update() { } }
    internal static class NativeGameAccess
    {
        internal static bool OwnsConfirmation(GenericConfirmationUIManager ui, Action? confirm, Action? cancel)
            => ReferenceEquals(ui.Confirm, confirm) && ReferenceEquals(ui.Cancel, cancel);
        internal static void ClearConfirmation(GenericConfirmationUIManager ui) { ui.Confirm = null; ui.Cancel = null; }
    }

    public sealed partial class GameBridge : IWallet
    {
        private const string SaveKey = "probably_stolen.stock_market.v1";
        private PlayerStore? _store;
        private string _identity = "";
        private bool _loading, _monetaryOperation;
        private readonly BoundaryLog _log = new();
        public MarketEngine? Engine { get; private set; }
        public int Cash => _store?.GetCash() ?? throw new InvalidOperationException("测试宿主尚未附加存档。");
        internal bool BrokerReady => Engine != null && _store != null;
        public void ChangeCash(int delta) => _store!.playerCash = checked(_store.playerCash + delta);
        internal string Identity => _identity;
        internal string LastFault { get; private set; } = "";
        internal int PersistCalls { get; private set; }
        internal int FailPersistCall { get; set; } = -1;
        internal bool IsAttached => Engine != null && _store != null;
        internal bool MonetaryOperation => _monetaryOperation;
        internal static string MarketSaveKey => SaveKey;
        internal void PrepareSave() => PrepareStartingDebtForSave();
        internal void SetLoading(bool loading) => _loading = loading;

        private void Attach(PlayerStore store, string identity, bool actualLoad)
        {
            _store = store; _identity = identity;
            var config = MarketConfig.CreateDefault(); config.EnableStockEvents = false;
            var state = store.modData!.TryGetValue(SaveKey, out var json) ? MarketEngine.Deserialize(json, config)
                : new MarketState { LastDay = StoreStation.Day };
            Engine = new MarketEngine(config, state);
            Engine.ApplyStorageAccess(false);
            // 真实 Attach 在附加后会写入一次基线，本替身也保留该写回边界。
            Persist();
        }
        private void Persist()
        {
            PersistCalls++;
            // 写入后再注入异常，以验证实际适配代码能恢复已被覆盖的存档字符串。
            _store!.modData![SaveKey] = Engine!.Serialize();
            if (PersistCalls == FailPersistCall) throw new IOException("模拟写回失败。");
        }
        private void Fault(string message, Exception error) => LastFault = message + ": " + error.Message;
        private sealed class BoundaryLog { internal void Msg(string text) { } }
    }
}
