using System;
using System.Numerics;
using AutoMarketbuddy.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AutoMarketbuddy.Windows;

public sealed class MainWindow(MarketSession session, Configuration configuration,
    AutoRetainerHandoff autoRetainer, PriceChangeHistory history, Action<bool> start, Action stop,
    Func<uint, string> itemName)
    : Window("Auto-Marketbuddy##AutoMarketbuddyMainWindow", ImGuiWindowFlags.None)
{
    private int lastHistoryCount;
    private int scrollFramesLeft;

    public override void PreDraw()
    {
        ImGui.SetNextWindowSize(new Vector2(460, 520), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(280, 220), new Vector2(float.MaxValue, float.MaxValue));
    }

    public override void OnClose() => stop();

    public override void Draw()
    {
        ImGui.TextUnformatted(session.Status);
        if (session.IsRunning || autoRetainer.IsBusy)
        {
            if (ImGui.Button("Stop")) stop();
        }
        else
        {
            if (ImGui.Button("Preview")) start(false);
            ImGui.SameLine();
            if (ImGui.Button("Apply")) start(true);
        }

        ImGui.BeginDisabled(session.IsRunning);
        ImGui.SetNextItemWidth(90f);
        var undercut = configuration.UndercutAmount;
        if (ImGui.InputInt("Undercut", ref undercut, 0, 0))
        {
            configuration.UndercutAmount = Math.Clamp(undercut, 0, (int)Pricing.MaximumPrice);
            configuration.Save();
        }
        ImGui.EndDisabled();

        var useHistory = configuration.UseSaleHistoryWhenNoListings;
        if (ImGui.Checkbox("Use recent sale price when no listings", ref useHistory))
        {
            configuration.UseSaleHistoryWhenNoListings = useHistory;
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Off (default) leaves the price as-is when the market has no competing listing.");

        var syncWindow = configuration.SyncWindowWithSellList;
        if (ImGui.Checkbox("Open/close with retainer sell list", ref syncWindow))
        {
            configuration.SyncWindowWithSellList = syncWindow;
            configuration.Save();
        }

        var exitOnFinish = configuration.ExitRetainerOnFinish;
        if (ImGui.Checkbox("Exit retainer when finish", ref exitOnFinish))
        {
            configuration.ExitRetainerOnFinish = exitOnFinish;
            configuration.Save();
        }

        var autoRetainerMode = (int)configuration.AutoRetainerMode;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.Combo("During AutoRetainer", ref autoRetainerMode, new[] { "Off", "Preview", "Apply" }, 3))
        {
            configuration.AutoRetainerMode = (AutoRetainerMode)Math.Clamp(autoRetainerMode, 0, 2);
            configuration.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Run when AutoRetainer hands over the retainer. Apply changes prices; Off keeps manual control.");
        if (autoRetainer.IsBusy) ImGui.TextWrapped($"AutoRetainer: {autoRetainer.Status}");

        if (session.LastError.Length > 0) ImGui.TextWrapped($"Stopped: {session.LastError}");
        if (history.Count == 0) return;

        ImGui.Separator();
        ImGui.TextUnformatted($"Price changes ({history.Count})");
        ImGui.SameLine();
        ImGui.BeginDisabled(session.IsRunning);
        if (ImGui.SmallButton("Clear")) history.Clear();
        ImGui.EndDisabled();
        if (!ImGui.BeginChild("##HistoryScroll", new Vector2(0, 0), false)) { ImGui.EndChild(); return; }
        if (ImGui.BeginTable("##History", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();
            // Appended in processing order, so each retainer reads top to bottom as it was shown.
            foreach (var change in history.Changes)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(itemName(change.ItemId) + (change.IsHq ? " HQ" : ""));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(change.ProposedPrice is uint proposed && proposed != change.PreviousPrice
                    ? $"{change.PreviousPrice:N0} -> {proposed:N0}"
                    : $"{change.PreviousPrice:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(change.RetainerName);
            }
            ImGui.EndTable();
        }
        if (history.Count != lastHistoryCount)
        {
            lastHistoryCount = history.Count;
            scrollFramesLeft = 3;
        }
        // Anchor to the end for a few frames: the scroll range only grows
        // after the new row is laid out, so a single SetScrollY clamps short.
        if (scrollFramesLeft > 0)
        {
            ImGui.SetScrollHereY(1.0f);
            scrollFramesLeft--;
        }
        ImGui.EndChild();
    }
}
