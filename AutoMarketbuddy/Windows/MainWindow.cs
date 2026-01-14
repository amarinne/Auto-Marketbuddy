using System;
using System.Numerics;
using AutoMarketbuddy.Services;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace AutoMarketbuddy.Windows
{
    public class MainWindow : Window, IDisposable
    {
        private readonly UndercutExecutor executor;
        private readonly Configuration configuration;

        public MainWindow(UndercutExecutor executor, Configuration configuration) 
            : base("Auto-Marketbuddy##AutoMarketbuddyMainWindow", ImGuiWindowFlags.AlwaysAutoResize)
        {
            this.executor = executor;
            this.configuration = configuration;
        }

        public void Dispose()
        {
        }

        public override void Draw()
        {
            if (ImGui.Button(executor.IsRunning ? "Stop Undercutting" : "Start Undercutting"))
            {
                if (executor.IsRunning)
                {
                    executor.Stop();
                }
                else
                {
                    executor.Start();
                }
            }

            ImGui.SameLine();
            ImGui.TextColored(executor.IsRunning ? new Vector4(0, 1, 0, 1) : new Vector4(1, 0, 0, 1), 
                executor.IsRunning ? "RUNNING" : "STOPPED");

            ImGui.Separator();

            ImGui.Text($"State: {executor.CurrentState}");
            if (!string.IsNullOrEmpty(executor.LastError))
            {
                ImGui.TextColored(new Vector4(1, 0, 0, 1), $"Error: {executor.LastError}");
            }

            ImGui.Spacing();
            var verbose = configuration.VerboseLogging;
            if (ImGui.Checkbox("Verbose Logging", ref verbose))
            {
                configuration.VerboseLogging = verbose;
                configuration.Save();
            }
            
            var recordMode = configuration.RecordMode;
            if (ImGui.Checkbox("Record Window States", ref recordMode))
            {
                configuration.RecordMode = recordMode;
                configuration.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Continuously log all window states as you interact with the game.\nUse this to record the exact window state sequence.");
            }

            ImGui.Spacing();
            ImGui.Text("Instructions:");
            ImGui.TextWrapped("1. Open Retainer Sell List.");
            ImGui.TextWrapped("2. Ensure AllaganMarket plugin is loaded.");
            ImGui.TextWrapped("3. Click Start.");
        }
    }
}
