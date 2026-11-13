using System;
using System.Collections.Generic;

namespace AutoMarketbuddy.Services;

public enum PriceChangeKind { Baseline, Proposal, Applied }

public sealed record PriceChange(ulong CharacterId, uint WorldId, ulong RetainerId, string RetainerName,
    short Slot, uint ItemId, bool IsHq, uint PreviousPrice, uint? ProposedPrice)
{
    public PriceChangeKind Kind { get; set; }
}

// One listing as the table shows it. An unchanged listing has no previous price.
public sealed record PriceChangeRow(ulong CharacterId, uint WorldId, ulong RetainerId, string RetainerName,
    short Slot, uint ItemId, bool IsHq, uint? PreviousPrice, uint Price, PriceChangeKind Kind);

// Rows with the same retainer, item, quality and price text share one display row.
public sealed record PriceChangeGroup(uint ItemId, bool IsHq, string RetainerName, uint? PreviousPrice,
    uint Price, PriceChangeKind Kind, int Count);

public static class PriceChangeTable
{
    // Keep the first-seen position. A different price or transition stays a separate row.
    public static IReadOnlyList<PriceChangeGroup> Build(IReadOnlyList<PriceChangeRow> rows)
    {
        var groups = new List<PriceChangeGroup>();
        var positions = new Dictionary<(ulong, uint, ulong, uint, bool, uint?, uint, PriceChangeKind), int>();
        foreach (var row in rows)
        {
            var key = (row.CharacterId, row.WorldId, row.RetainerId, row.ItemId, row.IsHq,
                row.PreviousPrice, row.Price, row.Kind);
            if (positions.TryGetValue(key, out var position))
            {
                groups[position] = groups[position] with { Count = groups[position].Count + 1 };
                continue;
            }
            positions[key] = groups.Count;
            groups.Add(new(row.ItemId, row.IsHq, row.RetainerName, row.PreviousPrice, row.Price, row.Kind, 1));
        }
        return groups;
    }
}

// What one observation did to the stored state. Baseline means the listing was seen for the first
// time, so later observations can report a change.
public enum PriceObservation { None, Baseline, Changed }

// One in-memory log for every path that observes a retainer listing price: normal runs, the
// AutoRetainer handoff, manual edits, and a manual market comparison. Recording never reads the
// game or the market board. Each listing keeps one state entry that updates in place, so a
// proposal becomes an applied change without another row. Observations in the same state add
// nothing.
public sealed class PriceChangeHistory(Action<string>? log = null)
{
    public const int Capacity = 500;
    private sealed class ListingState(uint price, uint? proposed, string retainerName, PriceChange? active)
    {
        public uint Price = price;
        public uint? Proposed = proposed;
        public string RetainerName = retainerName;
        public PriceChange? Active = active;
        // Clear hides the row but keeps the price, so the next observation of the same listing
        // is not a new baseline. A later change or proposal shows the row again.
        public bool Hidden;
    }

    private readonly record struct Key(ulong CharacterId, uint WorldId, ulong RetainerId, short Slot,
        uint ItemId, bool IsHq);

    private readonly List<PriceChange> changes = new();
    private readonly Dictionary<Key, ListingState> listings = new();
    private readonly List<Key> order = new();

    public IReadOnlyList<PriceChange> Changes => changes;
    public int Count => changes.Count;

    // Every observed listing in first-seen order, including listings that did not change.
    public IReadOnlyList<PriceChangeRow> Rows
    {
        get
        {
            var rows = new List<PriceChangeRow>(order.Count);
            foreach (var key in order)
            {
                if (!listings.TryGetValue(key, out var state) || state.Hidden) continue;
                rows.Add(state.Active is { } active
                    ? new(key.CharacterId, key.WorldId, key.RetainerId, state.RetainerName, key.Slot,
                        key.ItemId, key.IsHq, active.PreviousPrice, active.ProposedPrice ?? state.Price,
                        active.Kind)
                    : new(key.CharacterId, key.WorldId, key.RetainerId, state.RetainerName, key.Slot,
                        key.ItemId, key.IsHq, null, state.Price, PriceChangeKind.Baseline));
            }
            return rows;
        }
    }

    // Empty the table. Known prices stay as baselines: removing them made the next frame of an
    // open sale list record every listing again, so the table refilled at once.
    public void Clear()
    {
        changes.Clear();
        foreach (var state in listings.Values)
        {
            state.Active = null;
            state.Proposed = null;
            state.Hidden = true;
        }
    }

