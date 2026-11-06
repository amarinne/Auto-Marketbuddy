using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoMarketbuddy.Services;

// One comparison owns one request and all of its pages. No previous response is reusable.
public sealed class ComparisonCapture(uint itemId)
{
    private readonly Dictionary<ulong, MarketListing> listings = new();
    private int? requestId;

    public void Add(int id, IReadOnlyList<(ulong Id, MarketListing Listing)> page)
    {
        if (requestId != null && requestId != id)
            throw new InvalidOperationException("Overlapping market requests. Stopped.");
        requestId = id;
        foreach (var (listingId, listing) in page)
        {
            if (listing.ItemId != itemId || listingId == 0)
                throw new InvalidOperationException("Market data belongs to another item.");
            if (listings.TryGetValue(listingId, out var previous) && previous != listing)
                throw new InvalidOperationException("Conflicting market listing data.");
            listings[listingId] = listing;
        }
        if (listings.Count > 100) throw new InvalidOperationException("Too many market listings.");
    }

    public IReadOnlyList<MarketListing>? Complete(uint searchItemId, uint count)
    {
        if (searchItemId != itemId) return null;
        if (count > 100 || listings.Count > count)
            throw new InvalidOperationException("Unexpected market listing count.");
        // The proxy's WaitingForListings field can remain set at an idle retainer.
        // Only a new offerings packet can establish that this comparison received data.
        if (listings.Count != count || requestId == null) return null;
        return listings.Values.ToArray();
    }
}
