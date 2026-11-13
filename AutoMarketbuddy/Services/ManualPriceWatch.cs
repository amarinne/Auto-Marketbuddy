using System;
using System.Collections.Generic;

namespace AutoMarketbuddy.Services;

// Watches the retainer sale list and its price editor for price changes made by hand or by
// another plugin. Every read is passive: it never clicks, sets, or confirms a price, and it never
// requests market data. A run keeps its own path and reports to the same history store.
public sealed class ManualPriceWatch(IRetainerMarket market, PriceChangeHistory history)
{
    // The game can hide the sale list and reload its container while the price editor is open.
    // Keep reading the edited listing for a short time after the editor closes.
    public const long SettleMillis = 2_000;
    private sealed record ConfirmedEdit(ulong RetainerId, short Slot, uint ItemId, bool IsHq,
        uint Before, uint After);
    private readonly Dictionary<(ulong RetainerId, short Slot), ConfirmedEdit> confirmed = new();
    private SaleItem? edited;
    private long openedAt;
    private long? closedAt;

    public void Update(long now)
    {
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
            }
        }
        else if (edited != null && closedAt == null)
        {
            closedAt = now;
            AcceptConfirmedEdit();
        }

        var settling = edited != null &&
            (closedAt is not long closed || now - closed < SettleMillis);
        if (market.ListVisible || settling)
            foreach (var listing in market.Snapshot())
                if (!settling || listing.RetainerId == edited!.RetainerId)
                    history.Observe(Apply(listing), retainer);
        if (!settling)
        {
            edited = null;
            closedAt = null;
        }
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

    private SaleItem Apply(SaleItem listing)
    {
        var key = (listing.RetainerId, listing.Slot);
        if (!confirmed.TryGetValue(key, out var edit)) return listing;
        if (edit.ItemId != listing.ItemId || edit.IsHq != listing.IsHq || listing.Price == edit.After)
        {
            // The listing was replaced or the container now agrees with the confirmation.
            confirmed.Remove(key);
            return listing;
        }
        if (listing.Price == edit.Before) return listing with { Price = edit.After };
        // A new price is authoritative even when it differs from the confirmed one.
        confirmed.Remove(key);
        return listing;
    }
}
