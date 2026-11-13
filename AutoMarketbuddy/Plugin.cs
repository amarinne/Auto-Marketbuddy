using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using AutoMarketbuddy.Services;
using AutoMarketbuddy.Windows;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AutoMarketbuddy;

public sealed class Plugin : IDalamudPlugin
{
    private enum Control { Preview, Apply, Stop }
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly GameRetainerMarket market;
    private readonly MarketSession session;
    private readonly AutoRetainerIpc autoRetainerIpc;
    private readonly AutoRetainerHandoff autoRetainer;
    private readonly IDataManager data;
    private readonly Dictionary<uint, string> itemNames = new();
    private readonly Configuration configuration;
    private readonly PriceChangeHistory priceHistory;
    private readonly ManualPriceWatch manualWatch;
    private readonly WindowSystem windows = new("AutoMarketbuddy");
    private readonly MainWindow window;
    private readonly ConcurrentQueue<Control> controls = new();
    private bool wasListVisible;
    private volatile bool disposed;
    private readonly ICallGateProvider<string> diagnostics;
    private volatile DiagnosticsSnapshot diagnosticSnapshot;
    private long nextDiagnosticSample;
    private static readonly JsonSerializerOptions DiagnosticJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager,
        IPluginLog pluginLog, IGameGui gameGui, IFramework framework, IClientState clientState,
        IPlayerState playerState, IObjectTable objectTable, IChatGui chatGui, IMarketBoard marketBoard,
        IDataManager dataManager, IAddonLifecycle addonLifecycle)
    {
        this.pluginInterface = pluginInterface;
        commands = commandManager;
        this.framework = framework;
        data = dataManager;
        client = clientState;
        player = playerState;
        chat = chatGui;
        log = pluginLog;
        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new();
        configuration.Initialize(pluginInterface);
        // One Information line per baseline and per price-change row gives a later live test a
        // log trail even when the game state does not show the change.
        priceHistory = new(message => log.Information(message));
        market = new(gameGui, clientState, playerState, objectTable, pluginInterface, marketBoard, framework, dataManager, addonLifecycle);
        session = new(market, priceHistory);
        manualWatch = new(market, priceHistory);
        autoRetainerIpc = new(pluginInterface);
        autoRetainer = new(session, market, autoRetainerIpc,
            () => new(configuration.AutoRetainerMode, configuration.UndercutAmount,
                configuration.WaitDelayMillis, configuration.UseSaleHistoryWhenNoListings),
            () => !disposed && client.IsLoggedIn && player.IsLoaded,
            message => log.Information(message),
            message =>
            {
                log.Warning(message);
                chat.PrintError($"[Auto-Marketbuddy] {message}");
            });
        diagnosticSnapshot = new(1, DateTimeOffset.UtcNow, typeof(Plugin).Assembly.GetName().Version!.ToString(), session.GetDiagnostics(Environment.TickCount64));
        diagnostics = pluginInterface.GetIpcProvider<string>(DiagnosticsSnapshot.Endpoint);
        diagnostics.RegisterFunc(() => JsonSerializer.Serialize(diagnosticSnapshot, DiagnosticJson));
        window = new(session, configuration, autoRetainer, priceHistory, Start, Stop, ItemName);
        windows.AddWindow(window);
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Auto-Marketbuddy. preview (or start): preview this retainer; apply: adjust prices; stop: cancel; status: show status; autoretainer off|preview|apply: opt in to AutoRetainer's post-process handoff.",
        };
        foreach (var name in PluginCommands.All) commandManager.AddHandler(name, commandInfo);
        framework.Update += OnUpdate;
    }

    private string ItemName(uint itemId)
    {
        if (itemNames.TryGetValue(itemId, out var cached)) return cached;
        var name = data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ToString();
        if (string.IsNullOrWhiteSpace(name)) name = $"Item {itemId}";
        itemNames[itemId] = name;
        return name;
    }

    private void Draw() => windows.Draw();
    private void Open() => window.IsOpen = true;

    private void Start(bool apply)
    {
        if (autoRetainer.IsBusy)
        {
            chat.PrintError("[Auto-Marketbuddy] An AutoRetainer handoff is in progress. Wait for it to finish or stop it.");
            return;
        }
        window.IsOpen = true;
        if (!disposed) controls.Enqueue(apply ? Control.Apply : Control.Preview);
    }

    private void Stop() { if (!disposed) controls.Enqueue(Control.Stop); }

    private void OnCommand(string command, string args)
    {
        var text = args.Trim();
        var lower = text.ToLowerInvariant();
        if (lower == "autoretainer" || lower.StartsWith("autoretainer ", StringComparison.Ordinal))
        {
            var value = lower["autoretainer".Length..].Trim();
            SetAutoRetainerMode(value);
            return;
        }
        switch (lower)
        {
            case "": Open(); break;
            case "start":
            case "preview": Start(false); break;
            case "apply": Start(true); break;
            case "stop": Stop(); break;
            case "status":
                var auto = configuration.AutoRetainerMode == AutoRetainerMode.Off
                    ? ""
                    : $" AutoRetainer: {autoRetainer.Status}.";
                chat.Print($"[Auto-Marketbuddy] {session.Status}. Cached: {session.CachedItemCount}.{auto} {session.LastError}");
                break;
            default: chat.Print("[Auto-Marketbuddy] /amb [preview|apply|stop|status|autoretainer off|preview|apply] (alias: /aundercut)"); break;
        }
    }

    private void SetAutoRetainerMode(string value)
    {
        AutoRetainerMode mode;
        switch (value)
        {
            case "off": mode = AutoRetainerMode.Off; break;
            case "preview": mode = AutoRetainerMode.Preview; break;
            case "apply": mode = AutoRetainerMode.Apply; break;
            case "":
            case "status":
                chat.Print($"[Auto-Marketbuddy] AutoRetainer automation: {configuration.AutoRetainerMode}. " +
                    $"Loaded: {(autoRetainerIpc.IsAvailable ? "yes" : "no")}. {autoRetainer.Status}.");
                return;
            default:
                chat.Print("[Auto-Marketbuddy] /amb autoretainer [off|preview|apply|status]");
                return;
        }
        configuration.AutoRetainerMode = mode;
        configuration.Save();
        var availability = autoRetainerIpc.IsAvailable ? "" : " AutoRetainer is not loaded; the setting applies when it loads.";
        chat.Print($"[Auto-Marketbuddy] AutoRetainer automation: {mode}.{availability}");
    }

    private void OnUpdate(IFramework unused)
    {
        if (disposed) return;
        try
        {
            var now = Environment.TickCount64;
            var loaded = client.IsLoggedIn && player.IsLoaded;
            session.ObserveContext(loaded ? player.ContentId : 0, loaded ? player.CurrentWorld.RowId : 0);
            // UI and chat use one serialized control boundary before any native work for this frame.
            while (controls.TryDequeue(out var control))
            {
                if (control == Control.Stop)
                {
                    session.Stop();
                    autoRetainer.Cancel("Stopped by the user.", now);
                }
                else
                {
                    session.Start(control == Control.Apply, configuration.UndercutAmount,
                        configuration.WaitDelayMillis, now,
                        configuration.UseSaleHistoryWhenNoListings);
                    if (session.LastError.Length > 0) chat.PrintError($"[Auto-Marketbuddy] {session.LastError}");
                }
            }
            var visible = market.ListVisible;
            if (configuration.SyncWindowWithSellList)
            {
                if (visible && !wasListVisible) window.IsOpen = true;
                else if (!visible && wasListVisible) window.IsOpen = false;
            }
            wasListVisible = visible;
            if (session.IsRunning || autoRetainer.IsActive) market.CancelRetainerExit();
            else market.UpdateRetainerExit(now);
            var wasRunning = session.IsRunning;
            session.Update(now);
            // The handoff observes completion or failure after the session has advanced this frame.
            autoRetainer.Update(now);
            // Outside a run, a listing price can change through manual edits or another plugin.
            // The watcher reads the sale list and its price editor only; this path never fetches
            // market data, sets a price, or confirms one.
            if (!session.IsRunning) manualWatch.Update(now);
            if (wasRunning && !session.IsRunning && session.Status == "Completed" &&
                configuration.ExitRetainerOnFinish && !autoRetainer.IsActive)
                market.BeginRetainerExit();
            if (wasRunning && !session.IsRunning && session.LastError.Length > 0)
            {
                log.Warning($"Auto-Marketbuddy stopped: {session.LastError}");
                chat.PrintError($"[Auto-Marketbuddy] {session.LastError}");
            }
        }
        catch (Exception ex)
        {
            session.Stop();
            autoRetainer.Cancel("Auto-Marketbuddy update failed.", Environment.TickCount64);
            log.Error(ex, "Auto-Marketbuddy update failed");
        }
        finally
        {
            var now = Environment.TickCount64;
            if (now >= nextDiagnosticSample)
            {
                nextDiagnosticSample = now + 500;
                session.ExpireCache(now);
                diagnosticSnapshot = new(1, DateTimeOffset.UtcNow, typeof(Plugin).Assembly.GetName().Version!.ToString(), session.GetDiagnostics(now));
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= OnUpdate;
        diagnostics.UnregisterFunc();
        pluginInterface.UiBuilder.Draw -= Draw;
        pluginInterface.UiBuilder.OpenMainUi -= Open;
        pluginInterface.UiBuilder.OpenConfigUi -= Open;
        foreach (var name in PluginCommands.All) commands.RemoveHandler(name);
        controls.Clear();
        autoRetainer.Dispose();
        autoRetainerIpc.Dispose();
        session.Dispose();
        market.Dispose();
        windows.RemoveAllWindows();
    }
}
