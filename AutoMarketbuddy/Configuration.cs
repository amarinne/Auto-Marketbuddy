using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace AutoMarketbuddy
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;

        public int UndercutAmount { get; set; } = 1;
        public int WaitDelayMillis { get; set; } = 500;
        public bool CheckRetainerPrice { get; set; } = true;
        public bool VerboseLogging { get; set; } = false;
        public bool RecordMode { get; set; } = false;

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
