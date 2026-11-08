using System;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace AutoMarketbuddy.Services;

// Raw IPC keeps Auto-Marketbuddy free of an AutoRetainer assembly dependency. The endpoint names
// and the plugin-name echo match AutoRetainerAPI 1.0.0.4 / AutoRetainer 4.6.2.18.
public sealed class AutoRetainerIpc : IAutoRetainerIpc, IDisposable
{
    public const string RequestEndpoint = "AutoRetainer.RequestPostprocess";
    public const string FinishEndpoint = "AutoRetainer.FinishPostprocessRequest";
    public const string AdditionalTaskEndpoint = "AutoRetainer.OnRetainerAdditionalTask";
    public const string ReadyEndpoint = "AutoRetainer.OnRetainerReadyForPostprocess";
    public const string BusyEndpoint = "AutoRetainer.PluginState.IsBusy";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly string pluginName;
    private readonly ICallGateSubscriber<string, object> additionalTask;
    private readonly ICallGateSubscriber<string, string, object> ready;
    private readonly ICallGateSubscriber<string, object> request;
    private readonly ICallGateSubscriber<object> finish;
    private readonly ICallGateSubscriber<bool> busy;

    public event Action<string>? PostprocessStep;
    public event Action<string>? ReadyToPostprocess;

    public AutoRetainerIpc(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        pluginName = pluginInterface.InternalName;
        additionalTask = pluginInterface.GetIpcSubscriber<string, object>(AdditionalTaskEndpoint);
        ready = pluginInterface.GetIpcSubscriber<string, string, object>(ReadyEndpoint);
        request = pluginInterface.GetIpcSubscriber<string, object>(RequestEndpoint);
        finish = pluginInterface.GetIpcSubscriber<object>(FinishEndpoint);
        busy = pluginInterface.GetIpcSubscriber<bool>(BusyEndpoint);
        additionalTask.Subscribe(OnAdditionalTask);
        ready.Subscribe(OnReady);
    }

    public bool IsAvailable
    {
        get
        {
            try { return pluginInterface.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName == "AutoRetainer"); }
            catch (Exception) { return false; }
        }
    }

    public bool IsBusy
    {
        get
        {
            try { return busy.InvokeFunc(); }
            catch (Exception) { return true; }
        }
    }

    private void OnAdditionalTask(string retainerName) => PostprocessStep?.Invoke(retainerName);

    private void OnReady(string plugin, string retainerName)
    {
        if (plugin == pluginName) ReadyToPostprocess?.Invoke(retainerName);
    }

    public void RequestPostprocess() => request.InvokeAction(pluginName);
    public void FinishPostprocess() => finish.InvokeAction();

    public void Dispose()
    {
        additionalTask.Unsubscribe(OnAdditionalTask);
        ready.Unsubscribe(OnReady);
    }
}
