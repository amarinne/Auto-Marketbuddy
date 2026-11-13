using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoMarketbuddy.Services;

public sealed unsafe class GameRetainerMarket : IRetainerMarket, IDisposable
{
    private sealed record Packet(int RequestId, (ulong Id, MarketListing Listing)[] Listings);
    private sealed record HistoryPacket(uint ItemId, HistorySale[] Sales);
    private readonly IGameGui gui;
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly IObjectTable objects;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IMarketBoard board;
    private readonly IFramework framework;
    private readonly IDataManager data;
    private readonly ConcurrentQueue<Packet> packets = new();
    private readonly ConcurrentQueue<HistoryPacket> historyPackets = new();
    // Marketbuddy exposes this lock so other plugins can drive the retainer
    // windows without its auto-open, paste, and click-to-set handlers firing.
    private const string MarketbuddyLockToken = "AutoMarketbuddy";
    private readonly ICallGateSubscriber<string, bool> marketbuddyLock;
    private readonly ICallGateSubscriber<string, bool> marketbuddyUnlock;
    private readonly AutoRetainerSuppression autoRetainer;
    private bool marketbuddyHeld;
    private HashSet<ulong> ownRetainers = new();
    private SaleItem? ownedItem;
    private nint ownedEditor;
    private ushort listId;
    private nint listAddress;
    private ComparisonCapture? comparison;
    private bool comparing;
    private ushort editorId;
    private nint resultAddress;
    private ushort resultId;
    private string sellMenuText = "";