    // A run reports its decision. A preview keeps the current price; a verified apply moves the
    // baseline to the new price. Equal decisions for the same listing are not recorded again.
    public bool Report(SaleItem item, string retainerName, uint? proposed, bool applied)
    {
        var key = new Key(item.CharacterId, item.WorldId, item.RetainerId, item.Slot, item.ItemId, item.IsHq);
        listings.TryGetValue(key, out var previous);
        var name = Name(retainerName, previous?.RetainerName);
        if (previous != null)
        {
            if (proposed == null && previous.Price == item.Price) return false;
            if (previous.Price == item.Price && previous.Proposed == proposed)
            {
                // The same decision was already recorded. A verified apply still moves the
                // baseline, so a later observation does not write the change again.
                if (applied && proposed.HasValue && previous.Price != proposed.Value)
                {
                    previous.Price = proposed.Value;
                    previous.Proposed = proposed;
                    previous.RetainerName = name;
                    if (previous.Active is { Kind: PriceChangeKind.Proposal } active)
                    {
                        active.Kind = PriceChangeKind.Applied;
                        log?.Invoke($"Price change: {Where(item, name)}, {active.PreviousPrice} -> " +
                            $"{proposed.Value} gil.");
                    }
                }
                return false;
            }
        }
        var change = new PriceChange(item.CharacterId, item.WorldId, item.RetainerId, name, item.Slot,
            item.ItemId, item.IsHq, item.Price, proposed)
        {
            Kind = applied ? PriceChangeKind.Applied : PriceChangeKind.Proposal,
        };
        Add(change, applied ? "Price change" : "Price proposal");
        var price = applied && proposed.HasValue ? proposed.Value : item.Price;
        if (previous == null)
        {
            listings[key] = new ListingState(price, proposed, name, change);
            order.Add(key);
            Trim();
        }
        else
        {
            previous.Price = price;
            previous.Proposed = proposed;
            previous.RetainerName = name;
            previous.Active = change;
            previous.Hidden = false;
        }
        return true;
    }

    // A listing that is already known keeps its last price. A later different price becomes one
    // change row, so manual edits and changes made by other plugins are captured without a fetch.
    // A confirmed edit that matches a recorded proposal turns that proposal into the applied
    // change in place instead of adding a second row.
    public PriceObservation Observe(SaleItem item, string retainerName, bool confirmed = false)
    {
        var key = new Key(item.CharacterId, item.WorldId, item.RetainerId, item.Slot, item.ItemId, item.IsHq);
        listings.TryGetValue(key, out var previous);
        var name = Name(retainerName, previous?.RetainerName);
        if (previous == null)
        {
            listings[key] = new ListingState(item.Price, null, name, null);
            order.Add(key);
            Trim();
            log?.Invoke($"Price baseline: {Where(item, name)} is {item.Price} gil.");
            return PriceObservation.Baseline;
        }
        if (previous.Price == item.Price)
        {
            if (name.Length > 0 && name != previous.RetainerName) previous.RetainerName = name;
            // The container already shows the proposed price. A confirm click proves the
            // proposal was applied, so update the same row.
            if (confirmed && previous.Active is { Kind: PriceChangeKind.Proposal } matching &&
                matching.ProposedPrice == item.Price)
            {
                matching.Kind = PriceChangeKind.Applied;
                previous.Proposed = item.Price;
                log?.Invoke($"Price change: {Where(item, name)}, {matching.PreviousPrice} -> " +
                    $"{item.Price} gil.");
                return PriceObservation.Changed;
            }
            return PriceObservation.None;
        }
        if (confirmed && previous.Active is { Kind: PriceChangeKind.Proposal } proposed &&
            proposed.ProposedPrice == item.Price)
        {
            proposed.Kind = PriceChangeKind.Applied;
            previous.Price = item.Price;
            previous.Proposed = item.Price;
            previous.RetainerName = name;
            log?.Invoke($"Price change: {Where(item, name)}, {proposed.PreviousPrice} -> " +
                $"{item.Price} gil.");
            return PriceObservation.Changed;
        }
        var change = new PriceChange(item.CharacterId, item.WorldId, item.RetainerId, name, item.Slot,
            item.ItemId, item.IsHq, previous.Price, item.Price)
        {
            Kind = PriceChangeKind.Applied,
        };
        Add(change, "Price change");
        previous.Price = item.Price;
        previous.Proposed = item.Price;
        previous.RetainerName = name;
        previous.Active = change;
        previous.Hidden = false;
        return PriceObservation.Changed;
    }

    private void Add(PriceChange change, string kind)
    {
        changes.Add(change);
        if (changes.Count > Capacity) changes.RemoveAt(0);
        log?.Invoke($"{kind}: {Where(change.RetainerName, change.Slot, change.ItemId, change.IsHq)}, " +
            $"{change.PreviousPrice} -> {change.ProposedPrice?.ToString() ?? "unknown"} gil.");
    }

    private void Trim()
    {
        while (order.Count > Capacity)
        {
            listings.Remove(order[0]);
            order.RemoveAt(0);
        }
    }

    // One line carries the evidence a live test needs: retainer, slot, item, and price.
    private static string Where(SaleItem item, string retainerName) =>
        Where(retainerName, item.Slot, item.ItemId, item.IsHq);

    private static string Where(string retainerName, short slot, uint itemId, bool isHq) =>
        $"retainer \"{retainerName}\", slot {slot + 1}, item {itemId} ({(isHq ? "HQ" : "NQ")})";

    private static string Name(string current, string? previous) =>
        current.Length > 0 ? current : previous ?? "";
}
