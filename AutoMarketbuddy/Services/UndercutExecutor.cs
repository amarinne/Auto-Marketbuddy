using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Logging;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Utility;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui; 

using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;

namespace AutoMarketbuddy.Services
{
    public unsafe class UndercutExecutor : IDisposable
    {
        private readonly IPluginLog pluginLog;
        private readonly IGameGui gameGui;
        private readonly AtkOrderService atkOrderService;
        private readonly Configuration config;
        private readonly AllaganMarketClient marketClient;
        private readonly IClientState clientState;
        private readonly IChatGui chatGui;

        public bool IsRunning { get; private set; } = false;
        public string CurrentState { get; private set; } = "Idle";
        public string LastError { get; private set; } = "";

        private int CurrentItemIndex = 0;
        private int WaitTimer = 0;
        private uint TargetPrice = 0;
        private int PollTimer = 0;
        private bool PriceCopiedFromChat = false;

        private uint CurrentItemId = 0;
        private ulong CurrentWorldId = 0;
        private bool CurrentItemIsHq = false;
        
        private enum State
        {
            Idle,
            StartLoop,
            CheckItem,
            SelectCurrentItem,
            WaitForContextMenu,
            WaitForMarketWindow,
            WaitForMarketData,
            ClickMarketItem,
            ConfirmPrice,
            WaitForPriceSet,
            NextItem
        }

        private State StateMachine = State.Idle;

        public UndercutExecutor(IPluginLog pluginLog, IGameGui gameGui, AtkOrderService atkOrderService, Configuration config, AllaganMarketClient marketClient, IClientState clientState, IChatGui chatGui)
        {
            this.pluginLog = pluginLog;
            this.gameGui = gameGui;
            this.atkOrderService = atkOrderService;
            this.config = config;
            this.marketClient = marketClient;
            this.clientState = clientState;
            this.chatGui = chatGui;
            
            this.chatGui.ChatMessage += OnChatMessage;
        }

        public void Dispose()
        {
            this.chatGui.ChatMessage -= OnChatMessage;
        }

        private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
             if (!IsRunning) return;

             var text = message.TextValue;
             if (config.VerboseLogging) pluginLog.Information($"[CHAT] Type: {type}, Msg: {text}");
             
             // Check for "copied to clipboard."
             // The user reported format: "14,999 copied to clipboard."
             if (text.Contains("copied to clipboard"))
             {
                 if (StateMachine == State.WaitForMarketWindow)
                 {
                     if (config.VerboseLogging) pluginLog.Information($"Chat trigger detected: {text}");
                     PriceCopiedFromChat = true;
                     // We could potentially parse the price here too, but reading clipboard is safer
                 }
             }
        }

        public void Start()
        {
            if (IsRunning) return;
            IsRunning = true;
            CurrentItemIndex = 0;
            StateMachine = State.StartLoop;
            pluginLog.Information("Starting undercut process...");
        }

        public void Stop()
        {
            IsRunning = false;
            StateMachine = State.Idle;
            CurrentState = "Idle";
            pluginLog.Information("Stopping undercut process.");
        }

        public void Update()
        {
            // Record mode: continuously log window states
            if (config.RecordMode)
            {
                RecordWindowStates();
            }
            
            if (!IsRunning) return;

            if (WaitTimer > 0)
            {
                WaitTimer--;
                return;
            }

            try
            {
                RunStateMachine();
            }
            catch (Exception e)
            {
                LastError = e.Message;
                pluginLog.Error(e, "Error in undercut logic");
                Stop();
            }
        }
        
