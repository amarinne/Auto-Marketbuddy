using System;
using System.Collections.Generic;

namespace AutoMarketbuddy.Services;

// The captured comparison from the open market result window, with the time its capture started.
public sealed record ManualComparison(MarketListing[] Listings, long CapturedAt);

// Watches the retainer sale list, its price editor, and a manual market comparison. Every read is
// passive: it never clicks, sets, or confirms a price, and it never requests market data. A run
// keeps its own path and reports to the same history store. The passive path is inactive while a
// run or the AutoRetainer handoff is active, and it uses its own comparison queue.
public sealed class ManualPriceWatch(IRetainerMarket market, PriceChangeHistory history,
    MarketSession? session = null, Func<int>? undercut = null, Action<string>? log = null,
    Func<bool>? canObserve = null)
{
    // The game can hide the sale list and reload its container while the price editor is open.
    // Keep reading the edited listing for a short time after the editor closes.
    public const long SettleMillis = 2_000;
    private sealed record ConfirmedEdit(ulong RetainerId, short Slot, uint ItemId, bool IsHq,
        uint Before, uint After);
    private sealed record PendingComparison(ulong RetainerId, short Slot, uint ItemId, bool IsHq);
    private readonly Dictionary<(ulong RetainerId, short Slot), ConfirmedEdit> confirmed = new();
    private SaleItem? edited;
    private long openedAt;
    private long? closedAt;
    private PendingComparison? comparison;
    private bool comparisonRecorded;
    private bool suspended;

    public void Update(long now)
    {
        if (canObserve != null && !canObserve())
        {
            // A run owns the market windows and its own packet queue. Do not observe or read them.
            if (!suspended)
            {
                suspended = true;
                ResetComparison();
                edited = null;
                closedAt = null;
            }
            return;
        }
        suspended = false;
        var retainer = market.SelectedRetainerName;
        if (market.PriceEditorOpen)
        {
            // The price editor is the only direct signal for an edit that the sale list may hide.
            // Read the listing before the edit so the old price is the baseline.
            if (market.EditedListing is { } listing)
            {
                if (edited == null || edited != listing)
                {
                    edited = listing;
                    openedAt = now;
                }
                closedAt = null;
                history.Observe(listing, retainer);
                CaptureComparison(listing, retainer, now);
            }
        }
        else
        {
            ResetComparison();
            if (edited != null && closedAt == null)
            {
                closedAt = now;
                AcceptConfirmedEdit();
            }
        }

        var settling = edited != null &&
            (closedAt is not long closed || now - closed < SettleMillis);
        if (market.ListVisible || settling)
            foreach (var listing in market.Snapshot())
                if (!settling || listing.RetainerId == edited!.RetainerId)
                {
                    var (applied, wasConfirmed) = Apply(listing);
                    history.Observe(applied, retainer, wasConfirmed);
                }
        if (!settling)
        {
            edited = null;
            closedAt = null;
        }
    }

    // A manual Compare Prices result for the edited listing. Record the decision before the owner
    // confirms anything, then store the listings for a later run.
    private void CaptureComparison(SaleItem listing, string retainerName, long now)
    {
        if (session == null || undercut == null || !market.ComparisonWindowOpen)
        {
            ResetComparison();
            return;
        }
        if (comparison == null || comparison.RetainerId != listing.RetainerId ||
            comparison.Slot != listing.Slot || comparison.ItemId != listing.ItemId ||
            comparison.IsHq != listing.IsHq)
        {
            ResetComparison();
            comparison = new(listing.RetainerId, listing.Slot, listing.ItemId, listing.IsHq);
            market.BeginManualComparison(listing);
        }
        if (comparisonRecorded) return;
        if (market.ReadManualComparison(listing) is not { } captured) return;
        comparisonRecorded = true;
        var amount = Math.Clamp(undercut(), 0, (int)Pricing.MaximumPrice);
        var decision = Pricing.Decide(listing, captured.Listings, market.OwnRetainers, amount);
        log?.Invoke($"Comparison captured: item {listing.ItemId} " +
            $"({(listing.IsHq ? "HQ" : "NQ")}), {captured.Listings.Length} competing listings, " +
            $"lowest eligible {decision.Evidence.LowestCompetingPrice?.ToString() ?? "none"} gil.");
        if (decision.Price is uint proposed)
            history.Report(listing, retainerName, proposed, applied: false);
        // An empty market or a market without an eligible competitor has no usable comparison.
        // Do not cache it: a cached entry has no sale history and would hide the normal
        // empty-market path from a later run.
        if (decision.Evidence.Eligible > 0)
            session.OfferManualComparison(listing, captured.Listings, captured.CapturedAt, now);
    }

    private void ResetComparison()
    {
        if (comparison == null) return;
        comparison = null;
        comparisonRecorded = false;
        market.EndManualComparison();
    }

    // The game reported a confirm click in the price editor. When the listing container still
    // shows the price from before the edit, the confirmed price is the only evidence of the
    // change; the container wins as soon as it reports a different value.
    private void AcceptConfirmedEdit()
    {
        if (edited == null) return;
        if (market.ConfirmedEdit is not { } confirm) return;
        if (confirm.At < openedAt || confirm.Slot != edited.Slot || confirm.Price == edited.Price) return;
        confirmed[(edited.RetainerId, edited.Slot)] = new(edited.RetainerId, edited.Slot, edited.ItemId,
            edited.IsHq, edited.Price, confirm.Price);
    }

    private (SaleItem Listing, bool Confirmed) Apply(SaleItem listing)
    {
        var key = (listing.RetainerId, listing.Slot);
        if (!confirmed.TryGetValue(key, out var edit)) return (listing, false);
        if (edit.ItemId != listing.ItemId || edit.IsHq != listing.IsHq)
        {
            // The listing was replaced.
            confirmed.Remove(key);
            return (listing, false);
        }
        if (listing.Price == edit.After)
        {
            // The container now agrees with the confirmation.
            confirmed.Remove(key);
            return (listing, true);
        }
        if (listing.Price == edit.Before) return (listing with { Price = edit.After }, true);
        // A new price is authoritative even when it differs from the confirmed one.
        confirmed.Remove(key);
        return (listing, false);
    }
}
