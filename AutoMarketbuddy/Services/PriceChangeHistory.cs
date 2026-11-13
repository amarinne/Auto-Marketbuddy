using System;
using System.Collections.Generic;

namespace AutoMarketbuddy.Services;

public sealed record PriceChange(ulong CharacterId, uint WorldId, ulong RetainerId, string RetainerName,
    short Slot, uint ItemId, bool IsHq, uint PreviousPrice, uint? ProposedPrice);

// What one observation did to the stored state. Baseline means the listing was seen for the first
// time, so later observations can report a change.
public enum PriceObservation { None, Baseline, Changed }

// One in-memory log for every path that observes a retainer listing price: normal runs, the
// AutoRetainer handoff, and manual edits in the retainer sale list. Recording never reads the game
// or the market board. A listing keeps one baseline, so the same change is not written twice.
public sealed class PriceChangeHistory(Action<string>? log = null)
{
    public const int Capacity = 500;
    private sealed record ListingState(uint Price, uint? Proposed, string RetainerName);
    private readonly record struct Key(ulong CharacterId, uint WorldId, ulong RetainerId, short Slot,
        uint ItemId, bool IsHq);
    private readonly List<PriceChange> changes = new();
    private readonly Dictionary<Key, ListingState> listings = new();

    public IReadOnlyList<PriceChange> Changes => changes;
    public int Count => changes.Count;

    public void Clear()
    {
        changes.Clear();
        listings.Clear();
    }

    // A run reports its decision. A preview keeps the current price; a verified apply moves the
    // baseline to the new price. Equal decisions for the same listing are not recorded again.
    public bool Report(SaleItem item, string retainerName, uint? proposed, bool applied)
    {
        var key = new Key(item.CharacterId, item.WorldId, item.RetainerId, item.Slot, item.ItemId, item.IsHq);
        listings.TryGetValue(key, out var previous);
        var name = Name(retainerName, previous);
        if (previous != null)
        {
            if (proposed == null && previous.Price == item.Price) return false;
            if (previous.Price == item.Price && previous.Proposed == proposed)
            {
                // The same decision was already recorded. A verified apply still moves the
                // baseline, so a later observation does not write the change again.
                if (applied && proposed.HasValue && previous.Price != proposed.Value)
                    listings[key] = new(proposed.Value, proposed, name);
                return false;
            }
        }
        Add(new(item.CharacterId, item.WorldId, item.RetainerId, name, item.Slot, item.ItemId, item.IsHq,
            item.Price, proposed), applied ? "Price change" : "Price proposal");
        listings[key] = new(applied && proposed.HasValue ? proposed.Value : item.Price, proposed, name);
        return true;
    }

    // A listing that is already known keeps its last price. A later different price becomes one
    // change row, so manual edits and changes made by other plugins are captured without a fetch.
    public PriceObservation Observe(SaleItem item, string retainerName)
    {
        var key = new Key(item.CharacterId, item.WorldId, item.RetainerId, item.Slot, item.ItemId, item.IsHq);
        var name = Name(retainerName, listings.TryGetValue(key, out var previous) ? previous : null);
        if (previous == null)
        {
            listings[key] = new(item.Price, null, name);
            log?.Invoke($"Price baseline: {Where(item, name)} is {item.Price} gil.");
            return PriceObservation.Baseline;
        }
        if (previous.Price == item.Price)
        {
            if (name.Length > 0 && name != previous.RetainerName) listings[key] = previous with { RetainerName = name };
            return PriceObservation.None;
        }
        Add(new(item.CharacterId, item.WorldId, item.RetainerId, name, item.Slot, item.ItemId, item.IsHq,
            previous.Price, item.Price), "Price change");
        listings[key] = new(item.Price, item.Price, name);
        return PriceObservation.Changed;
    }

    private void Add(PriceChange change, string kind)
    {
        changes.Add(change);
        if (changes.Count > Capacity) changes.RemoveAt(0);
        log?.Invoke($"{kind}: {Where(change.RetainerName, change.Slot, change.ItemId, change.IsHq)}, " +
            $"{change.PreviousPrice} -> {change.ProposedPrice?.ToString() ?? "unknown"} gil.");
    }

    // One line carries the evidence a live test needs: retainer, slot, item, and price.
    private static string Where(SaleItem item, string retainerName) =>
        Where(retainerName, item.Slot, item.ItemId, item.IsHq);

    private static string Where(string retainerName, short slot, uint itemId, bool isHq) =>
        $"retainer \"{retainerName}\", slot {slot + 1}, item {itemId} ({(isHq ? "HQ" : "NQ")})";

    private static string Name(string current, ListingState? previous) =>
        current.Length > 0 ? current : previous?.RetainerName ?? "";
}