        private void RunStateMachine()
        {
            CurrentState = StateMachine.ToString() + $" (Idx: {CurrentItemIndex})";
            
            // Note: This is a simplified state machine based on RetainerItemUndercut logic
            // adapted to verify we can compile and run.

            // Declare addon pointers that may be used across multiple cases
            AtkUnitBase* listAddon;
            AtkUnitBase* contextMenu;
            AtkUnitBase* itemSearch;
            AtkUnitBase* marketBoard;
            AtkUnitBase* retainerSellOverlay;
            AtkUnitBase* inputNumeric;

            switch (StateMachine)
            {
                case State.StartLoop:
                    listAddon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("RetainerSellList", 1);
                    if (listAddon == null || !listAddon->IsVisible)
                    {
                        pluginLog.Error("Retainer list not visible");
                        Stop();
                        return;
                    }
                    
                    // Use AllaganMarket's AtkOrderService to determine order?
                    // var order = atkOrderService.GetCurrentOrder();
                    // For now, we iterate index 0-19 blindly like RetainerItemUndercut or until empty.
                    
                    StateMachine = State.CheckItem;
                    WaitTimer = 5; // Allow for data refresh from AllaganMarket
                    break;

                case State.CheckItem:
                    if (config.VerboseLogging) pluginLog.Information($"Checking item {CurrentItemIndex}");

                    // Reset Current Item Info
                    CurrentItemId = 0;
                    CurrentWorldId = 0;
                    CurrentItemIsHq = false;
                    TargetPrice = 0;
                    PollTimer = 0;

                    var itemData = marketClient.GetSaleItem(CurrentItemIndex);
                    
                    // Fallback: If IPC returns null, try to read the retainer inventory directly
                    uint itemId = 0;
                    uint currentPrice = 0;
                    ulong worldId = 0;
                    bool isHq = false;

                    if (itemData != null && itemData.HasValue)
                    {
                        (itemId, worldId, currentPrice, isHq) = itemData.Value;
                        if (config.VerboseLogging) pluginLog.Information($"IPC Data Found: ID {itemId}");
                    }
                    else
                    {
                        if (config.VerboseLogging) pluginLog.Information($"IPC data null for index {CurrentItemIndex}, attempting manual read for ID...");
                        // Manual read logic here using InventoryManager (simplified)
                        var inventoryManager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                        if (inventoryManager != null)
                        {
                            var container = inventoryManager->GetInventoryContainer(InventoryType.RetainerMarket);
                            if (container != null)
                            {
                                // We need to map Visual Index (0-19) to Inventory Slot (0-19 mixed)
                                // AtkOrderService should have the map
                                var order = atkOrderService.GetCurrentOrder();
                                // Inverse the map: Visual Index -> Inventory Slot
                                int invSlot = -1;
                                if (order != null)
                                {
                                    foreach (var kvp in order)
                                    {
                                        if (kvp.Value == CurrentItemIndex)
                                        {
                                            invSlot = kvp.Key;
                                            break;
                                        }
                                    }
                                }
                                else 
                                {
                                    // Fallback if order is null: assume 1:1 if we can't map?
                                    // Usually RetainerSellList is sorted, but RetainerMarket inventory might not be.
                                    // For now, if order is null, we can iterate and find first valid item not used? No, that's complex.
                                    // Let's assume order service works or we fail.
                                }

                                if (invSlot != -1 && invSlot < container->Size)
                                {
                                    var item = container->GetInventorySlot(invSlot);
                                    if (item->ItemId != 0)
                                    {
                                        itemId = item->ItemId;
                                        isHq = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                                        currentPrice = 0; // We don't know current price yet
                                        worldId = (ulong)clientState.LocalPlayer!.CurrentWorld.Value.RowId;
                                        
                                        if (config.VerboseLogging) pluginLog.Information($"Visual Index {CurrentItemIndex} -> Slot {invSlot}: ID {itemId} found manually.");
                                    }
                                }
                            }
                        }
                    }

                    if (itemId == 0) // Empty slot
                    {
                         StateMachine = State.NextItem;
                         return;
                    }

                    // Store for later
                    CurrentItemId = itemId;
                    CurrentWorldId = worldId;
                    CurrentItemIsHq = isHq;

                    // Proceed to interact 
                    if (config.VerboseLogging) pluginLog.Information($"Proceeding to adjust item {itemId}...");
                    StateMachine = State.SelectCurrentItem;
                    break;

                case State.SelectCurrentItem:
                    if (config.VerboseLogging) pluginLog.Information($"Selecting item {CurrentItemIndex}");
                    listAddon = (AtkUnitBase*)(nint)gameGui.GetAddonByName("RetainerSellList", 1);
                    if (listAddon == null) { Stop(); return; }
                    
                    // Open context menu for this item
                    FireCallback(listAddon, 0, CurrentItemIndex, 1); 
                    
                    WaitTimer = 10;
                    StateMachine = State.WaitForContextMenu;
                    break;
                    
                case State.WaitForContextMenu:
                    contextMenu = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ContextMenu", 1);
                    if (contextMenu != null && contextMenu->IsVisible)
                    {
                         if (config.VerboseLogging) pluginLog.Information("Context menu found, clicking adjust price");
                         // Click "Adjust Price" option (index 0)
                         FireCallback(contextMenu, 0, 0, 0, -1, 0); 
                         StateMachine = State.WaitForMarketWindow;
                         WaitTimer = 15;
                         PollTimer = 0;
                    }
                    else
                    {
                         if (config.VerboseLogging) pluginLog.Information("Waiting for context menu...");
                         PollTimer++;
                         if (PollTimer > 50) { StateMachine = State.NextItem; }
                    }
                    break;

                case State.WaitForMarketWindow:
                    // We expect the other plugin to open "ItemSearch" or "MarketBoard"
                    itemSearch = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ItemSearch", 1);
                    marketBoard = (AtkUnitBase*)(nint)gameGui.GetAddonByName("MarketBoard", 1);
                    
                    // Check flags
                    bool marketWindowVisible = (itemSearch != null && itemSearch->IsVisible) || (marketBoard != null && marketBoard->IsVisible);

                    if (marketWindowVisible || PriceCopiedFromChat)
                    {
                        if (config.VerboseLogging) 
                        {
                            if (PriceCopiedFromChat) pluginLog.Information("Price copied (Chat Trigger). Reading clipboard and closing overlay...");
                            else pluginLog.Information("Market window detected. Waiting for chat trigger...");
                        }
                        
                        if (PriceCopiedFromChat)
                        {
                            // Chat trigger fired - AllaganMarket is copying price to clipboard
                            // Don't read clipboard yet, wait for the actual copy operation
                            if (config.VerboseLogging) 
                                pluginLog.Information("Chat trigger detected. Waiting for clipboard...");
                            
                            StateMachine = State.WaitForMarketData;
                            PollTimer = 0;
                            WaitTimer = 120; // Wait 2 seconds for market data API call to complete
                        }
                    }
                    else
                    {
                        if (PollTimer % 60 == 0 && config.VerboseLogging) pluginLog.Information($"Waiting for MarketBoard/ItemSearch window or chat trigger... ({PollTimer}/600)");
                        PollTimer++;
                        if (PollTimer > 600)
                        {
                            if (config.VerboseLogging) pluginLog.Information("Timed out waiting for market window.");
                            StateMachine = State.NextItem;
                            PollTimer = 0;
                        }
                    }
                    break;

                case State.WaitForMarketData:
                    // Wait for AllaganMarket's API call to complete and populate the overlay
                    if (config.VerboseLogging && PollTimer % 60 == 0)
                        pluginLog.Information($"Waiting for market data to load... ({WaitTimer} frames remaining)");
                    
                    // Wait the full timer for API call and clipboard copy
                    if (WaitTimer == 0)
                    {
                        // Now read the clipboard after waiting
                        ReadClipboardAndSetPrice();
                        
                        if (TargetPrice > 0)
                        {
                            // Verify ItemSearchResult exists and has data before proceeding
                            var itemSearchCheck = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ItemSearchResult", 1);
                            if (itemSearchCheck != null && itemSearchCheck->IsVisible)
                            {
                                if (config.VerboseLogging)
                                    pluginLog.Information("ItemSearchResult is visible, proceeding to click");
                                StateMachine = State.ClickMarketItem;
                                PollTimer = 0;
                            }
                            else
                            {
                                if (config.VerboseLogging)
                                    pluginLog.Information("Waiting for ItemSearchResult to appear...");
                                // Give it more time
                                WaitTimer = 60;
                                PollTimer++;
                                if (PollTimer > 5) // Tried 5 times
                                {
                                    pluginLog.Error("ItemSearchResult never appeared after reading price");
                                    StateMachine = State.NextItem;
                                }
                            }
                        }
                        else
                        {
                            pluginLog.Error("Failed to read valid price from clipboard after waiting");
                            StateMachine = State.NextItem;
                        }
                    }
                    break;

                case State.ClickMarketItem:
                    // Set the price directly in RetainerSell window
                    if (PollTimer == 0)
                    {
                        pluginLog.Information($"[SetPrice] Starting - TargetPrice: {TargetPrice}");
                        
                        // First, close ItemSearchResult if it's open
                        var itemSearchResultToClose = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ItemSearchResult", 1);
                        if (itemSearchResultToClose != null && itemSearchResultToClose->IsVisible)
                        {
                            pluginLog.Information("[SetPrice] Closing ItemSearchResult window first");
                            itemSearchResultToClose->Close(true);
                        }
                    }
                    
                    var retainerSell = (AtkUnitBase*)(nint)gameGui.GetAddonByName("RetainerSell", 1);
                    
                    if (retainerSell == null || !retainerSell->IsVisible)
                    {
                        // Wait for RetainerSell window to open
                        PollTimer++;
                        if (PollTimer % 60 == 0)
                            pluginLog.Information($"[SetPrice] Waiting for RetainerSell window... ({PollTimer})");
                        
                        if (PollTimer > 180) // 3 seconds
                        {
                            pluginLog.Error("RetainerSell window never appeared");
                            StateMachine = State.NextItem;
                        }
                        break;
                    }
                    
                    // Give the window a moment to fully initialize
                    if (PollTimer < 15)
                    {
                        PollTimer++;
                        break;
                    }
                    
                    try
                    {
                        pluginLog.Information($"[SetPrice] RetainerSell window ready, setting price to {TargetPrice}");
                        
                        // Verify node count matches expected structure
                        if (retainerSell->UldManager.NodeListCount < 15)
                        {
                            pluginLog.Error($"RetainerSell has unexpected node count: {retainerSell->UldManager.NodeListCount}");
                            StateMachine = State.NextItem;
                            break;
                        }
                        
                        // Get the price input component (NodeList[15] according to Marketbuddy)
                        var priceNode = retainerSell->UldManager.NodeList[15];
                        if (priceNode == null)
                        {
                            pluginLog.Error("Price node is null");
                            StateMachine = State.NextItem;
                            break;
                        }
                        
                        var priceComponent = (AtkComponentNumericInput*)priceNode->GetComponent();
                        if (priceComponent == null)
                        {
                            pluginLog.Error("Price component is null");
                            StateMachine = State.NextItem;
                            break;
                        }
                        
                        // Apply undercut
                        var undercutPrice = (int)TargetPrice - (int)config.UndercutAmount;
                        if (undercutPrice < 1) undercutPrice = 1;
                        
                        pluginLog.Information($"[SetPrice] Setting price from {TargetPrice} to {undercutPrice} (undercut: {config.UndercutAmount})");
                        priceComponent->SetValue(undercutPrice);
                        
                        // Wait a frame for the value to be set
                        WaitTimer = 5;
                        StateMachine = State.ConfirmPrice;
                    }
                    catch (Exception ex)
                    {
                        pluginLog.Error(ex, "[SetPrice] Exception while setting price");
                        StateMachine = State.NextItem;
                    }
                    break;

                case State.ConfirmPrice:
                    if (WaitTimer > 0)
                    {
                        break; // Still waiting
                    }
                    
                    var retainerSellToConfirm = (AtkUnitBase*)(nint)gameGui.GetAddonByName("RetainerSell", 1);
                    if (retainerSellToConfirm != null && retainerSellToConfirm->IsVisible)
                    {
                        pluginLog.Information("[ConfirmPrice] Sending confirmation callback");
                        
                        // Confirm by clicking the confirm button (callback 0, param 0)
                        FireCallback(retainerSellToConfirm, 0, 0);
                        
                        pluginLog.Information("[ConfirmPrice] Confirmation sent");
                        
                        WaitTimer = 30; // Wait before next item
                        StateMachine = State.WaitForPriceSet;
                    }
                    else
                    {
                        pluginLog.Warning("[ConfirmPrice] RetainerSell window disappeared");
                        StateMachine = State.NextItem;
                    }
                    break;

                case State.WaitForPriceSet:
                    // Wait for Marketbuddy to process the click and set the price
                    if (WaitTimer > 0)
                    {
                        // Still waiting
                    }
                    else
                    {
                        if (config.VerboseLogging) pluginLog.Information("Price should be set by now, closing windows and moving to next item");
                        
                        // Close market windows before moving to next item
                        var itemSearchToClose = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ItemSearch", 1);
                        if (itemSearchToClose != null && itemSearchToClose->IsVisible)
                        {
                            itemSearchToClose->Close(true);
                            if (config.VerboseLogging) pluginLog.Information("Closed ItemSearch window");
                        }
                        
                        var itemSearchResultToClose = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ItemSearchResult", 1);
                        if (itemSearchResultToClose != null && itemSearchResultToClose->IsVisible)
                        {
                            itemSearchResultToClose->Close(true);
                            if (config.VerboseLogging) pluginLog.Information("Closed ItemSearchResult window");
                        }
                        
                        // Reset state
                        PriceCopiedFromChat = false;
                        TargetPrice = 0;
                        
                        StateMachine = State.NextItem;
                    }
                    break;
                    
                case State.NextItem:
                    // Small delay to ensure windows are fully closed
                    if (WaitTimer == -1) // First time entering this state
                    {
                        WaitTimer = 30; // Wait 0.5 seconds before next item
                        PollTimer = 0;
                        if (config.VerboseLogging) pluginLog.Information("Waiting for windows to close...");
                        break;
                    }
                    
                    if (WaitTimer > 0)
                    {
                        break; // Still waiting
                    }
                    
                    WaitTimer = -1; // Reset for next use
                    CurrentItemIndex++;
                    if (CurrentItemIndex >= 20) 
                    {
                        Stop();
                    }
                    else
                    {
                        StateMachine = State.CheckItem;
                    }
                    break;
            }
        }

