using System.Reflection;

namespace HarmonyLib
{
    public sealed class HarmonyMethod(MethodInfo method) { public MethodInfo Method { get; } = method; }
    public sealed class Harmony
    {
        public void Patch(MethodBase original, HarmonyMethod? prefix, HarmonyMethod? postfix) { }
        public void Unpatch(MethodBase original, MethodInfo patch) { }
    }
    public static class AccessTools
    {
        public static MethodInfo? Method(Type type, string name) => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
    }
}
namespace ProbablyStolenStockMarket.Localization
{
    public static class Localizer
    {
        public static int Revision { get; set; }
        public static string Text(string text) => text;
    }
}
namespace ProbablyStolenStockMarket
{
    public sealed class ModLogger
    {
        public List<string> Warnings { get; } = new();
        public void Msg(string message) { }
        public void Warning(string message) => Warnings.Add(message);
    }
}
namespace ProbablyStolenStockMarket.Game
{
    public sealed class MarketState { public int LastDay = 1; }
    public sealed class MarketEngine { public MarketState State { get; } = new(); }
    public sealed class GameBridge
    {
        internal readonly TerminalAccountState Account = new();
        internal bool BrokerReady = true;
        internal string BrokerSaveIdentity = "slot-a";
        internal MarketEngine? Engine = new();
        internal PlayerStore? CurrentStore;
        internal StoreClient? CurrentClient;
        internal TerminalAccountNpcAdapter? Adapter;
        internal bool OtherInputBlocked { get; set; }
        internal int Cash = 100, OpenCalls, PostponeCalls, Saves;
        internal GameBridge() { CurrentStore = new PlayerStore(this); }
        internal bool TerminalAccountVisitDue => BrokerReady && new TerminalAccountService(Account).IsVisitDue(Engine!.State.LastDay);
        internal bool TerminalAccountOpened => Account.Opened;
        internal bool BlocksGameInput => OtherInputBlocked || Adapter?.BlocksGameInput == true;
        internal string OpenTerminalAccount()
        {
            OpenCalls++;
            if (BlocksGameInput) return "操作繁忙";
            return new TerminalAccountService(Account).Open(Engine!.State.LastDay, () => Cash, d => Cash += d, () => Saves++);
        }
        internal void PostponeTerminalAccount()
        {
            PostponeCalls++;
            new TerminalAccountService(Account).Postpone(Engine!.State.LastDay, () => Saves++);
        }
    }
    public static class NativeGameAccess
    {
        public static List<SpriteDict.SpriteEntry> GetBrokerPortraits(SpriteDict dict) => dict.Portraits;
        public static bool OwnsConfirmation(GenericConfirmationUIManager ui, Action? confirm, Action? cancel)
            => ReferenceEquals(ui.Confirm, confirm) && ReferenceEquals(ui.Cancel, cancel);
        public static void ClearConfirmation(GenericConfirmationUIManager ui) { ui.Confirm = null; ui.Cancel = null; }
    }
}

public sealed class StoreClientManager
{
    public List<StoreClient> clientStack = new();
    public void AddNextClient(StoreClient client) => clientStack.Insert(0, client);
    public void GenerateClient() { }
    public void TrackClient(StoreClient client) { }
}
public static class StoreClientListDict
{
    public static Dictionary<string, Func<StoreClient>> storeClientDict = new();
    public static StoreClient CreateStoreClient(string id) => storeClientDict[id]();
}
public static class StoreClientFactionSetup { public static void InitBlackMarket(StoreClient client) { } }
public sealed class StoreClient
{
    public enum ClientIntent { INFORMATION_DEALER }
    public string identifier = "", displayName = "", spriteName = "";
    public ClientIntent clientIntent;
    public int serviceCost;
    public bool dismissable, arrestable, nonDismissableArrestable, nonShootable, isRegularSellDisable, isMultiBuyDisabled;
    public bool CanReturn, CanReturnAsBuyer, isMainDialogueStarted, isIntroduced, BusinessCompleted;
    public readonly List<string> clientBuyingIdList = new(), clientBuyingTagList = new();
    public Action? onDealAccepted, onItemBought, onItemSold, OnArrival, OnDismiss;
    public Dialogue mainDialogue = new(), repeatDialogue = new(), allDoneDialogue = new();
    public Dialogue? acceptDealDialogue, acceptDealDialogueLastLine;
    public void CompleteClientCreation(bool ignored) { serviceCost = 900; }
    public void CompleteBusiness() => BusinessCompleted = true;
    public void OnIntroduced() { }
    public bool IsClientBuyingThisItem() => true;
}
public sealed class Dialogue
{
    public string Speaker = "", Body = "";
    public Action? startAction, endAction;
    public Dialogue SetText(string speaker, string text) { Speaker = speaker; Body = text; return this; }
}
public sealed class SpriteDict
{
    public sealed class SpriteEntry { public string key = "base-portrait"; public object sprite = new(); }
    public static SpriteDict Instance = new();
    public List<SpriteEntry> Portraits = new() { new() };
}
public sealed class PlayerStore(ProbablyStolenStockMarket.Game.GameBridge host)
{
    public enum StoreState { MORNING, OPEN, CLOSED }
    public StoreState storeState = StoreState.OPEN;
    public StoreClientManager storeClientManager = new();
    public void DismissCurrentClient()
    { var client = host.CurrentClient; client?.OnDismiss?.Invoke(); host.CurrentClient = null; }
}
public static class StoreStation { public static int Day = 1; public static int GetDayCounter() => Day; }
public sealed class NegociationUIManager
{
    public void OpenUI() { }
    public void OnOfferAcceptClick() { }
    public void OnBuyAllClick() { }
    public void OnSellAllClick() { }
    public void OnWholesaleClick() { }
}
public sealed class FakePanel
{
    public bool activeSelf;
    public bool activeInHierarchy => activeSelf;
}
public sealed class GenericConfirmationUIManager
{
    public static GenericConfirmationUIManager Instance = new();
    public FakePanel panel = new();
    public string Body = "", ConfirmText = "", CancelText = "";
    public Action? Confirm, Cancel;
    public void OpenPanel(string body, string confirmText, string cancelText, Action? confirm, Action? cancel)
    { Body = body; ConfirmText = confirmText; CancelText = cancelText; Confirm = confirm; Cancel = cancel; panel.activeSelf = true; }
    public void ClosePanel() => panel.activeSelf = false;
    public void ConfirmButtonPressed() { Confirm?.Invoke(); ClosePanel(); }
    public void CancelButtonPressed() { Cancel?.Invoke(); ClosePanel(); }
}
public sealed class DialogUIManager
{
    public static DialogUIManager Instance = new();
    public Dialogue? Current;
    public void InterruptDialog() { }
    public void StartDialogue(Dialogue dialogue, bool force) => Current = dialogue;
}