    public GameRetainerMarket(IGameGui gui, IClientState client, IPlayerState player,
        IObjectTable objects, IDalamudPluginInterface pluginInterface, IMarketBoard board, IFramework framework,
        IDataManager dataManager)
    {
        this.gui = gui;
        this.client = client;
        this.player = player;
        this.objects = objects;
        this.pluginInterface = pluginInterface;
        this.board = board;
        this.framework = framework;
        data = dataManager;
        marketbuddyLock = pluginInterface.GetIpcSubscriber<string, bool>("Marketbuddy.Lock");
        marketbuddyUnlock = pluginInterface.GetIpcSubscriber<string, bool>("Marketbuddy.Unlock");
        var autoRetainerGetSuppressed = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed");
        var autoRetainerSetSuppressed = pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed");
        autoRetainer = new(
            () => pluginInterface.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName == "AutoRetainer"),
            autoRetainerGetSuppressed.InvokeFunc, autoRetainerSetSuppressed.InvokeAction);
        board.OfferingsReceived += OnOfferings;
        board.HistoryReceived += OnHistory;
    }

    public void BeginRun()
    {
        // Older builds without the suppression IPC cannot be paused. The session
        // still stops if a retainer window or listing changes.
        try { autoRetainer.Acquire(); }
        catch (Exception) { /* AutoRetainer is loaded without its suppression IPC. */ }

        if (marketbuddyHeld) return;
        var loaded = pluginInterface.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName == "Marketbuddy");
        try { marketbuddyLock.InvokeFunc(MarketbuddyLockToken); }
        catch (Exception)
        {
            if (loaded)
                throw new InvalidOperationException(
                    "Marketbuddy is loaded but its interop lock is unavailable. Stopped before touching prices.");
            return; // Marketbuddy absent: nothing to lock, run proceeds normally.
        }
        marketbuddyHeld = true;
    }

    public void EndRun()
    {
        try { autoRetainer.Release(); }
        catch (Exception) { /* AutoRetainer was unloaded mid-run; its suppression state is gone with it. */ }

        if (!marketbuddyHeld) return;
        marketbuddyHeld = false;
        try { marketbuddyUnlock.InvokeFunc(MarketbuddyLockToken); }
        catch (Exception) { /* Marketbuddy was unloaded mid-run; its lock set is gone with it. */ }
    }

    private int exitStage;
    private long exitDeadline;

    public void BeginRetainerExit() => exitStage = 1;
    public void CancelRetainerExit() => exitStage = 0;

    // Close the sell list, then the retainer menu that reappears under it.
    // Stops at the retainer selection list so the next retainer can be picked.
    public void UpdateRetainerExit(long now)
    {
        if (exitStage == 0) return;
        if (exitStage == 1)
        {
            var list = Addon("RetainerSellList");
            if (Ready(list)) { list->Close(true); exitStage = 2; exitDeadline = now + 5_000; return; }
            exitStage = 2;
            exitDeadline = now + 5_000;
        }
        if (exitStage == 2)
        {
            var menu = Addon("SelectString");
            if (Ready(menu)) { menu->Close(true); exitStage = 0; return; }
            if (now >= exitDeadline) exitStage = 0;
        }
    }

    public IReadOnlySet<ulong> OwnRetainers => ownRetainers;
    private AtkUnitBase* Addon(string name) => (AtkUnitBase*)gui.GetAddonByName(name).Address;
    private AddonRetainerSell* Editor => (AddonRetainerSell*)Addon("RetainerSell");
    private static bool Ready(AtkUnitBase* addon) => addon != null && addon->IsVisible && addon->IsReady;
    public bool ListVisible => Ready(Addon("RetainerSellList"));
    public bool ListReady
    {
        get
        {
            if (!ListVisible) return false;
            var manager = InventoryManager.Instance();
            var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
            return container != null && container->IsLoaded && container->Size == 20;
        }
    }
    public bool EditorClosed => !Ready((AtkUnitBase*)Editor) && !Ready(Addon("ItemSearchResult"));
    public bool SellListClosed => !Ready(Addon("RetainerSellList"));

    public bool HasListings
    {
        get
        {
            var inventory = Container();
            for (short slot = 0; slot < inventory->Size; slot++)
                if (inventory->GetInventorySlot(slot)->ItemId != 0) return true;
            return false;
        }
    }

    public string SelectedRetainerName
    {
        get
        {
            var manager = RetainerManager.Instance();
            if (manager == null || !manager->IsReady || manager->LastSelectedRetainerId == 0) return "";
            foreach (var retainer in manager->Retainers)
                if (retainer.RetainerId == manager->LastSelectedRetainerId) return retainer.NameString;
            return "";
        }
    }

    // AutoRetainer fires its post-process callback at the retainer SelectString menu. Match the
    // game's own "Sell items..." row instead of a menu index so reordering cannot select the
    // retrieve or entrust entry.
    private string SellMenuText
    {
        get
        {
            if (sellMenuText.Length == 0)
                sellMenuText = data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(2380)?.Text.ToString() ?? "";
            return sellMenuText;
        }
    }

    private int SellMenuIndex(AddonSelectString* addon)
    {
        var expected = SellMenuText;
        var count = addon->PopupMenu.PopupMenu.EntryCount;
        if (expected.Length == 0 || count <= 0 || count > 32 ||
            addon->PopupMenu.PopupMenu.EntryNames == null) return -1;
        for (var i = 0; i < count; i++)
        {
            var pointer = addon->PopupMenu.PopupMenu.EntryNames[i].Value;
            var text = pointer == null ? null : Marshal.PtrToStringUTF8((nint)pointer);
            if (text != null && text.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public bool RetainerMenuReady
    {
        get
        {
            var addon = (AddonSelectString*)Addon("SelectString");
            return Ready((AtkUnitBase*)addon) && SellMenuIndex(addon) >= 0;
        }
    }

    public void OpenSellList()
    {
        if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            throw new InvalidOperationException("Game controls require an active framework thread.");
        var addon = (AddonSelectString*)Addon("SelectString");
        if (!Ready((AtkUnitBase*)addon))
            throw new InvalidOperationException("The retainer menu is not ready.");
        var index = SellMenuIndex(addon);
        if (index < 0)
            throw new InvalidOperationException("The retainer menu has no sale-list entry.");
        AtkValue value = default;
        value.Type = AtkValueType.Int;
        value.Int = index;
        addon->FireCallback(1, &value, true);
    }

    public void CloseSellList()
    {
        // Unload can run after the framework is gone. Leave the addon alone in that case.
        if (framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread) return;
        var list = Addon("RetainerSellList");
        if (Ready(list)) list->Close(true);
    }

    public IReadOnlyList<SaleItem> Capture()
    {
        if (!ListVisible || !EditorClosed || Ready(Addon("ContextMenu")) || Ready(Addon("ItemSearch")))
            throw new InvalidOperationException("Open the retainer sale list and close other market windows first.");
        var list = Addon("RetainerSellList");
        listId = list->Id;
        listAddress = (nint)list;
        ownRetainers = new();
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            throw new InvalidOperationException("Retainer data is not ready.");
        foreach (var retainer in manager->Retainers)
            if (retainer.RetainerId != 0) ownRetainers.Add(retainer.RetainerId);
        var items = new List<SaleItem>();
        var inventory = Container();
        for (short slot = 0; slot < inventory->Size; slot++)
            if (inventory->GetInventorySlot(slot)->ItemId != 0) items.Add(ReadItem(slot));
        return items;
    }

    // The sale list shows its own order, which is not inventory slot order. AgentRetainer keeps
    // the displayed entries with their inventory slots, top row first.
    public IReadOnlyList<short> SaleListSlots
    {
        get
        {
            var agent = AgentRetainer.Instance();
            if (agent == null) return Array.Empty<short>();
            var entries = agent->SellListEntries;
            var count = Math.Min(agent->SellListEntryCount, entries.Length);
            if (count <= 0) return Array.Empty<short>();
            var slots = new List<short>(count);
            for (var index = 0; index < count; index++)
                slots.Add((short)entries[index].InventorySlot);
            return slots;
        }
    }

    // A read-only view for manual edits and other plugins. It tolerates a list that changes
    // between checks and never takes ownership of the editor.
    public IReadOnlyList<SaleItem> Snapshot()
    {
        if (!ListVisible) return Array.Empty<SaleItem>();
        try
        {
            var items = new List<SaleItem>();
            var inventory = Container();
            for (short slot = 0; slot < inventory->Size; slot++)
                if (inventory->GetInventorySlot(slot)->ItemId != 0) items.Add(ReadItem(slot));
            return SaleListOrder.Sort(items, SaleListSlots);
        }
        catch (InvalidOperationException) { return Array.Empty<SaleItem>(); }
    }

    public void ValidateSession(SaleItem item)
    {
        if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            throw new InvalidOperationException("Game controls require an active framework thread.");
        var list = Addon("RetainerSellList");
        var manager = RetainerManager.Instance();
        var agent = AgentRetainer.Instance();
        if (!client.IsLoggedIn || !player.IsLoaded || objects.LocalPlayer == null ||
            player.ContentId != item.CharacterId || objects.LocalPlayer.CurrentWorld.RowId != item.WorldId ||
            !Ready(list) || (nint)list != listAddress || list->Id != listId || agent == null ||
            agent->RetainerSellListAddonId != listId || manager == null || !manager->IsReady ||
            manager->LastSelectedRetainerId != item.RetainerId)
            throw new InvalidOperationException("Character, world, retainer, or sale list changed. Stopped.");
    }

    private static InventoryContainer* Container()
    {
        var manager = InventoryManager.Instance();
        var container = manager == null ? null : manager->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null || !container->IsLoaded || container->Size != 20)
            throw new InvalidOperationException("Retainer market inventory is not ready.");
        return container;
    }

    public SaleItem ReadItem(short slot)
    {
        var container = Container();
        var manager = RetainerManager.Instance();
        if (slot < 0 || slot >= container->Size || !client.IsLoggedIn || !player.IsLoaded ||
            objects.LocalPlayer == null || manager == null || manager->LastSelectedRetainerId == 0)
            throw new InvalidOperationException("The listing is unavailable.");
        var item = container->GetInventorySlot(slot);
        var price = InventoryManager.Instance()->GetRetainerMarketPrice(slot);
        if (item == null || item->ItemId == 0 || item->Quantity <= 0 || price > Pricing.MaximumPrice)
            throw new InvalidOperationException("The listing was removed or has an invalid price.");
        return new(player.ContentId, objects.LocalPlayer.CurrentWorld.RowId, manager->LastSelectedRetainerId,
            slot, item->ItemId, (item->Flags & InventoryItem.ItemFlags.HighQuality) != 0,
            (uint)item->Quantity, (uint)price);
    }

    public void Open(SaleItem item)
    {
        ValidateSession(item);
        BeginRun(); // Re-acquire before the editor setup can trigger Marketbuddy.
        if (!EditorClosed || Ready(Addon("ContextMenu")) || Ready(Addon("ItemSearch")))
            throw new InvalidOperationException("Another market window is open.");
        ownedItem = item;
        ownedEditor = 0;
        editorId = 0;
        // This takes an inventory slot, independent of the visual sorting order.
        AgentRetainer.Instance()->OpenRetainerSell(InventoryType.RetainerMarket, (ushort)item.Slot);
    }

    public bool EditorReady(SaleItem item)
    {
        ValidateSession(item);
        var addon = Editor;
        if (!Ready((AtkUnitBase*)addon)) return false;
        var agent = AgentRetainer.Instance();
        if (ownedItem != item || agent->SellItemInventoryType != InventoryType.RetainerMarket ||
            agent->SellItemInventorySlot != item.Slot || agent->RetainerSellAddonId != addon->Id ||
            addon->Quantity == null || addon->AskingPrice == null || addon->Confirm == null ||
            addon->Cancel == null || addon->ComparePrices == null || addon->Quantity->Value != item.Quantity)
            throw new InvalidOperationException("The price editor does not match the selected listing.");
        if (ownedEditor != 0 && (ownedEditor != (nint)addon || editorId != addon->Id))
            throw new InvalidOperationException("The price editor was replaced.");
        ownedEditor = (nint)addon;
        editorId = addon->Id;
        return true;
    }

    public void Compare(SaleItem item)
    {
        if (!EditorReady(item) || Editor->AskingPrice->Value != item.Price)
            throw new InvalidOperationException("The editor price changed before comparison.");
        if (Ready(Addon("ItemSearchResult")))
            throw new InvalidOperationException("Another comparison is active.");
        packets.Clear();
        historyPackets.Clear();
        comparison = new(item.ItemId);
        resultAddress = 0;
        resultId = 0;
        comparing = true;
        Click((AtkUnitBase*)Editor, Editor->ComparePrices);
    }

    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        // The event can run off the framework thread. Copy data only; never access game pointers here.
        packets.Enqueue(new(offerings.RequestId, offerings.ItemListings.Select(x =>
            (x.ListingId, new MarketListing(x.ItemId, x.IsHq, x.RetainerId, x.PricePerUnit, x.OnMannequin))).ToArray()));
        while (packets.Count > 20) packets.TryDequeue(out _);
    }

    private void OnHistory(IMarketBoardHistory history)
    {
        // Same rule as offerings: copy managed data, never touch game pointers off the framework thread.
        historyPackets.Enqueue(new(history.ItemId, history.HistoryListings.Select(x =>
            new HistorySale(history.ItemId, x.IsHq, x.SalePrice, x.OnMannequin)).ToArray()));
        while (historyPackets.Count > 20) historyPackets.TryDequeue(out _);
    }

    public IReadOnlyList<HistorySale>? ReadHistory(SaleItem item)
    {
        // Returns null until this comparison's history arrives. An empty array means the server sent none.
        IReadOnlyList<HistorySale>? matched = null;
        while (historyPackets.TryDequeue(out var packet))
            if (packet.ItemId == item.ItemId) matched = packet.Sales;
        return matched;
    }

    public IReadOnlyList<MarketListing>? ReadComparison(SaleItem item)
    {
        if (!comparing || comparison == null || !EditorReady(item)) throw new InvalidOperationException("No active comparison.");
        while (packets.TryDequeue(out var packet))
        {
            comparison.Add(packet.RequestId, packet.Listings);
        }
        var proxy = InfoProxyItemSearch.Instance();
        var result = Addon("ItemSearchResult");
        if (proxy == null) throw new InvalidOperationException("Market data is unavailable.");
        if (!Ready(result)) return null;
        if (resultAddress == 0) { resultAddress = (nint)result; resultId = result->Id; }
        if (resultAddress != (nint)result || resultId != result->Id)
            throw new InvalidOperationException("The comparison window was replaced.");
        return comparison.Complete(proxy->SearchItemId, proxy->ListingCount);
    }

    private void CloseComparison()
    {
        var result = Addon("ItemSearchResult");
        if (Ready(result))
        {
            if ((nint)result != resultAddress || result->Id != resultId)
                throw new InvalidOperationException("Cannot close an unowned comparison window.");
            result->Close(true);
        }
        comparing = false;
        packets.Clear();
        historyPackets.Clear();
        comparison = null;
    }

    public void SetPrice(SaleItem item, uint price)
    {
        if (!EditorReady(item) || Editor->AskingPrice->Value != item.Price || price is 0 or > Pricing.MaximumPrice)
            throw new InvalidOperationException("The proposed price or editor changed.");
        CloseComparison();
        Editor->AskingPrice->SetValue((int)price);
    }

    public void Confirm(SaleItem item, uint price)
    {
        if (!EditorReady(item) || ReadItem(item.Slot) != item ||
            Editor->AskingPrice->Value != price || Ready(Addon("ItemSearchResult")))
            throw new InvalidOperationException("The listing, editor, or proposed price changed before confirmation.");
        Click((AtkUnitBase*)Editor, Editor->Confirm);
    }

    private static void Click(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null || button->OwnerNode == null || !button->IsEnabled)
            throw new InvalidOperationException("The requested button is unavailable.");
        // Use the actual button registration instead of guessed node indices or callback numbers.
        var evt = button->OwnerNode->AtkResNode.AtkEventManager.Event;
        for (var count = 0; evt != null && count < 64; count++, evt = evt->NextEvent)
        {
            if (evt->State.EventType != AtkEventType.ButtonClick || evt->Listener != (AtkEventListener*)addon) continue;
            var copy = *evt;
            AtkEventData data = default;
            evt->Listener->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, &copy, &data);
            return;
        }
        throw new InvalidOperationException("The button's click registration is unsupported.");
    }

    public void CancelOwnedEditor()
    {
        // Cancel only while the original session and editor identity still match. Never submit here.
        if (ownedItem == null) return;
        try
        {
            // During process shutdown, the framework can already be gone. Release ownership without native calls.
            if (framework.IsFrameworkUnloading || !framework.IsInFrameworkUpdateThread) return;
            ValidateSession(ownedItem);
            CloseComparison();
            if (EditorReady(ownedItem)) Click((AtkUnitBase*)Editor, Editor->Cancel);
        }
        finally
        {
            ownedItem = null;
            ownedEditor = 0;
            editorId = 0;
            comparing = false;
            packets.Clear();
            comparison = null;
        }
    }

    public void Dispose()
    {
        CancelRetainerExit();
        EndRun();
        board.OfferingsReceived -= OnOfferings;
        board.HistoryReceived -= OnHistory;
        packets.Clear();
        historyPackets.Clear();
    }
}