        private void LogAddonStatus()
        {
            try
            {
                var inputNumeric = (AtkUnitBase*)(nint)gameGui.GetAddonByName("InputNumeric", 1);
                var contextMenu = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ContextMenu", 1);
                var retainerSellList = (AtkUnitBase*)(nint)gameGui.GetAddonByName("RetainerSellList", 1);
                var itemSearch = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ItemSearch", 1);
                var marketBoard = (AtkUnitBase*)(nint)gameGui.GetAddonByName("MarketBoard", 1);
                var retainerSellOverlay = (AtkUnitBase*)(nint)gameGui.GetAddonByName("RetainerSellOverlay", 1);

                pluginLog.Information($"[DEBUG STATE] SellList: {(nint)retainerSellList:X} ({(retainerSellList != null ? retainerSellList->IsVisible : false)}), " +
                                      $"CtxMenu: {(nint)contextMenu:X} ({(contextMenu != null ? contextMenu->IsVisible : false)}), " +
                                      $"InputNumeric: {(nint)inputNumeric:X} ({(inputNumeric != null ? inputNumeric->IsVisible : false)}), " +
                                      $"ItemSearch: {(nint)itemSearch:X} ({(itemSearch != null ? itemSearch->IsVisible : false)}), " +
                                      $"MarketBoard: {(nint)marketBoard:X} ({(marketBoard != null ? marketBoard->IsVisible : false)}), " +
                                      $"Overlay: {(nint)retainerSellOverlay:X} ({(retainerSellOverlay != null ? retainerSellOverlay->IsVisible : false)})");
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Error logging addon status");
            }
        }

