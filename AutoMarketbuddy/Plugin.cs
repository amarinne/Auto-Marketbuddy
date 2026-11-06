using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using AutoMarketbuddy.Services;
using AutoMarketbuddy.Windows;
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
    private readonly IDataManager data;
    private readonly Dictionary<uint, string> itemNames = new();
    private readonly Configuration configuration;
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
        IDataManager dataManager)
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
        market = new(gameGui, clientState, playerState, objectTable, pluginInterface, marketBoard, framework);
        session = new(market);
        diagnosticSnapshot = new(1, DateTimeOffset.UtcNow, "1.1.0.0", session.GetDiagnostics(Environment.TickCount64));
        diagnostics = pluginInterface.GetIpcProvider<string>(DiagnosticsSnapshot.Endpoint);
        diagnostics.RegisterFunc(() => JsonSerializer.Serialize(diagnosticSnapshot, DiagnosticJson));
        window = new(session, configuration, Start, Stop, ItemName);
        windows.AddWindow(window);
        pluginInterface.UiBuilder.Draw += Draw;
        pluginInterface.UiBuilder.OpenMainUi += Open;
        pluginInterface.UiBuilder.OpenConfigUi += Open;
        var commandInfo = new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Auto-Marketbuddy. preview (or start): preview this retainer; apply: adjust prices; stop: cancel; status: show status.",
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
        window.IsOpen = true;
        if (!disposed) controls.Enqueue(apply ? Control.Apply : Control.Preview);
    }

    private void Stop() { if (!disposed) controls.Enqueue(Control.Stop); }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "": Open(); break;
            case "start":
            case "preview": Start(false); break;
            case "apply": Start(true); break;
            case "stop": Stop(); break;
            case "status": chat.Print($"[Auto-Marketbuddy] {session.Status}. Cached: {session.CachedItemCount}. {session.LastError}"); break;
            default: chat.Print("[Auto-Marketbuddy] /amb [preview|apply|stop|status] (alias: /aundercut)"); break;
        }
    }

    private void OnUpdate(IFramework unused)
    {
        if (disposed) return;
        try
        {
            var loaded = client.IsLoggedIn && player.IsLoaded;
            session.ObserveContext(loaded ? player.ContentId : 0, loaded ? player.CurrentWorld.RowId : 0);
            // UI and chat use one serialized control boundary before any native work for this frame.
            while (controls.TryDequeue(out var control))
            {
                if (control == Control.Stop) session.Stop();
                else
                {
                    session.Start(control == Control.Apply, configuration.UndercutAmount,
                        configuration.WaitDelayMillis, Environment.TickCount64,
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
            if (session.IsRunning) market.CancelRetainerExit();
            else market.UpdateRetainerExit(Environment.TickCount64);
            var wasRunning = session.IsRunning;
            session.Update(Environment.TickCount64);
            if (wasRunning && !session.IsRunning && session.Status == "Completed" &&
                configuration.ExitRetainerOnFinish)
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
            log.Error(ex, "Auto-Marketbuddy update failed");
        }
        finally
        {
            var now = Environment.TickCount64;
            if (now >= nextDiagnosticSample)
            {
                nextDiagnosticSample = now + 500;
                session.ExpireCache(now);
                diagnosticSnapshot = new(1, DateTimeOffset.UtcNow, "1.1.0.0", session.GetDiagnostics(now));
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
        session.Dispose();
        market.Dispose();
        windows.RemoveAllWindows();
    }
}
