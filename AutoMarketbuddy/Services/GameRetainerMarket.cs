using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
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
    private readonly IAddonLifecycle addonLifecycle;
    private readonly ConcurrentQueue<Packet> packets = new();
    private readonly ConcurrentQueue<HistoryPacket> historyPackets = new();
    // The passive watcher reads this queue. A run never touches it, so observation cannot take a
    // run's packets or clear them.
    private readonly ConcurrentQueue<Packet> manualPackets = new();
    private ComparisonCapture? manualComparison;
    private long manualStartedAt;
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
    private long comparedAt;
    private long searchWindowAt;
    private int nextRequestIdAtCompare;
    private uint searchItemIdAtCompare;
    private uint listingCountAtCompare;
    private short confirmedEditSlot = -1;
    private uint confirmedEditPrice;
    private long confirmedEditAt;
    private ushort editorId;
    private nint resultAddress;
    private ushort resultId;
    // Addon sheet rows for retainer menu entries: "Sell items…" and "Quit".
    private const uint SellMenuRow = 2380;
    private const uint QuitMenuRow = 2383;
    private readonly Dictionary<uint, string> menuTexts = new();

    public GameRetainerMarket(IGameGui gui, IClientState client, IPlayerState player,
        IObjectTable objects, IDalamudPluginInterface pluginInterface, IMarketBoard board, IFramework framework,
        IDataManager dataManager, IAddonLifecycle addonLifecycle)
    {
        this.gui = gui;
        this.client = client;
        this.player = player;
        this.objects = objects;
        this.pluginInterface = pluginInterface;
        this.board = board;
        this.framework = framework;
        this.addonLifecycle = addonLifecycle;
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
        // The game reports each addon event to Dalamud. This listener only reads the confirm
        // click of the retainer price editor; it never sends an event of its own.
        addonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, "RetainerSell", OnRetainerSellEvent);
    }

    private void OnRetainerSellEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs receive) return;
        if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading) return;
        var addon = Editor;
        if (!Ready((AtkUnitBase*)addon)) return;
        // Match the registration of the Confirm button instead of a guessed callback number.
        var click = ButtonClickEvent((AtkUnitBase*)addon, addon->Confirm);
        if (click == null) return;
        if (receive.EventParam != click->Param) return;
        var agent = AgentRetainer.Instance();
        if (agent == null || agent->SellItemInventoryType != InventoryType.RetainerMarket) return;
        var slot = (short)agent->SellItemInventorySlot;
        if (slot is < 0 or >= 20) return;
        confirmedEditSlot = slot;
        confirmedEditPrice = (uint)addon->AskingPrice->Value;
        confirmedEditAt = Environment.TickCount64;
    }

    public void BeginRun()
    {
        // A run drives the editor itself. Drop any confirm seen before it so the passive watcher
        // cannot attribute the run's own change to a manual edit.
        confirmedEditAt = 0;
        confirmedEditSlot = -1;
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
    private long nextTalkClick;

    public void BeginRetainerExit() => exitStage = 1;
    public void CancelRetainerExit() => exitStage = 0;

    // Close the sale list, choose Quit in the retainer menu, and click through the retainer's
    // farewell dialog. Closing the menu addon only hid it and left the retainer called. Stops at
    // the retainer selection list so the next retainer can be picked. Each stage has a deadline.
    public void UpdateRetainerExit(long now)
    {
        if (exitStage == 0) return;
        if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
        {
            exitStage = 0;
            return;
        }
        if (exitStage == 1)
        {
            var list = Addon("RetainerSellList");
            if (Ready(list)) list->Close(true);
            exitStage = 2;
            exitDeadline = now + 5_000;
            return;
        }
        if (exitStage == 2)
        {
            var menu = (AddonSelectString*)Addon("SelectString");
            var index = Ready((AtkUnitBase*)menu) ? MenuIndex(menu, QuitMenuRow) : -1;
            if (index >= 0)
            {
                SelectMenu(menu, index);
                exitStage = 3;
                exitDeadline = now + 5_000;
                nextTalkClick = 0;
            }
            else if (now >= exitDeadline) exitStage = 0;
            return;
        }
        // The retainer says goodbye in a Talk dialog that waits for a click.
        if (Ready(Addon("RetainerList")) || now >= exitDeadline)
        {
            exitStage = 0;
            return;
        }
        var talk = Addon("Talk");
        if (Ready(talk) && now >= nextTalkClick)
        {
            ClickTalk(talk);
            nextTalkClick = now + 300;
        }
    }

    // The synthetic click that AutoRetainer (ECommons AddonMaster.Talk) uses to advance a dialog.
    private static void ClickTalk(AtkUnitBase* talk)
    {
        var evt = new AtkEvent
        {
            Listener = (AtkEventListener*)talk,
            Target = &AtkStage.Instance()->AtkEventTarget,
        };
        evt.State.StateFlags = (AtkEventStateFlags)132;
        AtkEventData data = default;
        talk->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
        talk->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
    }

    public IReadOnlySet<ulong> OwnRetainers => ownRetainers;
    private AtkUnitBase* Addon(string name) => (AtkUnitBase*)gui.GetAddonByName(name).Address;
    private AddonRetainerSell* Editor => (AddonRetainerSell*)Addon("RetainerSell");
    private static bool Ready(AtkUnitBase* addon) => addon != null && addon->IsVisible && addon->IsReady;
    public bool ListVisible => Ready(Addon("RetainerSellList"));
    // Read-only. The sale list hides its price editor for manual edits, and other plugins can open
    // the same editor, so the watcher reads this state and never operates it.
    public bool PriceEditorOpen => Ready((AtkUnitBase*)Editor);
    public bool ComparisonWindowOpen => Ready(Addon("ItemSearchResult"));

    // The last price confirmation that the game reported for the retainer price editor. It is the
    // direct signal for a manual change when the game has not yet exposed the new listing price.
    public (short Slot, uint Price, long At)? ConfirmedEdit => confirmedEditAt == 0
        ? null
        : (confirmedEditSlot, confirmedEditPrice, confirmedEditAt);

    public SaleItem? EditedListing
    {
        get
        {
            if (!PriceEditorOpen) return null;
            var agent = AgentRetainer.Instance();
            if (agent == null || agent->SellItemInventoryType != InventoryType.RetainerMarket) return null;
            var slot = (short)agent->SellItemInventorySlot;
            if (slot is < 0 or >= 20) return null;
            // The container price is the price before the edit; the game updates it on confirmation.
            try { return ReadItem(slot); }
            catch (InvalidOperationException) { return null; }
        }
    }
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

    // Match a retainer menu entry by the game's own text instead of a menu index, so reordering
    // cannot select the retrieve or entrust entry.
    private string MenuText(uint row)
    {
        if (!menuTexts.TryGetValue(row, out var text) || text.Length == 0)
            menuTexts[row] = text = data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(row)?.Text.ToString() ?? "";
        return text;
    }

    private int MenuIndex(AddonSelectString* addon, uint row)
    {
        var expected = MenuText(row);
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
            return Ready((AtkUnitBase*)addon) && MenuIndex(addon, SellMenuRow) >= 0;
        }
    }

    public void OpenSellList()
    {
        if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            throw new InvalidOperationException("Game controls require an active framework thread.");
        var addon = (AddonSelectString*)Addon("SelectString");
        if (!Ready((AtkUnitBase*)addon))
            throw new InvalidOperationException("The retainer menu is not ready.");
        var index = MenuIndex(addon, SellMenuRow);
        if (index < 0)
            throw new InvalidOperationException("The retainer menu has no sale-list entry.");
        SelectMenu(addon, index);
    }

    private static void SelectMenu(AddonSelectString* addon, int index)
    {
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
    // between checks and never takes ownership of the editor. The container can stay readable while
    // the sale list addon is hidden behind its price editor, so the list addon is not required.
    public IReadOnlyList<SaleItem> Snapshot()
    {
        if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            return Array.Empty<SaleItem>();
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
        // A zero-listing market sends no offerings packet, so keep the facts that prove a new
        // search window belongs to this comparison. A previous search of the same item must not
        // satisfy the empty case.
        comparedAt = Environment.TickCount64;
        searchWindowAt = 0;
        var proxy = InfoProxyItemSearch.Instance();
        nextRequestIdAtCompare = proxy == null ? 0 : proxy->NextRequestId;
        searchItemIdAtCompare = proxy == null ? 0 : proxy->SearchItemId;
        listingCountAtCompare = proxy == null ? 0 : proxy->ListingCount;
        Click((AtkUnitBase*)Editor, Editor->ComparePrices);
    }

    private void OnOfferings(IMarketBoardCurrentOfferings offerings)
    {
        // The event can run off the framework thread. Copy data only; never access game pointers here.
        var packet = new Packet(offerings.RequestId, offerings.ItemListings.Select(x =>
            (x.ListingId, new MarketListing(x.ItemId, x.IsHq, x.RetainerId, x.PricePerUnit, x.OnMannequin))).ToArray());
        packets.Enqueue(packet);
        manualPackets.Enqueue(packet);
        while (packets.Count > 20) packets.TryDequeue(out _);
        while (manualPackets.Count > 20) manualPackets.TryDequeue(out _);
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
        if (resultAddress == 0)
        {
            // This comparison needs its own result window. Record when it opened after the request.
            resultAddress = (nint)result;
            resultId = result->Id;
            searchWindowAt = Environment.TickCount64;
        }
        if (resultAddress != (nint)result || resultId != result->Id)
            throw new InvalidOperationException("The comparison window was replaced.");
        var now = Environment.TickCount64;
        if (comparison.Complete(proxy->SearchItemId, proxy->ListingCount) is { } listings) return listings;
        // No offerings packet arrived. Accept the proxy's zero count only when a new search
        // request or a new proxy state appeared after Compare() and this result window stayed
        // open for the grace period. A stale same-item search cannot prove an empty market.
        var requestIssued = proxy->NextRequestId != nextRequestIdAtCompare ||
            proxy->SearchItemId != searchItemIdAtCompare ||
            proxy->ListingCount != listingCountAtCompare;
        return comparison.AcceptsEmptyMarket(proxy->SearchItemId, proxy->ListingCount, now, comparedAt,
            searchWindowAt, requestIssued)
            ? Array.Empty<MarketListing>()
            : null;
    }

    public void BeginManualComparison(SaleItem item)
    {
        manualPackets.Clear();
        manualComparison = new(item.ItemId);
        manualStartedAt = Environment.TickCount64;
    }

    public ManualComparison? ReadManualComparison(SaleItem item)
    {
        if (manualComparison == null || !ComparisonWindowOpen) return null;
        try
        {
            while (manualPackets.TryDequeue(out var packet))
                manualComparison.Add(packet.RequestId, packet.Listings);
            var proxy = InfoProxyItemSearch.Instance();
            if (proxy == null || proxy->SearchItemId != item.ItemId) return null;
            return manualComparison.Complete(proxy->SearchItemId, proxy->ListingCount) is { } listings
                ? new ManualComparison(listings.ToArray(), manualStartedAt)
                : null;
        }
        catch (InvalidOperationException)
        {
            // A replaced window or a conflicting packet starts a new passive comparison. The
            // passive path stays quiet; it does not stop a run or change a price.
            manualComparison = new(item.ItemId);
            manualPackets.Clear();
            manualStartedAt = Environment.TickCount64;
            return null;
        }
    }

    public void EndManualComparison()
    {
        manualComparison = null;
        manualPackets.Clear();
        manualStartedAt = 0;
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
        comparedAt = 0;
        searchWindowAt = 0;
        nextRequestIdAtCompare = 0;
        searchItemIdAtCompare = 0;
        listingCountAtCompare = 0;
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

    private static AtkEvent* ButtonClickEvent(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null || button->OwnerNode == null) return null;
        // Use the actual button registration instead of guessed node indices or callback numbers.
        var evt = button->OwnerNode->AtkResNode.AtkEventManager.Event;
        for (var count = 0; evt != null && count < 64; count++, evt = evt->NextEvent)
            if (evt->State.EventType == AtkEventType.ButtonClick && evt->Listener == (AtkEventListener*)addon)
                return evt;
        return null;
    }

    private static void Click(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null || !button->IsEnabled)
            throw new InvalidOperationException("The requested button is unavailable.");
        var evt = ButtonClickEvent(addon, button);
        if (evt == null)
            throw new InvalidOperationException("The button's click registration is unsupported.");
        var copy = *evt;
        AtkEventData data = default;
        evt->Listener->ReceiveEvent(AtkEventType.ButtonClick, (int)evt->Param, &copy, &data);
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
        addonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, "RetainerSell", OnRetainerSellEvent);
        board.OfferingsReceived -= OnOfferings;
        board.HistoryReceived -= OnHistory;
        packets.Clear();
        historyPackets.Clear();
        manualPackets.Clear();
        manualComparison = null;
    }
}