        private int recordFrameCounter = 0;
        private string lastRecordedState = "";

        private void RecordWindowStates()
        {
            // Record every 15 frames (~0.25 seconds at 60fps) to avoid spam
            recordFrameCounter++;
            if (recordFrameCounter < 15) return;
            recordFrameCounter = 0;

            try
            {
                var retainerSellList = (AtkUnitBase*)(nint)gameGui.GetAddonByName("RetainerSellList", 1);
                var contextMenu = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ContextMenu", 1);
                var inputNumeric = (AtkUnitBase*)(nint)gameGui.GetAddonByName("InputNumeric", 1);
                var itemSearch = (AtkUnitBase*)(nint)gameGui.GetAddonByName("ItemSearch", 1);
                var marketBoard = (AtkUnitBase*)(nint)gameGui.GetAddonByName("MarketBoard", 1);
                var retainerSellOverlay = (AtkUnitBase*)(nint)gameGui.GetAddonByName("RetainerSellOverlay", 1);

                var state = $"SellList:{(retainerSellList != null && retainerSellList->IsVisible ? "1" : "0")} " +
                           $"CtxMenu:{(contextMenu != null && contextMenu->IsVisible ? "1" : "0")} " +
                           $"Input:{(inputNumeric != null && inputNumeric->IsVisible ? "1" : "0")} " +
                           $"Search:{(itemSearch != null && itemSearch->IsVisible ? "1" : "0")} " +
                           $"Market:{(marketBoard != null && marketBoard->IsVisible ? "1" : "0")} " +
                           $"Overlay:{(retainerSellOverlay != null && retainerSellOverlay->IsVisible ? "1" : "0")}";

                // Only log when state changes
                if (state != lastRecordedState)
                {
                    pluginLog.Information($"[RECORD] {state}");
                    lastRecordedState = state;
                }
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Error recording window states");
            }
        }
        
