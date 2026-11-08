using System;
using AutoMarketbuddy.Services;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace AutoMarketbuddy
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;

        public int UndercutAmount { get; set; } = 1;
        public int WaitDelayMillis { get; set; } = 500;
        public bool SyncWindowWithSellList { get; set; } = true;
        public bool ExitRetainerOnFinish { get; set; } = false;
        public bool UseSaleHistoryWhenNoListings { get; set; } = false;
        public AutoRetainerMode AutoRetainerMode { get; set; } = AutoRetainerMode.Off;

        [NonSerialized]
        private IDalamudPluginInterface? PluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.PluginInterface = pluginInterface;
        }

        public void Save()
        {
            this.PluginInterface!.SavePluginConfig(this);
        }
    }
}
