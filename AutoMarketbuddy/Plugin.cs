using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using AutoMarketbuddy.Services;
using AutoMarketbuddy.Windows;

namespace AutoMarketbuddy
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "Allagan Undercut";

        private const string CommandName = "/aundercut";

        private IDalamudPluginInterface PluginInterface { get; init; }
        private ICommandManager CommandManager { get; init; }
        public Configuration Configuration { get; init; }
        public UndercutExecutor Executor { get; init; }
        public AtkOrderService AtkOrderService { get; init; }
        public AllaganMarketClient AllaganMarketClient { get; init; }
        public WindowSystem WindowSystem { get; init; }
        public MainWindow MainWindow { get; init; }

        private IGameGui GameGui { get; init; }

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IPluginLog pluginLog,
            IGameGui gameGui,
            IFramework framework,
            IClientState clientState,
            IChatGui chatGui)
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;
            this.GameGui = gameGui;

            this.Configuration = this.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(this.PluginInterface);

            this.AllaganMarketClient = new AllaganMarketClient(this.PluginInterface);
            this.AtkOrderService = new AtkOrderService(gameGui);
            this.Executor = new UndercutExecutor(pluginLog, gameGui, this.AtkOrderService, this.Configuration, this.AllaganMarketClient, clientState, chatGui);

            this.WindowSystem = new WindowSystem("AllaganUndercut");
            this.MainWindow = new MainWindow(this.Executor, this.Configuration);
            this.WindowSystem.AddWindow(this.MainWindow);

            this.PluginInterface.UiBuilder.Draw += DrawUI;
            this.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
            this.PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUI;

            this.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the Allagan Undercut control window"
            });
            
            framework.Update += OnUpdate;
        }

        private void DrawUI()
        {
            this.WindowSystem.Draw();
        }

        private void ToggleMainUI()
        {
            this.MainWindow.IsOpen = !this.MainWindow.IsOpen;
        }
        
        private bool wasRetainerListOpen = false;

        private void OnUpdate(IFramework framework)
        {
            unsafe
            {
                var addonPtr = GameGui.GetAddonByName("RetainerSellList", 1);
                var addon = (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)addonPtr.Address;
                var isRetainerListOpen = addon != null && addon->IsVisible;

                if (isRetainerListOpen && !wasRetainerListOpen)
                {
                    MainWindow.IsOpen = true;
                }
                wasRetainerListOpen = isRetainerListOpen;
            }

            Executor.Update();
        }

        private void OnCommand(string command, string args)
        {
            if (args == "start")
             {
                 Executor.Start();
             }
             else if (args == "stop")
             {
                 Executor.Stop();
             }
             else
             {
                 ToggleMainUI();
             }
        }

        public void Dispose()
        {
            this.PluginInterface.UiBuilder.Draw -= DrawUI;
            this.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUI;
            this.PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUI;
            
            this.WindowSystem.RemoveAllWindows();
            this.CommandManager.RemoveHandler(CommandName);
            Executor.Stop();
        }
    }
}
