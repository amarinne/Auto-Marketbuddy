using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoMarketbuddy.Services;

// One comparison owns one request and all of its pages. No previous response is reusable.
public sealed class ComparisonCapture
{
    // A market with no competing listing sends no offerings packet. The game still opens the
    // result window for the searched item with a zero count. Accept that only after the window has
    // been open for this comparison for a short time, so a slow response is not mistaken for an
    // empty market.
    public const long EmptyResponseGraceMillis = 1_000;
    private readonly uint itemId;
    private readonly Dictionary<ulong, MarketListing> listings = new();
    private int? requestId;

    public ComparisonCapture(uint itemId) => this.itemId = itemId;

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
        // Only a new offerings packet can prove that a comparison with listings received data.
        if (listings.Count != count || requestId == null) return null;
        return listings.Values.ToArray();
    }

    // True when this comparison has a positive empty result: the game searched this item, the
    // request issued by Compare() changed the search proxy, the result window belongs to that
    // request, and it stayed open for the grace period without an offerings packet. A window or
    // proxy state that existed before the request cannot prove an empty market, even for the item
    // that was searched before.
    public bool AcceptsEmptyMarket(uint searchItemId, uint count, long now, long requestStartedAt,
        long windowOpenedAt, bool requestIssued)
    {
        if (searchItemId != itemId || count != 0 || requestId != null) return false;
        if (!requestIssued || windowOpenedAt < requestStartedAt) return false;
        return now - windowOpenedAt >= EmptyResponseGraceMillis;
    }
}
