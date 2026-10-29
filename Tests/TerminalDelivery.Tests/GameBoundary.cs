// 游戏边界替身只提供物品、容器和保存接口，发放流程使用生产源码。
public sealed class GameGridInventory
{
    public string identifier = "inventory";
    public int widthPixels = 40, heightPixels = 40;
    public bool HasSpace = true;
    public readonly List<GameItem> childItems = new();
    public bool MayHaveValidInventorySlot(GameItem item) => HasSpace;
    public object? TryFindOneValidInventorySlot(GameItem item) => HasSpace ? new object() : null;
}

public class GameItem
{
    public string identifier = "postit", Name = "";
    public int uniqueId, Count;
    public GameGridInventory? parentInventory;
    public bool Owned;
    public void SetName(string value) => Name = value;
    public string GetDisplayName(bool canonical) => Name;
    public void SetUnitCount(int value) => Count = value;
    public virtual void Destroy() { }
}
public sealed class GameItemElement : GameItem
{
    public bool Destroyed, ThrowOnDestroy;
    public bool IsDestroyed() => Destroyed;
    public override void Destroy()
    {
        if (ThrowOnDestroy) throw new IOException("模拟清理失败。");
        Destroyed = true;
        parentInventory?.childItems.Remove(this); parentInventory = null;
    }
}
public sealed class InventoryWindow { public GameGridInventory? childElement; }
public sealed class EmporiumEntry
{
    public static EmporiumEntry? Instance;
    public GameGridInventory invElement = new(), backInvinvElement = new(), frontInvinvElement = new();
    public InventoryWindow invWindow = new(), backInvWindow = new(), frontInvWindow = new();
    public readonly List<GameItem> OtherItems = new();
    public bool CompleteQueries = true;
    public EmporiumEntry()
    {
        invWindow.childElement = invElement; backInvWindow.childElement = backInvinvElement;
        frontInvWindow.childElement = frontInvinvElement;
    }
    private IEnumerable<GameItem> All() => invElement.childItems.Concat(backInvinvElement.childItems)
        .Concat(frontInvinvElement.childItems).Concat(OtherItems);
    public List<GameItem> GetAllItems() => CompleteQueries ? All().ToList() : new();
    public List<GameItem> GetAllOwnedItems() => All().Where(item => item.Owned).ToList();
}
public static class GeneralHelper { public static void SetItemOwned(GameItem item, bool owned) => item.Owned = owned; }
public static class DirectoryMaster
{
    public static readonly List<GameItemElement> Created = new();
    public static Action<GameItemElement>? ConfigureCreated;
    public static GameItem Item(string identifier, bool created = true)
    {
        var item = new GameItemElement { identifier = identifier, uniqueId = Created.Count + 100 };
        Created.Add(item); ConfigureCreated?.Invoke(item); return item;
    }
}
public static class GraphUtils
{
    public static int AcceptCalls;
    public static Action<GameGridInventory, GameItem>? OverrideAccept;
    public static void Place(GameGridInventory target, GameItem item)
    { target.childItems.Add(item); item.parentInventory = target; }
    public static void TryAcceptAllMid(GameGridInventory target, GameItem item)
    {
        AcceptCalls++;
        if (OverrideAccept != null) OverrideAccept(target, item); else Place(target, item);
    }
}
public sealed class StoreUIManager
{
    public static StoreUIManager? Instance = new();
    public string Message = "";
    public void Notify(string message) => Message = message;
}
public sealed class ItemMouseDragHandler
{ public static ItemMouseDragHandler? current = new(); public bool IsDraggingItem; }
public sealed class ItemMultiSelectHandler
{ public static ItemMultiSelectHandler? current = new(); public bool IsGroupDragging; }
public sealed class ItemSelectHandler
{ public static ItemSelectHandler? current = new(); public bool IsEquipped; }
namespace ProbablyStolenStockMarket.Localization
{ internal static class Localizer { internal static string Text(string value) => value; } }
namespace ProbablyStolenStockMarket
{
    internal sealed class ModLogger
    {
        internal readonly List<string> Messages = new();
        internal void Msg(string value) => Messages.Add(value);
        internal void Warning(string value) => Messages.Add(value);
        internal void Error(string value) => Messages.Add(value);
    }
}
namespace ProbablyStolenStockMarket.Market
{
    public sealed class MarketEngine { public State State = new(); }
    public sealed class State { public bool TerminalGranted; }
}
namespace ProbablyStolenStockMarket.Game
{
    public sealed partial class GameBridge
    {
        private const string TerminalName = "股市面板";
        private object? _store = new();
        private bool _loading = false, _inMenu = false;
        private AdapterSave _save = new();
        private TerminalGrantPolicy _terminalGrant = new();
        private GameItem? _trackedTerminal;
        private readonly TerminalVisual _terminalVisual = new();
        private string _lastInventoryProbe = "";
        private readonly ModLogger _log = new();
        public Market.MarketEngine? Engine = new();
        public event Action? OpenPanelRequested;
        public bool BlocksGameInput;
        internal AdapterSave Save => _save;
        internal int PersistCalls, FailPersistCall = -1;
        internal Action? BeforePersist;
        internal bool Current = true;
        private bool IsCurrentStore() => Current;
        private void Persist()
        {
            PersistCalls++; BeforePersist?.Invoke();
            if (PersistCalls == FailPersistCall) throw new IOException("模拟终端保存失败。");
        }
        internal string Poll() => EnsureTerminal(false);
        internal void NewSession() { _terminalGrant = new(); _trackedTerminal = null; }
    }
    internal sealed class TerminalVisual { internal void Apply(GameItem item) { } }
    internal sealed class NativeReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly NativeReferenceComparer Instance = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}