        // Helper
        private void ReadClipboardAndSetPrice()
        {
            try 
            {
                var text = ImGui.GetClipboardText();
                if (config.VerboseLogging) pluginLog.Information($"Raw clipboard: '{text}'");
                
                // AllaganMarket formats as "14,999 copied to clipboard."
                // Extract just the number part
                var match = System.Text.RegularExpressions.Regex.Match(text, @"([\d,]+)\s*copied");
                if (match.Success)
                {
                    text = match.Groups[1].Value;
                }
                
                // Strip comma, dot, spaces and parse
                var cleaned = text.Replace(",", "").Replace(".", "").Replace(" ", "").Trim();
                if (config.VerboseLogging) pluginLog.Information($"Cleaned number: '{cleaned}'");
                
                if (int.TryParse(cleaned, out int price) && price > 0)
                {
                    TargetPrice = (uint)price;
                    if (config.VerboseLogging) pluginLog.Information($"Copied price from clipboard: {TargetPrice}");
                }
                else
                {
                    if (config.VerboseLogging) pluginLog.Information($"Clipboard did not contain valid price: '{text}'");
                    // Keep existing TargetPrice if any, or it stays 0
                }
            } 
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to read clipboard");
            }
        }

        private void SendClickEvent(AtkUnitBase* addon, uint eventType, uint eventParam)
        {
            try
            {
                if (addon == null) return;
                
                // Create event structures like Marketbuddy's Commons.SendClick
                var listener = (AtkEventListener*)addon;
                
                var atkEvent = Marshal.AllocHGlobal(0x40);
                for (var i = 0; i < 0x40; i++)
                    Marshal.WriteByte(atkEvent, i, 0);
                
                Marshal.WriteIntPtr(atkEvent, 0x8, (IntPtr)addon);
                Marshal.WriteIntPtr(atkEvent, 0x10, (IntPtr)listener);
                
                var atkEventData = Marshal.AllocHGlobal(0x40);
                for (var i = 0; i < 0x40; i++)
                    Marshal.WriteByte(atkEventData, i, 0);
                
                listener->ReceiveEvent((FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType)eventType, (int)eventParam, (AtkEvent*)atkEvent, (AtkEventData*)atkEventData);
                
                Marshal.FreeHGlobal(atkEvent);
                Marshal.FreeHGlobal(atkEventData);
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to send click event");
            }
        }

        private void SendClickEventWithNode(AtkUnitBase* addon, uint eventType, IntPtr nodeParam)
        {
            try
            {
                if (addon == null) return;
                
                // Create event structures like Marketbuddy's Commons.SendClick
                // The key difference is we pass the actual node pointer in the event data
                var listener = (AtkEventListener*)addon;
                
                var atkEvent = Marshal.AllocHGlobal(0x40);
                for (var i = 0; i < 0x40; i++)
                    Marshal.WriteByte(atkEvent, i, 0);
                
                Marshal.WriteIntPtr(atkEvent, 0x8, nodeParam); // Pass the actual clicked node
                Marshal.WriteIntPtr(atkEvent, 0x10, (IntPtr)listener);
                
                var atkEventData = Marshal.AllocHGlobal(0x40);
                for (var i = 0; i < 0x40; i++)
                    Marshal.WriteByte(atkEventData, i, 0);
                
                listener->ReceiveEvent((FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType)eventType, (int)nodeParam.ToInt32(), (AtkEvent*)atkEvent, (AtkEventData*)atkEventData);
                
                Marshal.FreeHGlobal(atkEvent);
                Marshal.FreeHGlobal(atkEventData);
            }
            catch (Exception ex)
            {
                pluginLog.Error(ex, "Failed to send click event with node");
            }
        }

        public static void FireCallback(AtkUnitBase* unitBase, params object[] values)
        {
             if (unitBase == null) return;
             var atkValues = (AtkValue*)Marshal.AllocHGlobal(values.Length * sizeof(AtkValue));
             if (atkValues == null) return;
             try
             {
                 for (int i = 0; i < values.Length; i++)
                 {
                     var v = values[i];
                     if (v is bool b)
                     {
                         atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool;
                         atkValues[i].Byte = (byte)(b ? 1 : 0);
                     }
                     else if (v is int n)
                     {
                         atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                         atkValues[i].Int = n;
                     }
                     else if (v is string s)
                     {
                         atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String;
                         var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                         var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
                         Marshal.Copy(bytes, 0, ptr, bytes.Length);
                         ((byte*)ptr)[bytes.Length] = 0;
                         atkValues[i].String = (byte*)ptr;
                     }
                     else if (v is uint u)
                     {
                          atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
                          atkValues[i].UInt = u;
                     }
                 }
                 unitBase->FireCallback((uint)values.Length, atkValues, true);
             }
             finally
             {
                 for (int i = 0; i < values.Length; i++)
                 {
                     if (values[i] is string)
                     {
                         Marshal.FreeHGlobal((IntPtr)(byte*)atkValues[i].String);
                     }
                 }
                 Marshal.FreeHGlobal((IntPtr)atkValues);
             }
        }
    }
}
