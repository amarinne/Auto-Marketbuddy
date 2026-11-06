using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace XivCompanionBridge;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private readonly IFramework framework;
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly ICondition conditions;
    private readonly IDataManager data;
    private readonly StatePublisher publisher;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IGameGui gui;
    private readonly ICallGateSubscriber<string> ambDiagnostics;
    private long nextPluginSample;
    private PluginHealth[] plugins = [];
    private static readonly string[] WatchedWindows = ["RetainerSellList", "RetainerSell", "ItemSearchResult", "ItemSearch", "ContextMenu", "SelectYesno"];
    private readonly object sampleGate = new();
    private Task worker = Task.CompletedTask;
    private long nextSample;
    private long sequence;
    private volatile bool disposed;

    public Plugin(IDalamudPluginInterface pi, IFramework framework, IClientState client,
        IPlayerState player, IObjectTable objects, ITargetManager targets,
        ICondition conditions, IDataManager data, IPluginLog log, IGameGui gui)
    {
        this.framework = framework;
        this.client = client;
        this.player = player;
        this.objects = objects;
        this.targets = targets;
        this.conditions = conditions;
        this.data = data;
        this.gui = gui;
        pluginInterface = pi;
        ambDiagnostics = pi.GetIpcSubscriber<string>("AutoMarketbuddy.Diagnostics.V1");
        publisher = new(Path.Combine(pi.ConfigDirectory.FullName, "state.json"),
            ex => log.Error(ex, "XIV Companion could not publish state; dashboard will show stale data."));
    }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        worker = Task.Run(publisher.RunAsync);
        framework.Update += OnUpdate;
        return Task.CompletedTask;
    }

    private void OnUpdate(IFramework unused)
    {
        lock (sampleGate) Sample();
    }

    private void Sample()
    {
        if (disposed || framework.IsFrameworkUnloading || Environment.TickCount64 < nextSample) return;
        nextSample = Environment.TickCount64 + 500;
        var currentSequence = Interlocked.Increment(ref sequence);
        try
        {
            // Dalamud wrappers are read only on the framework thread and never retained.
            var loggedIn = client.IsLoggedIn && player.IsLoaded;
            var diagnostics = ReadDiagnostics(loggedIn);
            if (!loggedIn)
            {
                publisher.Publish(Snapshot.Empty(currentSequence, true) with { Diagnostics = diagnostics });
                return;
            }
            var active = new List<string>();
            for (var i = 0; i < conditions.MaxEntries; i++)
                if (conditions[i]) active.Add(((ConditionFlag)i).ToString());
            var local = objects.LocalPlayer;
            var territory = data.GetExcelSheet<TerritoryType>().GetRowOrDefault(client.TerritoryType);
            var location = new Location(client.TerritoryType, territory?.PlaceName.Value.Name.ToString() ?? "",
                client.MapId, client.Instance);
            var currentPlayer = local is null ? null : new Player(ReadActor(local),
                player.CurrentWorld.Value.Name.ToString(), player.HomeWorld.Value.Name.ToString(),
                player.ClassJob.Value.Abbreviation.ToString());
            var target = targets.Target;
            publisher.Publish(new(1, DateTimeOffset.UtcNow, currentSequence, "0.2.0.0", true, true,
                currentPlayer, target is null ? null : ReadActor(target), location, active.ToArray(), Diagnostics: diagnostics));
        }
        catch (Exception ex)
        {
            // A failed sample replaces old state; it never leaves the dashboard apparently connected.
            publisher.Publish(Snapshot.Empty(currentSequence, true, $"State sample failed ({ex.GetType().Name})."));
            nextSample = Environment.TickCount64 + 5000;
        }
    }

    private Diagnostics ReadDiagnostics(bool loggedIn)
    {
        var now = Environment.TickCount64;
        if (now >= nextPluginSample)
        {
            plugins = pluginInterface.InstalledPlugins.Select(p => new PluginHealth(p.Name, p.InternalName,
                p.Version.ToString(), p.Manifest.DalamudApiLevel, p.IsLoaded, p.IsOutdated, p.IsBanned, p.IsDev))
                .OrderBy(p => p.InternalName).ToArray();
            nextPluginSample = now + 2000;
        }
        var visible = loggedIn ? WatchedWindows.Where(name =>
        {
            var addon = gui.GetAddonByName(name);
            return !addon.IsNull && addon.IsReady && addon.IsVisible;
        }).ToArray() : [];
        var ambStatus = "Not loaded";
        string? ambSnapshot = null;
        if (plugins.Any(p => p.InternalName == "AutoMarketbuddy" && p.IsLoaded))
        {
            try
            {
                ambSnapshot = ambDiagnostics.InvokeFunc();
                if (ambSnapshot.Length > 48_000) throw new InvalidOperationException("Diagnostic snapshot exceeds size limit.");
                ambStatus = "Available";
            }
            catch (IpcNotReadyError) { ambStatus = "Loaded; diagnostics endpoint unavailable. Load the updated AMB build."; }
            catch (Exception ex) { ambStatus = $"Diagnostics unavailable ({ex.GetType().Name})."; ambSnapshot = null; }
        }
        return new(pluginInterface.GetDalamudVersion().Version.ToString(), plugins, visible, ambStatus, ambSnapshot);
    }

    private static Actor ReadActor(IGameObject value)
    {
        var character = value as ICharacter;
        var battle = value as IBattleChara;
        var position = value.Position;
        var casting = battle?.IsCasting == true;
        return new(value.Name.ToString(), value.ObjectKind.ToString(), character?.Level,
            character?.CurrentHp, character?.MaxHp, character?.CurrentMp, character?.MaxMp,
            new(position.X, position.Y, position.Z), casting, casting ? battle!.CastActionId : null,
            casting ? battle!.CurrentCastTime : null, casting ? battle!.TotalCastTime : null);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sampleGate)
        {
            if (disposed) return;
            disposed = true;
            framework.Update -= OnUpdate;
            publisher.Publish(Snapshot.Empty(Interlocked.Increment(ref sequence), false));
            publisher.Complete();
        }
        await worker.ConfigureAwait(false);
    }
}
