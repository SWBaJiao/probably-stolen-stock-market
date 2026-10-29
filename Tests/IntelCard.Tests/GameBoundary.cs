using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

// 宿主替身只提供原生物品、背包及保存边界，发放与恢复始终执行生产适配器源码。
public sealed class GameGridInventory
{
    public int widthPixels = 40, heightPixels = 40;
    public bool HasSpace = true;
    public readonly List<GameItem> childItems = new();
    public bool MayHaveValidInventorySlot(GameItem item) => HasSpace;
    public object? TryFindOneValidInventorySlot(GameItem item) => HasSpace ? new object() : null;
}

public sealed class GameItem
{
    public string identifier = "handnote", Name = "";
    public int uniqueId, unitValue, Count;
    public GameGridInventory? parentInventory;
    public bool Destroyed;
    private readonly Dictionary<string, TagState> _tags = new();
    public void SetName(string value) => Name = value;
    public void SetUnitCount(int value) => Count = value;
    public void ModifyTag(string key, Action<TagState> write)
    {
        if (!_tags.TryGetValue(key, out var state)) _tags[key] = state = new TagState();
        write(state);
    }
    public TagState? GetTagReadonly(string key) => _tags.GetValueOrDefault(key);
    public void Destroy()
    {
        if (parentInventory != null) throw new InvalidOperationException("不能销毁容器中的测试物品。");
        Destroyed = true;
    }
}

public sealed class TagState
{
    private string _value = "";
    public TagState Enable() => this;
    public TagState SetString(string value) { _value = value; return this; }
    public string GetString() => _value;
}

public sealed class InventoryWindow { public GameGridInventory? childElement; }
public sealed class EmporiumEntry
{
    public static EmporiumEntry? Instance;
    public GameGridInventory invElement = new();
    public InventoryWindow invWindow = new();
    public readonly List<GameItem> OtherItems = new();
    public bool CompleteQueries = true;
    public EmporiumEntry() => invWindow.childElement = invElement;
    public List<GameItem> GetAllItems() => CompleteQueries ? invElement.childItems.Concat(OtherItems).ToList() : new List<GameItem>();
    public List<GameItem> GetAllOwnedItems() => invElement.childItems.Concat(OtherItems).ToList();
}

public static class DirectoryMaster
{
    public static readonly List<GameItem> Created = new();
    public static GameItem Item(string identifier, bool created = true)
    {
        var item = new GameItem { identifier = identifier, uniqueId = Created.Count + 100 };
        Created.Add(item);
        return item;
    }
}

public static class GraphUtils
{
    public static int AcceptCalls;
    public static Action<GameGridInventory, GameItem>? OverrideAccept;
    public static void TryAcceptAllMid(GameGridInventory target, GameItem item)
    {
        AcceptCalls++;
        if (OverrideAccept != null) { OverrideAccept(target, item); return; }
        target.childItems.Add(item); item.parentInventory = target;
    }
}

namespace ProbablyStolenStockMarket.Market { public sealed class MarketEngine { } }
namespace ProbablyStolenStockMarket
{
    internal sealed class ModLogger
    {
        internal readonly List<string> Messages = new();
        internal void Msg(string value) => Messages.Add(value);
        internal void Warning(string value) => Messages.Add(value);
    }
}

namespace ProbablyStolenStockMarket.Game
{
    internal sealed class GameBridge
    {
        internal AdapterSave Save = new();
        internal bool BrokerReady = true, HasTerminal = true;
        internal string BrokerSaveIdentity = "save-A";
        internal Market.MarketEngine? Engine = new();
        internal int PersistCalls, FailPersistCall = -1;
        internal string Snapshot = "";
        internal bool IntelCardDelivered { get => Save.IntelCardDelivered; set => Save.IntelCardDelivered = value; }
        internal int IntelCardItemId { get => Save.IntelCardItemId; set => Save.IntelCardItemId = value; }
        internal bool IntelCardUncertain { get => Save.IntelCardUncertain; set => Save.IntelCardUncertain = value; }
        internal bool OwnsStockTerminal() => HasTerminal;
        internal void PersistBrokerState()
        {
            PersistCalls++;
            if (PersistCalls == FailPersistCall) throw new IOException("模拟名片保存失败。");
            Snapshot = JsonConvert.SerializeObject(Save);
        }
        internal void Reload() => Save = JsonConvert.DeserializeObject<AdapterSave>(Snapshot)!;
    }
    internal static class NativeGameAccess
    {
        internal static long Milliseconds;
        internal static string HashPrefix(string value, int length) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..length];
    }
    internal sealed class NativeReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly NativeReferenceComparer Instance = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
    internal sealed class BusinessCardVisual : IDisposable
    {
        internal BusinessCardVisual(ModLogger log, string atlas, string legacy, string sprite, string file, string label) { }
        internal void Apply(GameItem item) { }
        internal void RestoreForSave() { }
        internal void ResumeAfterSave() { }
        internal void ResetTracking() { }
        public void Dispose() { }
    }
}
