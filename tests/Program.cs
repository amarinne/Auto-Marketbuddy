using AutoMarketbuddy;
using AutoMarketbuddy.Services;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

var count = 0;
void Check(string name, Action test)
{
    test();
    Console.WriteLine($"PASS {name}");
    count++;
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}");
}
void Throws(Action action)
{
    try { action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Expected rejection");
}
Check("both command names are registered, with /amb as the short entrance", () =>
    Equal("/aundercut,/amb", string.Join(',', PluginCommands.All)));
var item = new SaleItem(100, 20, 30, 7, 42, true, 5, 1000);
MarketListing Listing(uint price, bool hq = true, ulong retainer = 99, uint id = 42, bool mannequin = false)
    => new(id, hq, retainer, price, mannequin);
var own = new HashSet<ulong> { 30, 31 };
PriceDecision Price(params MarketListing[] listings) => Pricing.Decide(item, listings, own, 1);
Check("raw competitor is undercut exactly once", () => Equal<uint?>(899, Price(Listing(900)).Price));
Check("quality, item, own retainers, mannequins and invalid prices are excluded", () =>
    Equal<uint?>(899, Price(Listing(1, false), Listing(2, retainer: 30), Listing(3, retainer: 31),
        Listing(4, id: 43), Listing(5, mannequin: true), Listing(0), Listing(uint.MaxValue), Listing(6, retainer: 0), Listing(900)).Price));
Check("NQ does not compete with HQ", () => Equal<uint?>(499,
    Pricing.Decide(item with { IsHq = false }, new[] { Listing(1), Listing(500, false) }, own, 1).Price));
Check("no competitors leaves the listing unchanged", () => Equal<uint?>(null, Price().Price));
Check("price below target is raised to the target", () => Equal<uint?>(1999, Price(Listing(2000)).Price));
Check("price far below target is raised, not left unchanged", () => Equal<uint?>(899,
    Pricing.Decide(item with { Price = 217 }, new[] { Listing(900) }, own, 1).Price));
Check("already at target is unchanged", () => Equal<uint?>(null, Price(Listing(1001)).Price));
Check("zero undercut raises to match the competitor", () => Equal<uint?>(900,
    Pricing.Decide(item with { Price = 500 }, new[] { Listing(900) }, own, 0).Price));
Check("undercut clamps at one without unsigned wrap", () => Equal<uint?>(1,
    Pricing.Decide(item, new[] { Listing(2) }, own, 999_999_999).Price));
Check("zero undercut matches the competitor", () => Equal<uint?>(900,
    Pricing.Decide(item, new[] { Listing(900) }, own, 0).Price));
Check("negative undercut is rejected", () => Throws(() => Pricing.Decide(item, new[] { Listing(900) }, own, -1)));
Check("maximum legal price remains safe", () => Equal<uint?>(999_999_998,
    Pricing.Decide(item with { Price = 999_999_999 }, new[] { Listing(999_999_999) }, own, 1).Price));

Check("a fresh response completes without the proxy wait flag", () =>
{
    var quote = new ComparisonCapture(42);
    quote.Add(4, new[] { (1UL, Listing(900)) });
    Equal(1, quote.Complete(42, 1)?.Count ?? -1);
});
Check("cached data without a new response is never accepted", () =>
{
    var quote = new ComparisonCapture(42);
    Equal<IReadOnlyList<MarketListing>?>(null, quote.Complete(42, 0));
    Equal<IReadOnlyList<MarketListing>?>(null, quote.Complete(42, 1));
});
Check("all pages of one request are required; duplicate pages are harmless", () =>
{
    var quote = new ComparisonCapture(42);
    quote.Add(4, new[] { (1UL, Listing(900)) });
    Equal<IReadOnlyList<MarketListing>?>(null, quote.Complete(42, 2));
    quote.Add(4, new[] { (1UL, Listing(900)), (2UL, Listing(800)) });
    Equal(2, quote.Complete(42, 2)!.Count);
    Equal<IReadOnlyList<MarketListing>?>(null, quote.Complete(43, 2));
});
Check("overlapping requests and wrong-item packets stop the run", () =>
{
    var quote = new ComparisonCapture(42);
    quote.Add(4, new[] { (1UL, Listing(900)) });
    Throws(() => quote.Add(5, new[] { (2UL, Listing(800)) }));
    Throws(() => new ComparisonCapture(42).Add(4, new[] { (2UL, Listing(800, id: 43)) }));
});
Check("conflicting listings and unexpected counts are rejected", () =>
{
    var quote = new ComparisonCapture(42);
    quote.Add(4, new[] { (1UL, Listing(900)) });
    Throws(() => quote.Add(4, new[] { (1UL, Listing(800)) }));
    Throws(() => quote.Complete(42, 0));
    Throws(() => quote.Complete(42, 101));
});
Check("empty results need a new response packet", () =>
{
    var quote = new ComparisonCapture(42);
    Equal<IReadOnlyList<MarketListing>?>(null, quote.Complete(42, 0));
    quote.Add(4, Array.Empty<(ulong, MarketListing)>());
    Equal(0, quote.Complete(42, 0)!.Count);
});

FakeMarket Fake() => new(item, new[] { Listing(900) });
void Tick(MarketSession session, int number) => session.Update(number * 500L);
Check("Apply reuses Preview without another comparison", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    game.Listings = new[] { Listing(700) };
    session.Start(true, 1, 500, 5000);
    for (var n = 11; n <= 14; n++) Tick(session, n);
    Equal(1, game.CompareCalls);
    Equal(899U, game.Proposed);
    Equal(1, game.ConfirmCalls);
    Equal(2, game.OpenedSlots.Count);
});
Check("cached comparison follows the item to another retainer and slot", () =>
{
    var game = Fake();
    game.Listings = new[] { Listing(1, retainer: 30), Listing(2, retainer: 31), Listing(900) };
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    game.Item = item with { RetainerId = 31, Slot = 3, Price = 1200, Quantity = 8 };
    session.Start(true, 2, 500, 5000);
    for (var n = 11; n <= 14; n++) Tick(session, n);
    Equal(1, game.CompareCalls);
    Equal("7,3", string.Join(',', game.OpenedSlots));
    Equal(898U, game.Proposed);
    Equal(1, game.ConfirmCalls);
});
Check("Preview compares duplicate item and quality listings only once", () =>
{
    var game = Fake();
    var second = item with { Slot = 3, Quantity = 8, Price = 800 };
    game.SnapshotItems = new[] { item, second };
    game.Inventory = new() { [item.Slot] = item, [second.Slot] = second };
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 20; n++) Tick(session, n);
    Equal(1, game.CompareCalls);
    Equal(false, session.IsRunning);
    Equal(2, session.Results.Count);
    Equal<uint?>(899, session.Results[0].ProposedPrice);
    Equal<uint?>(899, session.Results[1].ProposedPrice);
    Equal("7,3", string.Join(',', game.OpenedSlots));
    Equal(0, game.SetCalls);
    Equal(0, game.ConfirmCalls);
});
Check("a new Preview refreshes its cached comparisons", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    game.Listings = new[] { Listing(700) };
    session.Start(false, 1, 500, 5000);
    for (var n = 11; n < 20; n++) Tick(session, n);
    Equal<uint?>(699, session.Results.Single().ProposedPrice);
    game.Listings = new[] { Listing(500) };
    session.Start(true, 1, 500, 10000);
    for (var n = 21; n <= 24; n++) Tick(session, n);
    Equal(2, game.CompareCalls);
    Equal(699U, game.Proposed);
    Equal("Cache", session.GetDiagnostics(12000).ComparisonSource);
    Equal<long?>(5500, session.GetDiagnostics(12000).QuoteAgeMs);
});
Check("cache misses compare in game for a different item or quality", () =>
{
    foreach (var replacement in new[] { item with { ItemId = 43 }, item with { IsHq = false } })
    {
        var game = Fake();
        using var session = new MarketSession(game);
        session.Start(false, 1, 500, 0);
        for (var n = 1; n < 10; n++) Tick(session, n);
        game.Item = replacement;
        game.Listings = new[] { Listing(700, hq: replacement.IsHq, id: replacement.ItemId) };
        session.Start(true, 1, 500, 5000);
        for (var n = 11; n <= 14; n++) Tick(session, n);
        Equal(2, game.CompareCalls);
        Equal(699U, game.Proposed);
        Equal("In-game", session.GetDiagnostics(7000).ComparisonSource);
    }
});
Check("character and world changes cannot reuse an earlier session cache", () =>
{
    foreach (var replacement in new[] { item with { CharacterId = 101 }, item with { WorldId = 21 } })
    {
        var game = Fake();
        using var session = new MarketSession(game);
        session.Start(false, 1, 500, 0);
        for (var n = 1; n < 10; n++) Tick(session, n);
        game.Item = replacement;
        game.Listings = new[] { Listing(700) };
        session.Start(true, 1, 500, 5000);
        for (var n = 11; n <= 14; n++) Tick(session, n);
        Equal(2, game.CompareCalls);
        Equal(699U, game.Proposed);
    }
});
Check("logout or a world transition clears cache even when returning to the same world", () =>
{
    foreach (var context in new[] { (0UL, 0U), (100UL, 21U) })
    {
        var game = Fake();
        using var session = new MarketSession(game);
        session.Start(false, 1, 500, 0);
        for (var n = 1; n < 10; n++) Tick(session, n);
        Equal(1, session.CachedItemCount);
        session.ObserveContext(context.Item1, context.Item2);
        Equal(0, session.CachedItemCount);
        session.ObserveContext(100, 20);
        game.Listings = new[] { Listing(700) };
        session.Start(true, 1, 500, 5000);
        for (var n = 11; n <= 14; n++) Tick(session, n);
        Equal(2, game.CompareCalls);
        Equal(699U, game.Proposed);
    }
});
Check("cached responses are copied and changed listings still stop before confirmation", () =>
{
    var game = Fake();
    var original = new[] { Listing(900) };
    game.Listings = original;
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    original[0] = Listing(500);
    session.Start(true, 1, 500, 5000);
    Tick(session, 11);
    Tick(session, 12);
    Equal(899U, game.Proposed);
    game.Item = item with { Quantity = 4 };
    Tick(session, 13);
    Equal(false, session.IsRunning);
    Equal(0, game.ConfirmCalls);
    Equal(1, game.CompareCalls);
});
Check("Stop cancels cached Apply before confirmation while retaining reusable quotes", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    session.Start(true, 1, 500, 5000);
    Tick(session, 11);
    Tick(session, 12);
    session.Stop();
    Tick(session, 13);
    Equal(0, game.ConfirmCalls);
    Equal(1, session.CachedItemCount);
    session.Dispose();
    Equal(0, session.CachedItemCount);
});
Check("Apply also fills cache for later retainers", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 4; n++) Tick(session, n);
    game.Acknowledge();
    Tick(session, 5);
    Tick(session, 6);
    game.Item = item with { RetainerId = 31, Slot = 2, Price = 1200 };
    session.Start(true, 1, 500, 5000);
    for (var n = 11; n <= 14; n++) Tick(session, n);
    Equal(1, game.CompareCalls);
    Equal(2, game.ConfirmCalls);
    Equal(899U, game.Proposed);
});
Check("Apply fetches again when a comparison reaches five minutes", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    session.ExpireCache(301499);
    Equal(1, session.CachedItemCount);
    session.ExpireCache(301500);
    Equal(0, session.CachedItemCount);
    game.Listings = new[] { Listing(700) };
    session.Start(true, 1, 500, 302000);
    for (var now = 302500; now <= 304000; now += 500) session.Update(now);
    Equal(2, game.CompareCalls);
    Equal(699U, game.Proposed);
    Equal(1, game.ConfirmCalls);
});
Check("cache reuse across retainers does not extend its expiry", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    game.Item = item with { RetainerId = 31, Slot = 3, Price = 1200 };
    session.Start(true, 1, 500, 100000);
    for (var now = 100500; now <= 102000; now += 500) session.Update(now);
    Equal(1, game.CompareCalls);
    game.Acknowledge();
    session.Update(102500);
    session.Update(103000);
    game.Item = item with { RetainerId = 32, Slot = 5, Price = 1300 };
    game.Listings = new[] { Listing(700) };
    session.Start(true, 1, 500, 301500);
    for (var now = 302000; now <= 303500; now += 500) session.Update(now);
    Equal(2, game.CompareCalls);
    Equal(699U, game.Proposed);
});
Check("a cached price expiring before confirmation is refreshed without submitting it", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    session.Start(true, 1, 500, 300000);
    session.Update(300500);
    session.Update(301000);
    Equal(899U, game.Proposed);
    game.Listings = new[] { Listing(700) };
    session.Update(301500);
    Equal(0, game.ConfirmCalls);
    for (var now = 302000; now <= 303500; now += 500) session.Update(now);
    Equal(2, game.CompareCalls);
    Equal(699U, game.Proposed);
    Equal(1, game.ConfirmCalls);
});
Check("preview targets the inventory slot and cannot set or confirm prices", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    Equal((short)7, game.OpenedSlot);
    Equal(false, session.IsRunning);
    Equal("Completed", session.Status);
    Equal<uint?>(899, session.Results.Single().ProposedPrice);
    Equal(0, game.SetCalls);
    Equal(0, game.ConfirmCalls);
});
Check("apply confirms once and waits for the actual listing price", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal(1, game.ConfirmCalls);
    Equal(1, game.SetCalls);
    Equal(0, session.Results.Count);
    Equal(true, session.IsRunning);
    game.Acknowledge();
    Tick(session, 9);
    Tick(session, 10);
    Equal("Completed", session.Status);
    Equal("Local price verified", session.Results.Single().Outcome);
});
Check("Apply raises a listing priced below the target and verifies the new price", () =>
{
    var low = item with { Price = 217 };
    var game = new FakeMarket(low, new[] { Listing(900) });
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal(899U, game.Proposed);
    Equal(1, game.SetCalls);
    Equal(1, game.ConfirmCalls);
    game.Acknowledge();
    Tick(session, 9);
    Tick(session, 10);
    Equal("Completed", session.Status);
    Equal<uint?>(899, session.Results.Single().ProposedPrice);
    Equal("Local price verified", session.Results.Single().Outcome);
});
Check("interop hold spans a run and is released at completion, Stop, and failed Start", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    Equal(true, game.InteropHeld);
    for (var n = 1; n <= 4; n++) Tick(session, n);
    game.Acknowledge();
    Tick(session, 5);
    Tick(session, 6);
    Equal("Completed", session.Status);
    Equal(false, game.InteropHeld);

    session.Start(true, 1, 500, 5000);
    Equal(true, game.InteropHeld);
    session.Stop();
    Equal(false, game.InteropHeld);

    game.FailBegin = true;
    session.Start(true, 1, 500, 10000);
    Equal(false, session.IsRunning);
    Equal(false, game.InteropHeld);
    if (game.EndCalls < 3) throw new Exception("Failed Start did not release the interop hold.");
});
Check("AutoRetainer suppression is held for a run and released only when owned", () =>
{
    var loaded = true;
    var suppressed = false;
    var writes = 0;
    var ar = new AutoRetainerSuppression(() => loaded, () => suppressed, value =>
    {
        writes++;
        suppressed = value;
    });
    ar.Acquire();
    ar.Acquire();
    Equal(true, suppressed);
    Equal(true, ar.IsHeld);
    Equal(1, writes);
    suppressed = false; // AutoRetainer reloaded during the run.
    ar.Acquire();
    Equal(true, suppressed);
    Equal(2, writes);
    ar.Release();
    ar.Release();
    Equal(false, suppressed);
    Equal(false, ar.IsHeld);
    Equal(3, writes);

    suppressed = true;
    ar.Acquire();
    Equal(true, suppressed);
    Equal(false, ar.IsHeld);
    ar.Release();
    Equal(true, suppressed); // Another plugin owns this suppression.

    suppressed = false;
    loaded = false;
    ar.Acquire();
    ar.Release();
    Equal(false, suppressed);
    Equal(3, writes);
});
Check("Stop at every stage prevents every subsequent confirmation", () =>
{
    for (var stopAt = 0; stopAt <= 5; stopAt++)
    {
        var game = Fake();
        using var session = new MarketSession(game);
        session.Start(true, 1, 500, 0);
        for (var n = 1; n <= stopAt; n++) Tick(session, n);
        var submitted = game.ConfirmCalls;
        session.Stop();
        for (var n = 6; n < 100; n++) Tick(session, n);
        Equal(submitted, game.ConfirmCalls);
        Equal(false, session.IsRunning);
    }
});
Check("price or quantity changed before confirmation aborts", () =>
{
    foreach (var replacement in new[] { item with { Price = 999 }, item with { Quantity = 4 }, item with { ItemId = 43 }, item with { IsHq = false } })
    {
        var game = Fake();
        using var session = new MarketSession(game);
        session.Start(true, 1, 500, 0);
        for (var n = 1; n <= 3; n++) Tick(session, n);
        game.Item = replacement;
        Tick(session, 4);
        Equal(false, session.IsRunning);
        Equal(0, game.ConfirmCalls);
    }
});
Check("retainer/world/character or sale list changes cancel even during a delay", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(true, 1, 5000, 0);
    game.SessionValid = false;
    session.Update(1);
    Equal(false, session.IsRunning);
    Equal(0, game.ConfirmCalls);
});
Check("every wait has a wall-clock deadline", () =>
{
    foreach (var block in new[] { "editor", "quote", "ack", "close" })
    {
        var game = Fake();
        game.Block = block;
        using var session = new MarketSession(game);
        session.Start(block != "close", 1, 500, 0);
        for (var n = 1; n <= 5; n++) Tick(session, n);
        session.Update(40_000);
        Equal(false, session.IsRunning);
        if (!session.LastError.Contains("Timed out")) throw new Exception(session.LastError);
        Equal(block == "ack" ? 1 : 0, game.ConfirmCalls);
    }
});
Check("Stop followed by restart cannot retain a previous target or timer", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 3; n++) Tick(session, n);
    session.Stop();
    session.Start(true, 201, 500, 10_000);
    for (var n = 21; n <= 24; n++) Tick(session, n);
    Equal(699U, game.Proposed);
    Equal(1, game.ConfirmCalls);
    Equal(1, game.CompareCalls);
});
Check("disposed controller cannot restart or confirm", () =>
{
    var game = Fake();
    var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 3; n++) Tick(session, n);
    session.Dispose();
    session.Start(true, 1, 500, 0);
    Tick(session, 4);
    Equal(false, session.IsRunning);
    Equal(0, game.ConfirmCalls);
});
Check("unexpected post-confirmation identity change stops without retry", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 4; n++) Tick(session, n);
    game.Item = item with { ItemId = 43 };
    Tick(session, 5);
    Equal(false, session.IsRunning);
    Equal(1, game.ConfirmCalls);
    Equal(0, session.Results.Count);
});
Check("batch cannot advance until the submitted slot is acknowledged", () =>
{
    var second = item with { Slot = 2, Quantity = 8, Price = 1200 };
    var game = Fake();
    game.SnapshotItems = new[] { item, second };
    game.Inventory = new() { [item.Slot] = item, [second.Slot] = second };
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal("7", string.Join(',', game.OpenedSlots));
    game.Acknowledge();
    for (var n = 9; n <= 14; n++) Tick(session, n);
    Equal("7,2", string.Join(',', game.OpenedSlots));
    Equal(2, game.ConfirmCalls);
    game.Acknowledge();
    Tick(session, 15);
    Tick(session, 16);
    Equal("Completed", session.Status);
    Equal(2, session.Results.Count);
});
Check("skip does not submit and duplicate Start cannot change an active run", () =>
{
    var game = Fake();
    game.Listings = Array.Empty<MarketListing>();
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    session.Start(false, 999, 5000, 0);
    Equal(true, session.IsApplying);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal(0, game.ConfirmCalls);
    Equal(0, game.SetCalls);
    Equal("Completed", session.Status);
});
Check("invalid or mixed snapshot is rejected before opening anything", () =>
{
    foreach (var bad in new[] { item with { WorldId = 21 }, item with { RetainerId = 31 },
        item with { CharacterId = 101 }, item with { Price = 0 }, item with { Slot = -1 }, item with { Slot = 20 } })
    {
        var game = Fake();
        game.SnapshotItems = new[] { item, bad };
        using var session = new MarketSession(game);
        session.Start(true, 1, 500, 0);
        Equal(false, session.IsRunning);
        Equal(0, game.OpenedSlots.Count);
    }
});
Check("pricing evidence accounts for each exclusion using the decision filter", () =>
{
    var decision = Price(Listing(900), Listing(1, false), Listing(2, retainer: 30),
        Listing(3, id: 44), Listing(4, mannequin: true), Listing(0), Listing(5, retainer: 0));
    var evidence = decision.Evidence;
    Equal(7, evidence.Received);
    Equal(1, evidence.Eligible);
    Equal(1, evidence.WrongItem);
    Equal(1, evidence.WrongQuality);
    Equal(1, evidence.OwnRetainer);
    Equal(1, evidence.Mannequin);
    Equal(2, evidence.Invalid);
    Equal<uint?>(900, evidence.LowestCompetingPrice);
    Equal<uint?>(899, decision.Price);
});
Check("diagnostic reads expose the wait and deadline without advancing controls", () =>
{
    var game = Fake();
    game.Block = "quote";
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    Tick(session, 1);
    Tick(session, 2);
    var first = session.GetDiagnostics(1100);
    var next = session.GetDiagnostics(2100);
    Equal("WaitQuote", next.Phase);
    Equal<long?>(1000, first.TimeoutRemainingMs - next.TimeoutRemainingMs);
    Equal<short>(7, next.Item!.Slot);
    Equal("000000000000001E", next.Item.RetainerId);
    Equal<long?>(null, next.QuoteAgeMs);
    Equal(1, game.OpenedSlots.Count);
    Equal(0, game.SetCalls);
    Equal(0, game.ConfirmCalls);
    session.Stop();
    Equal(false, session.GetDiagnostics(2200).IsRunning);
    Equal<uint>(42, session.GetDiagnostics(2200).Item!.ItemId);
    Equal<long?>(null, session.GetDiagnostics(2200).TimeoutRemainingMs);
});
Check("preview diagnostics preserve decision evidence and never report submission", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 10; n++) Tick(session, n);
    var diagnostic = session.GetDiagnostics(5000);
    Equal("Completed", diagnostic.Status);
    Equal<uint?>(899, diagnostic.Decision!.Price);
    Equal("Not submitted", diagnostic.Confirmation);
    Equal<long?>(3500, diagnostic.QuoteAgeMs);
    Equal("In-game", diagnostic.ComparisonSource);
    Equal(1, diagnostic.CachedItems);
    Equal(true, diagnostic.Trace.Any(x => x.Phase == "WaitQuote"));
});
Check("pending confirmation remains explicit after Stop and after local verification", () =>
{
    var game = Fake();
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal("Submitted; local acknowledgement pending", session.GetDiagnostics(4000).Confirmation);
    session.Stop();
    Equal("Submitted; local acknowledgement pending", session.GetDiagnostics(4500).Confirmation);
    Equal(true, session.Status.Contains("may still complete"));
    session.Start(true, 1, 500, 5000);
    for (var n = 11; n <= 18; n++) Tick(session, n);
    game.Acknowledge();
    Tick(session, 19);
    Equal("Local price verified; server persistence is not proven", session.GetDiagnostics(9500).Confirmation);
});
Check("diagnostic trace is bounded and a restart clears old decision evidence", () =>
{
    var game = Fake();
    game.SnapshotItems = Enumerable.Range(0, 20).Select(n => item with { Slot = (short)n }).ToArray();
    game.Inventory = game.SnapshotItems.ToDictionary(x => x.Slot);
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n <= 100; n++) Tick(session, n);
    Equal(20, session.Results.Count);
    Equal(30, session.GetDiagnostics(50000).Trace.Length);
    var previous = session.GetDiagnostics(50000).RunId;
    session.Start(false, 1, 500, 51000);
    var restarted = session.GetDiagnostics(51000);
    Equal(false, previous == restarted.RunId);
    Equal<PriceDecision?>(null, restarted.Decision);
    Equal(1, restarted.Trace.Length);
});
Check("empty market leaves the price unchanged by default", () =>
{
    var game = Fake();
    game.Listings = Array.Empty<MarketListing>();
    game.History = new[] { new HistorySale(42, true, 1200, false) };
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0); // Default: history fallback disabled.
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal(0, game.ConfirmCalls);
    Equal(0, game.SetCalls);
    Equal(0, game.HistoryReads);
    Equal("No competing listing of the same quality.", session.Results.Single().Outcome);
    Equal("Completed", session.Status);
});
Check("empty market uses the newest same-quality sale when the fallback is on", () =>
{
    var game = Fake();
    game.Listings = Array.Empty<MarketListing>();
    game.History = new[] { new HistorySale(42, true, 1200, false), new HistorySale(42, true, 4000, false) };
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0, useHistoryFallback: true);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal(1199U, game.Proposed);
    Equal(1, game.SetCalls);
    Equal(1, game.ConfirmCalls);
    Equal("History", session.GetDiagnostics(4000).ComparisonSource);
    game.Acknowledge();
    Tick(session, 9);
    Tick(session, 10);
    Equal("Local price verified", session.Results.Single().Outcome);
});
Check("sale history is filtered by quality and excludes mannequin sales", () =>
{
    var game = Fake();
    game.Listings = Array.Empty<MarketListing>();
    game.History = new[]
    {
        new HistorySale(42, false, 50, false),
        new HistorySale(43, true, 60, false),
        new HistorySale(42, true, 70, true),
        new HistorySale(42, true, 900, false),
    };
    using var session = new MarketSession(game);
    session.Start(false, 2, 500, 0, useHistoryFallback: true);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal<uint?>(898, session.Results.Single().ProposedPrice);
    var decision = session.GetDiagnostics(4000).Decision!;
    Equal(1, decision.Evidence.HistorySales);
    Equal<uint?>(900, decision.Evidence.LatestHistoryPrice);
});
Check("no same-quality sale leaves the price unchanged even with the fallback on", () =>
{
    var game = Fake();
    game.Listings = Array.Empty<MarketListing>();
    game.History = new[] { new HistorySale(42, false, 900, false) };
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0, useHistoryFallback: true);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    Equal(0, game.SetCalls);
    Equal(0, game.ConfirmCalls);
    Equal("No competing listing and no recent same-quality sale.", session.Results.Single().Outcome);
});
Check("missing sale-history packet times out instead of reusing an old price", () =>
{
    var game = Fake();
    game.Listings = Array.Empty<MarketListing>();
    game.History = null;
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0, useHistoryFallback: true);
    for (var n = 1; n <= 5; n++) Tick(session, n);
    Equal(true, session.IsRunning);
    session.Update(40_000);
    Equal(false, session.IsRunning);
    Equal(0, game.ConfirmCalls);
    if (!session.LastError.Contains("Timed out")) throw new Exception(session.LastError);
});
Check("a cached empty market keeps its history price across retainers", () =>
{
    var game = Fake();
    game.Listings = Array.Empty<MarketListing>();
    game.History = new[] { new HistorySale(42, true, 1200, false) };
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0, useHistoryFallback: true);
    for (var n = 1; n <= 8; n++) Tick(session, n);
    game.Item = item with { RetainerId = 31, Slot = 3, Price = 5000 };
    var compareCalls = game.CompareCalls;
    session.Start(true, 1, 500, 5000, useHistoryFallback: true);
    for (var n = 11; n <= 14; n++) Tick(session, n);
    Equal(compareCalls, game.CompareCalls);
    Equal(1199U, game.Proposed);
    Equal("Cache", session.GetDiagnostics(7000).ComparisonSource);
    Equal(1, game.ConfirmCalls);
});
Check("AutoRetainer handoff requests once and runs one apply session", () =>
{
    var game = Fake();
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    Equal(1, f.Ipc.Requests);
    Equal(true, f.Handoff.IsBusy);
    Equal(false, f.Handoff.IsActive);
    f.Ipc.Step("Retainer");
    Equal(1, f.Ipc.Requests);
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    Equal(1, game.SellListOpens);
    f.Handoff.Update(0);
    Equal(true, f.Session.IsRunning);
    for (var n = 1; n <= 4; n++) f.Tick(n * 500L);
    game.Acknowledge();
    for (var n = 5; n <= 10; n++) f.Tick(n * 500L);
    Equal("Completed", f.Session.Status);
    Equal(1, game.ConfirmCalls);
    Equal(1, game.SellListCloses);
    Equal(1, f.Ipc.Finishes);
    Equal(false, f.Handoff.IsBusy);
    Equal(0, f.Errors.Count);
    f.Ipc.Ready("Retainer");
    Equal(1, f.Ipc.Finishes);
});
Check("AutoRetainer handoff preview never sets or confirms a price", () =>
{
    var game = Fake();
    using var f = new HandoffFixture(game, AutoRetainerMode.Preview);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    for (var n = 1; n <= 8; n++) f.Tick(n * 500L);
    Equal("Completed", f.Session.Status);
    Equal(0, game.SetCalls);
    Equal(0, game.ConfirmCalls);
    Equal(1, f.Ipc.Finishes);
    Equal(false, f.Handoff.IsBusy);
});
Check("AutoRetainer handoff releases a duplicate callback and a busy run", () =>
{
    var game = Fake();
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    Equal(1, game.SellListOpens);
    Equal(0, f.Ipc.Finishes);

    var busy = Fake();
    using var g = new HandoffFixture(busy, AutoRetainerMode.Apply);
    g.Ipc.Step("Retainer");
    g.Session.Start(true, 1, 500, 0);
    g.Handoff.ReadyToPostprocess("Retainer", 0);
    Equal(1, g.Ipc.Finishes);
    Equal(0, busy.SellListOpens);
    Equal(false, g.Handoff.IsBusy);
    g.Session.Stop();
});
Check("AutoRetainer handoff rejects a mismatched retainer and missing menu", () =>
{
    var game = Fake();
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer A");
    f.Handoff.ReadyToPostprocess("Retainer B", 0);
    Equal(1, f.Ipc.Finishes);
    Equal(0, game.SellListOpens);
    Equal(false, f.Handoff.IsBusy);

    var late = Fake();
    using var g = new HandoffFixture(late, AutoRetainerMode.Apply);
    late.MenuReady = false;
    g.Ipc.Step("Retainer");
    g.Handoff.ReadyToPostprocess("Retainer", 0);
    g.Handoff.Update(AutoRetainerHandoff.MenuTimeoutMillis);
    Equal(1, g.Ipc.Finishes);
    Equal(0, late.SellListOpens);
    Equal(1, g.Errors.Count);
});
Check("AutoRetainer handoff closes a list that never becomes ready", () =>
{
    var game = Fake();
    game.OpenListOnOpen = false;
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    f.Handoff.Update(AutoRetainerHandoff.ListTimeoutMillis);
    f.Handoff.Update(AutoRetainerHandoff.ListTimeoutMillis);
    f.Handoff.Update(AutoRetainerHandoff.ListTimeoutMillis);
    Equal(1, f.Ipc.Finishes);
    Equal(1, game.SellListCloses);
    Equal(1, f.Errors.Count);
    Equal(false, f.Handoff.IsBusy);
});
Check("AutoRetainer handoff waits for the sale list container", () =>
{
    var game = Fake();
    game.ListContentReady = false;
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    Equal(false, f.Session.IsRunning);
    game.ListContentReady = true;
    f.Handoff.Update(0);
    Equal(true, f.Session.IsRunning);
    f.Handoff.Cancel("Stopped by the user.", 0);
    Equal(1, f.Ipc.Finishes);
});
Check("AutoRetainer handoff skips a retainer with no active listings", () =>
{
    var game = Fake();
    game.SnapshotItems = Array.Empty<SaleItem>();
    game.Inventory = new();
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    Equal("Idle", f.Session.Status);
    Equal(0, game.ConfirmCalls);
    Equal(1, f.Ipc.Finishes);
    Equal(1, game.SellListCloses);
    Equal(0, f.Errors.Count);
});
Check("AutoRetainer handoff stops on a changed listing without retrying", () =>
{
    var game = Fake();
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    f.Tick(500);
    f.Tick(1000);
    game.Item = item with { Quantity = 4 };
    f.Tick(1500);
    Equal(false, f.Session.IsRunning);
    Equal(0, game.ConfirmCalls);
    f.Tick(2000);
    f.Tick(2500);
    Equal(1, f.Ipc.Finishes);
    f.Tick(3000);
    Equal(1, f.Ipc.Finishes);
    Equal(0, game.ConfirmCalls);
    if (!f.Handoff.LastError.Contains("listing changed")) throw new Exception(f.Handoff.LastError);
});
Check("AutoRetainer handoff releases after a failed session start", () =>
{
    var game = Fake();
    game.FailBegin = true;
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    Equal(false, f.Session.IsRunning);
    Equal(1, f.Errors.Count);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    Equal(1, f.Ipc.Finishes);
    Equal(0, game.ConfirmCalls);
    Equal(false, f.Handoff.IsBusy);
});
Check("AutoRetainer handoff releases when the sale list cannot close", () =>
{
    var game = Fake();
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    for (var n = 1; n <= 4; n++) f.Tick(n * 500L);
    game.Acknowledge();
    f.Tick(2500);
    f.Tick(3000);
    game.CloseFails = true;
    f.Tick(3500);
    Equal(1, f.Ipc.Finishes);
    Equal(false, f.Handoff.IsBusy);
    Equal(1, f.Errors.Count);
    f.Tick(4000);
    Equal(1, f.Ipc.Finishes);
});
Check("AutoRetainer handoff Stop and mode-off cancel cleanly", () =>
{
    var game = Fake();
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    f.Tick(500);
    f.Handoff.Cancel("Stopped by the user.", 1000);
    Equal(false, f.Session.IsRunning);
    Equal(1, f.Ipc.Finishes);
    Equal(1, game.SellListCloses);
    f.Tick(2000);
    Equal(1, f.Ipc.Finishes);
    Equal(0, game.ConfirmCalls);

    var mode = Fake();
    using var g = new HandoffFixture(mode, AutoRetainerMode.Apply);
    g.Ipc.Step("Retainer");
    g.Handoff.ReadyToPostprocess("Retainer", 0);
    g.Handoff.Update(0);
    g.Handoff.Update(0);
    g.Settings = new(AutoRetainerMode.Off, 1, 500, false);
    g.Handoff.Update(1000);
    Equal(1, g.Ipc.Finishes);
    Equal(false, g.Handoff.IsBusy);
});
Check("AutoRetainer handoff survives unload, request failure and release failure", () =>
{
    var unavailable = Fake();
    using var f = new HandoffFixture(unavailable, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Ipc.IsAvailable = false;
    f.Handoff.Update(0);
    Equal(false, f.Handoff.IsBusy);
    Equal(0, f.Ipc.Finishes);
    Equal(0, unavailable.SellListOpens);

    var request = Fake();
    using var g = new HandoffFixture(request, AutoRetainerMode.Apply);
    g.Ipc.FailRequest = true;
    g.Ipc.Step("Retainer");
    Equal(1, g.Ipc.Requests);
    Equal(false, g.Handoff.IsBusy);
    Equal(1, g.Errors.Count);
    g.Ipc.FailRequest = false;
    g.Ipc.Step("Retainer");
    Equal(2, g.Ipc.Requests);
    Equal(true, g.Handoff.IsBusy);

    var stopped = Fake();
    using var s = new HandoffFixture(stopped, AutoRetainerMode.Apply);
    s.Ipc.Step("Retainer");
    s.Ipc.Busy = false;
    s.Handoff.Update(0);
    Equal(false, s.Handoff.IsBusy);
    Equal(0, s.Ipc.Finishes);

    var finish = Fake();
    using var h = new HandoffFixture(finish, AutoRetainerMode.Apply);
    h.Ipc.FailFinish = true;
    h.Ipc.Step("Retainer");
    h.Handoff.ReadyToPostprocess("Retainer", 0);
    h.Handoff.Update(0);
    h.Handoff.Update(0);
    for (var n = 1; n <= 4; n++) h.Tick(n * 500L);
    finish.Acknowledge();
    for (var n = 5; n <= 10; n++) h.Tick(n * 500L);
    Equal(1, h.Ipc.Finishes);
    Equal(false, h.Handoff.IsBusy);
    if (!h.Errors.Any(x => x.Contains("release failed"))) throw new Exception("Expected a release failure notice.");
});
Check("AutoRetainer handoff disposes an active session without a second release", () =>
{
    var game = Fake();
    var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    f.Dispose();
    Equal(1, f.Ipc.Finishes);
    Equal(false, f.Handoff.IsBusy);
    Equal(1, game.SellListCloses);
    Equal(0, game.ConfirmCalls);
});
Check("disposing a pending AutoRetainer request does not release another participant", () =>
{
    var game = Fake();
    var f = new HandoffFixture(game, AutoRetainerMode.Apply);
    f.Ipc.Step("Retainer");
    // AutoRetainer can be waiting for another participant before AMB receives Ready.
    f.Dispose();
    Equal(1, f.Ipc.Requests);
    Equal(0, f.Ipc.Finishes);
    Equal(0, game.SellListOpens);
});
Check("AutoRetainer verifies its retainer again before starting the loaded list", () =>
{
    foreach (var selected in new[] { "Other retainer", "" })
    {
        var game = Fake();
        using var f = new HandoffFixture(game, AutoRetainerMode.Apply);
        f.Ipc.Step("Retainer");
        f.Handoff.ReadyToPostprocess("Retainer", 0);
        f.Handoff.Update(0);
        // A retainer change while the list loads must not become the next session's target.
        game.SelectedName = selected;
        f.Handoff.Update(0);
        Equal(false, f.Session.IsRunning);
        Equal(0, game.BeginCalls);
        Equal(0, game.ConfirmCalls);
        f.Handoff.Update(0);
        f.Handoff.Update(0);
        Equal(1, f.Ipc.Finishes);
    }
});
Check("price history records one row for each observed change and no duplicate", () =>
{
    var lines = new List<string>();
    var history = new PriceChangeHistory(lines.Add);
    Equal(PriceObservation.Baseline, history.Observe(item, "Retainer"));
    Equal(0, history.Count);
    Equal(PriceObservation.Changed, history.Observe(item with { Price = 1791 }, "Retainer"));
    Equal(1, history.Count);
    Equal(1000U, history.Changes[0].PreviousPrice);
    Equal<uint?>(1791, history.Changes[0].ProposedPrice);
    Equal("Retainer", history.Changes[0].RetainerName);
    Equal(PriceObservation.None, history.Observe(item with { Price = 1791 }, "Retainer"));
    Equal(1, history.Count);
    // A baseline and a change each write one Information line with retainer, slot, item and price.
    Equal(2, lines.Count);
    if (!lines[0].StartsWith("Price baseline:") || !lines[0].Contains("\"Retainer\"") ||
        !lines[0].Contains("slot 8") || !lines[0].Contains("item 42") || !lines[0].Contains("1000"))
        throw new Exception(lines[0]);
    if (!lines[1].StartsWith("Price change:") || !lines[1].Contains("1000 -> 1791"))
        throw new Exception(lines[1]);

    var preview = new PriceChangeHistory();
    Equal(true, preview.Report(item, "Retainer", 900, applied: false));
    Equal(false, preview.Report(item, "Retainer", 900, applied: false));
    Equal(1, preview.Count);
    Equal<uint?>(900, preview.Changes[0].ProposedPrice);
    // A preview does not change the listing, so a manual move to 900 is still one new change.
    Equal(PriceObservation.Changed, preview.Observe(item with { Price = 900 }, "Retainer"));
    Equal(2, preview.Count);
    Equal(1000U, preview.Changes[1].PreviousPrice);
    Equal<uint?>(900, preview.Changes[1].ProposedPrice);
    // The applied result moves the baseline, so a later observation is not a second record.
    Equal(true, preview.Report(item with { Price = 900 }, "Retainer", 899, applied: true));
    Equal(3, preview.Count);
    Equal(PriceObservation.None, preview.Observe(item with { Price = 899 }, "Retainer"));
    Equal(3, preview.Count);

    // Preview and the same verified apply are one row, and observation stays quiet.
    var applied = new PriceChangeHistory();
    Equal(true, applied.Report(item, "Retainer", 900, applied: false));
    Equal(false, applied.Report(item, "Retainer", 900, applied: true));
    Equal(PriceObservation.None, applied.Observe(item with { Price = 900 }, "Retainer"));
    Equal(1, applied.Count);
});
Check("AutoRetainer handoff writes price changes to the shared history", () =>
{
    var game = Fake();
    var history = new PriceChangeHistory();
    using var f = new HandoffFixture(game, AutoRetainerMode.Apply, history);
    f.Ipc.Step("Retainer");
    f.Handoff.ReadyToPostprocess("Retainer", 0);
    f.Handoff.Update(0);
    f.Handoff.Update(0);
    for (var n = 1; n <= 4; n++) f.Tick(n * 500L);
    game.Acknowledge();
    for (var n = 5; n <= 10; n++) f.Tick(n * 500L);
    Equal("Completed", f.Session.Status);
    Equal(1, history.Count);
    Equal(1000U, history.Changes[0].PreviousPrice);
    Equal<uint?>(899, history.Changes[0].ProposedPrice);
    Equal("Retainer", history.Changes[0].RetainerName);
    // The normal read-only observation after the run must not repeat the same change.
    Equal(PriceObservation.None, history.Observe(item with { Price = 899 }, "Retainer"));
    Equal(1, history.Count);

    // A later run keeps the earlier row and appends in processing order.
    game.Item = item with { RetainerId = 31, Slot = 3, Quantity = 8, Price = 1200 };
    f.Session.Start(true, 1, 500, 6000);
    for (var n = 13; n <= 20; n++) f.Tick(n * 500L);
    game.Acknowledge();
    for (var n = 21; n <= 30; n++) f.Tick(n * 500L);
    Equal(2, history.Count);
    Equal(1200U, history.Changes[1].PreviousPrice);
    Equal<uint?>(899, history.Changes[1].ProposedPrice);
});
Check("processing and history follow the sale list's displayed order", () =>
{
    var top = item with { Slot = 9, Quantity = 1, Price = 5000 };
    var middle = item with { Slot = 2, Quantity = 2, Price = 5100 };
    var bottom = item with { Slot = 5, Quantity = 3, Price = 5200 };
    var game = new FakeMarket(top, new[] { Listing(900) })
    {
        // Inventory order differs from the order that the game shows in the sale list.
        SnapshotItems = new[] { middle, top, bottom },
        Inventory = new() { [top.Slot] = top, [middle.Slot] = middle, [bottom.Slot] = bottom },
        SaleListSlots = new short[] { 5, 9, 2 },
    };
    var history = new PriceChangeHistory();
    using var session = new MarketSession(game, history);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 40; n++) session.Update(n * 500L);
    Equal("5,9,2", string.Join(',', game.OpenedSlots));
    Equal("5,9,2", string.Join(',', session.Results.Select(x => x.Item.Slot)));
    Equal("5,9,2", string.Join(',', history.Changes.Select(x => x.Slot)));
});
Check("listings missing from the displayed order stay last in slot order", () =>
{
    var first = item with { Slot = 9, Quantity = 1 };
    var second = item with { Slot = 2, Quantity = 2 };
    var third = item with { Slot = 5, Quantity = 3 };
    var game = new FakeMarket(first, new[] { Listing(900) })
    {
        SnapshotItems = new[] { first, second, third },
        Inventory = new() { [first.Slot] = first, [second.Slot] = second, [third.Slot] = third },
        SaleListSlots = new short[] { 5 },
    };
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 40; n++) session.Update(n * 500L);
    Equal("5,2,9", string.Join(',', game.OpenedSlots));
});
Check("a manual price edit is one history row when the sale list is hidden by its editor", () =>
{
    // The game hides the sale list while the price editor is open, and a container reload can
    // hide the prices or report the price from before the edit after the editor closes. The old
    // passive loop waited for a ready sale list and read only that container, so it missed both.
    var game = Fake();
    game.SellListOpen = true;
    game.ListContentReady = false; // The container is not readable yet.
    var lines = new List<string>();
    var history = new PriceChangeHistory(lines.Add);
    var watch = new ManualPriceWatch(game, history);

    watch.Update(0);
    Equal(0, history.Count);

    game.EditorOpen = true;
    game.SaleListHidden = true;
    game.Edited = item;
    watch.Update(100); // Baseline from the editor before the edit.
    Equal(0, history.Count);
    Equal(1, lines.Count);
    if (!lines[0].StartsWith("Price baseline:") || !lines[0].Contains("1000")) throw new Exception(lines[0]);

    // The owner confirms 1791. The game reports the confirm, then closes the editor and reloads
    // the container, which still reports the old price.
    game.Confirmed = (item.Slot, 1791, 150);
    game.EditorOpen = false;
    game.SaleListHidden = false;
    game.ListContentReady = true;
    watch.Update(200);
    Equal(1, history.Count);
    Equal(1000U, history.Changes[0].PreviousPrice);
    Equal<uint?>(1791, history.Changes[0].ProposedPrice);

    // A second observation of the same stale container price adds no row and no reverse row.
    watch.Update(300);
    watch.Update(400);
    watch.Update(2_000);
    Equal(1, history.Count);
    Equal(2, lines.Count);
    if (!lines.Any(x => x.StartsWith("Price change:") && x.Contains("1000 -> 1791")))
        throw new Exception(string.Join('|', lines));

    // When the container catches up, the observation is quiet. A later manual change is one row.
    game.Item = item with { Price = 1791 };
    watch.Update(2_500);
    Equal(1, history.Count);
    game.Item = item with { Price = 1500 };
    watch.Update(3_000);
    Equal(2, history.Count);
    Equal(1791U, history.Changes[1].PreviousPrice);
    Equal<uint?>(1500, history.Changes[1].ProposedPrice);

    // An editor close without a confirm click does not record the typed price.
    game.EditorOpen = true;
    game.Edited = game.Item;
    watch.Update(4_000);
    game.EditorOpen = false;
    watch.Update(4_100);
    watch.Update(4_200);
    Equal(2, history.Count);

    // The same store treats an applied run as exactly one row and a later poll as no change.
    var appliedGame = Fake();
    var appliedHistory = new PriceChangeHistory();
    using var session = new MarketSession(appliedGame, appliedHistory);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 4; n++) Tick(session, n);
    appliedGame.Acknowledge();
    Tick(session, 5);
    Tick(session, 6);
    Equal("Completed", session.Status);
    var appliedWatch = new ManualPriceWatch(appliedGame, appliedHistory);
    appliedGame.SellListOpen = true;
    appliedWatch.Update(4000);
    appliedWatch.Update(5000);
    Equal(1, appliedHistory.Count);
    Equal(1000U, appliedHistory.Changes[0].PreviousPrice);
    Equal<uint?>(899, appliedHistory.Changes[0].ProposedPrice);
});
Check("an empty market without an offerings packet continues the run", () =>
{
    var second = item with { Slot = 2, Quantity = 8, Price = 1200, ItemId = 43 };
    var game = Fake();
    game.SnapshotItems = new[] { item, second };
    game.Inventory = new() { [item.Slot] = item, [second.Slot] = second };
    game.EmptyItems = new HashSet<uint> { item.ItemId };
    game.Listings = new[] { Listing(900, id: 43) };
    using var session = new MarketSession(game);
    session.Start(false, 1, 500, 0);
    for (var n = 1; n < 40; n++) Tick(session, n);
    Equal("Completed", session.Status);
    Equal(2, session.Results.Count);
    Equal("No competing listing of the same quality.", session.Results[0].Outcome);
    Equal("7,2", string.Join(',', game.OpenedSlots));
    Equal(0, game.ConfirmCalls);
    Equal(0, game.SetCalls);
});
Check("a stale same-item search does not prove an empty market", () =>
{
    var quote = new ComparisonCapture(42);
    // Fresh window and request: after the grace period the zero count is this comparison's result.
    Equal(true, quote.AcceptsEmptyMarket(42, 0, now: 3_000, requestStartedAt: 1_000,
        windowOpenedAt: 1_500, requestIssued: true));
    // No new request, a window that predates the request, another item, a non-zero count, or a
    // window that is still inside the grace period cannot prove an empty market.
    Equal(false, quote.AcceptsEmptyMarket(42, 0, 3_000, 1_000, 1_500, requestIssued: false));
    Equal(false, quote.AcceptsEmptyMarket(42, 0, 3_000, 1_000, 500, requestIssued: true));
    Equal(false, quote.AcceptsEmptyMarket(43, 0, 3_000, 1_000, 1_500, requestIssued: true));
    Equal(false, quote.AcceptsEmptyMarket(42, 3, 3_000, 1_000, 1_500, requestIssued: true));
    Equal(false, quote.AcceptsEmptyMarket(42, 0, 2_000, 1_000, 1_500, requestIssued: true));
    // An offerings packet still completes normally, including an empty page.
    Equal<IReadOnlyList<MarketListing>?>(null, quote.Complete(42, 0));
    quote.Add(4, Array.Empty<(ulong, MarketListing)>());
    Equal(0, quote.Complete(42, 0)!.Count);
    Equal(false, quote.AcceptsEmptyMarket(42, 0, 3_000, 1_000, 1_500, requestIssued: true));

    // The same state in a run: the request was not reissued, so the item keeps waiting and the
    // run ends on the wall-clock deadline without setting or confirming a price.
    var game = Fake();
    game.EmptyItems = new HashSet<uint> { item.ItemId };
    game.EmptyRequestIssued = false;
    using var session = new MarketSession(game);
    session.Start(true, 1, 500, 0);
    for (var n = 1; n <= 10; n++) Tick(session, n);
    Equal(true, session.IsRunning);
    session.Update(40_000);
    Equal(false, session.IsRunning);
    Equal(0, game.SetCalls);
    Equal(0, game.ConfirmCalls);
    if (!session.LastError.Contains("Timed out")) throw new Exception(session.LastError);
});
Check("a manual comparison records a proposal and a later run reuses its cache", () =>
{
    var game = Fake();
    game.SellListOpen = true;
    game.EditorOpen = true;
    game.Edited = item;
    game.ComparisonWindowOpen = true;
    game.Manual = new ManualComparison(new[] { Listing(1789) }, 1_500);
    var lines = new List<string>();
    var history = new PriceChangeHistory(lines.Add);
    using var session = new MarketSession(game, history);
    var watch = new ManualPriceWatch(game, history, session, () => 1, lines.Add,
        () => !session.IsRunning);
    watch.Update(2_000);
    Equal(1, history.Count);
    Equal(1000U, history.Changes[0].PreviousPrice);
    Equal<uint?>(1788, history.Changes[0].ProposedPrice);
    Equal(PriceChangeKind.Proposal, history.Changes[0].Kind);
    Equal(1, session.CachedItemCount);
    if (!lines.Any(x => x.StartsWith("Comparison captured:") && x.Contains("item 42") &&
            x.Contains("1 competing") && x.Contains("1789")))
        throw new Exception(string.Join('|', lines));

    // The owner closes the editor without a confirmation. A later Preview or Apply run uses the
    // cache and needs no new comparison.
    game.EditorOpen = false;
    game.ComparisonWindowOpen = false;
    session.Start(false, 1, 500, 3_000);
    for (var n = 7; n <= 12; n++) session.Update(n * 500L);
    Equal(0, game.CompareCalls);
    Equal(1, session.Results.Count);
    Equal<uint?>(1788, session.Results[0].ProposedPrice);
    session.Start(true, 1, 500, 7_000);
    for (var n = 15; n <= 18; n++) session.Update(n * 500L);
    Equal(0, game.CompareCalls);
    Equal(1, game.ConfirmCalls);
    Equal(1788U, game.Proposed);
    game.Acknowledge();
    session.Update(9_500);
    session.Update(10_000);
    Equal(1, history.Count);
    Equal(PriceChangeKind.Applied, history.Changes[0].Kind);
    Equal(1, PriceChangeTable.Build(history.Rows).Count);
});
Check("an empty passive comparison is not cached", () =>
{
    var game = Fake();
    game.SellListOpen = true;
    game.EditorOpen = true;
    game.Edited = item;
    game.ComparisonWindowOpen = true;
    game.Manual = new ManualComparison(Array.Empty<MarketListing>(), 1_000);
    var history = new PriceChangeHistory();
    using var session = new MarketSession(game, history);
    var watch = new ManualPriceWatch(game, history, session, () => 1);
    watch.Update(2_000);
    Equal(0, session.CachedItemCount);
    Equal(0, history.Count);
});
Check("the passive path stays inactive while a run is active", () =>
{
    var game = Fake();
    game.SellListOpen = true;
    game.EditorOpen = true;
    game.Edited = item;
    game.ComparisonWindowOpen = true;
    game.Manual = new ManualComparison(new[] { Listing(900) }, 0);
    var history = new PriceChangeHistory();
    using var session = new MarketSession(game, history);
    session.Start(false, 1, 500, 0);
    var watch = new ManualPriceWatch(game, history, session, () => 1, null,
        () => !session.IsRunning);
    watch.Update(1_000);
    Equal(0, game.ManualBegins);
    Equal(0, game.ManualReads);
    Equal(0, history.Count);
    Equal(true, session.IsRunning);
});
Check("unchanged listings appear in the table with their price", () =>
{
    var game = Fake();
    game.SellListOpen = true;
    var history = new PriceChangeHistory();
    var watch = new ManualPriceWatch(game, history);
    watch.Update(100);
    var rows = PriceChangeTable.Build(history.Rows);
    Equal(1, rows.Count);
    Equal(1000U, rows[0].Price);
    Equal<uint?>(null, rows[0].PreviousPrice);
    Equal(PriceChangeKind.Baseline, rows[0].Kind);
    Equal(1, rows[0].Count);
});
Check("identical listing transitions merge into one row with a count", () =>
{
    var history = new PriceChangeHistory();
    for (short slot = 0; slot < 6; slot++)
    {
        var listing = item with { Slot = slot, Price = 1791 };
        history.Observe(listing, "Vires");
        history.Report(listing, "Vires", 1788, applied: false);
    }
    var other = item with { Slot = 6, Price = 1791 };
    history.Observe(other, "Vires");
    history.Report(other, "Vires", 1600, applied: false);
    var rows = PriceChangeTable.Build(history.Rows);
    Equal(2, rows.Count);
    Equal(6, rows[0].Count);
    Equal<uint?>(1791, rows[0].PreviousPrice);
    Equal(1788U, rows[0].Price);
    Equal(PriceChangeKind.Proposal, rows[0].Kind);
    Equal(1, rows[1].Count);
    Equal(1600U, rows[1].Price);
});
Check("a confirmed proposal updates the same row in place", () =>
{
    var history = new PriceChangeHistory();
    Equal(PriceObservation.Baseline, history.Observe(item, "Vires"));
    Equal(true, history.Report(item, "Vires", 1788, applied: false));
    Equal(1, history.Count);
    Equal(PriceChangeKind.Proposal, history.Changes[0].Kind);
    Equal(PriceObservation.Changed, history.Observe(item with { Price = 1788 }, "Vires", confirmed: true));
    Equal(1, history.Count);
    Equal(PriceChangeKind.Applied, history.Changes[0].Kind);
    var rows = PriceChangeTable.Build(history.Rows);
    Equal(1, rows.Count);
    Equal(PriceChangeKind.Applied, rows[0].Kind);
    Equal<uint?>(1000, rows[0].PreviousPrice);
    Equal(1788U, rows[0].Price);
    Equal(PriceObservation.None, history.Observe(item with { Price = 1788 }, "Vires", confirmed: true));
    Equal(1, history.Count);

    // The manual watcher also keeps one row when the confirm click and the proposal agree.
    var game = Fake();
    game.SellListOpen = true;
    game.EditorOpen = true;
    game.Edited = item;
    game.ComparisonWindowOpen = true;
    game.Manual = new ManualComparison(new[] { Listing(1789) }, 1_000);
    var watchHistory = new PriceChangeHistory();
    using var session = new MarketSession(game, watchHistory);
    var watch = new ManualPriceWatch(game, watchHistory, session, () => 1);
    watch.Update(1_500);
    Equal(1, watchHistory.Count);
    Equal(PriceChangeKind.Proposal, watchHistory.Changes[0].Kind);
    game.Confirmed = (item.Slot, 1788, 1_600);
    game.EditorOpen = false;
    game.ComparisonWindowOpen = false;
    watch.Update(1_700);
    Equal(1, watchHistory.Count);
    Equal(PriceChangeKind.Applied, watchHistory.Changes[0].Kind);
});
Check("Clear empties the table while the sale list stays open", () =>
{
    // Live log 2026-11-14 03:23: each Clear click logged every listing as a new baseline and the
    // table refilled on the next frame.
    var game = Fake();
    game.SellListOpen = true;
    var lines = new List<string>();
    var history = new PriceChangeHistory(lines.Add);
    var watch = new ManualPriceWatch(game, history);
    watch.Update(100);
    Equal(true, history.Report(item, "Retainer", 900, applied: false));
    Equal(1, PriceChangeTable.Build(history.Rows).Count);
    var logged = lines.Count;

    history.Clear();
    watch.Update(200);
    watch.Update(300);
    Equal(0, PriceChangeTable.Build(history.Rows).Count);
    Equal(0, history.Count);
    Equal(logged, lines.Count);

    // A later change still compares with the known price and shows one row again.
    game.Item = item with { Price = 950 };
    watch.Update(400);
    var rows = PriceChangeTable.Build(history.Rows);
    Equal(1, rows.Count);
    Equal<uint?>(1000, rows[0].PreviousPrice);
    Equal(950U, rows[0].Price);
    Equal(PriceChangeKind.Applied, rows[0].Kind);

    // The same proposal after Clear is shown again instead of being treated as already recorded.
    history.Clear();
    Equal(true, history.Report(item with { Price = 950 }, "Retainer", 900, applied: false));
    Equal(1, PriceChangeTable.Build(history.Rows).Count);
    // A new listing still appears as a baseline.
    history.Clear();
    Equal(PriceObservation.Baseline, history.Observe(item with { Slot = 3 }, "Retainer"));
    Equal(1, PriceChangeTable.Build(history.Rows).Count);
});
Console.WriteLine($"{count} behavior checks passed.");

if (args.Length == 2)
{
    Check("package, source manifest, assembly and catalog identity agree", () =>
    {
        using var zip = ZipFile.OpenRead(args[0]);
        var names = zip.Entries.Select(x => x.FullName).OrderBy(x => x).ToArray();
        Equal("AutoMarketbuddy.deps.json,AutoMarketbuddy.dll,AutoMarketbuddy.json", string.Join(',', names));
        using var manifest = JsonDocument.Parse(zip.GetEntry("AutoMarketbuddy.json")!.Open());
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "AutoMarketbuddy/AutoMarketbuddy.json")));
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[1], "repo.json")));
        foreach (var doc in new[] { manifest.RootElement, source.RootElement, catalog.RootElement[0] })
        {
            Equal("AutoMarketbuddy", doc.GetProperty("InternalName").GetString());
            Equal("1.1.1.0", doc.GetProperty("AssemblyVersion").GetString());
            Equal(15, doc.GetProperty("DalamudApiLevel").GetInt32());
        }
        Equal(false, manifest.RootElement.GetProperty("CanUnloadAsync").GetBoolean());
        Equal(false, source.RootElement.GetProperty("CanUnloadAsync").GetBoolean());
        using var dll = new MemoryStream();
        using (var entry = zip.GetEntry("AutoMarketbuddy.dll")!.Open()) entry.CopyTo(dll);
        dll.Position = 0;
        using var pe = new PEReader(dll);
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        Equal("AutoMarketbuddy", metadata.GetString(assembly.Name));
        Equal(new Version(1, 1, 1, 0), assembly.Version);
        if (!catalog.RootElement[0].GetProperty("DownloadLinkInstall").GetString()!.Contains("/v1.1.1/"))
            throw new Exception("Catalog must use the release versioned package URL.");
        // The sale-history fallback changes prices, so its default must stay opt-in.
        var config = File.ReadAllText(Path.Combine(args[1], "AutoMarketbuddy/Configuration.cs"));
        if (!config.Contains("UseSaleHistoryWhenNoListings { get; set; } = false;"))
            throw new Exception("Sale-history fallback must default to off.");
        if (!config.Contains("AutoRetainerMode { get; set; } = AutoRetainerMode.Off;"))
            throw new Exception("AutoRetainer automation must default to off.");
    });
}

sealed class FakeMarket(SaleItem item, IReadOnlyList<MarketListing> listings) : IRetainerMarket
{
    public SaleItem Item = item;
    public IReadOnlyList<MarketListing> Listings = listings;
    public IReadOnlyList<HistorySale>? History;
    public int HistoryReads;
    public bool SessionValid = true;
    public string Block = "";
    public int SetCalls;
    public int ConfirmCalls;
    public int CompareCalls;
    public short OpenedSlot = -1;
    public uint Proposed;
    public IReadOnlyList<SaleItem>? SnapshotItems;
    public Dictionary<short, SaleItem>? Inventory;
    public List<short> OpenedSlots = new();
    public bool MenuReady = true;
    public bool OpenListOnOpen = true;
    public bool ListContentReady = true;
    public string SelectedName = "Retainer";
    public int SellListOpens;
    public int SellListCloses;
    public bool SellListOpen;
    public bool CloseFails;
    private bool closed = true;
    public int BeginCalls;
    public int EndCalls;
    public bool InteropHeld;
    public bool FailBegin;
    // Manual-edit state: the price editor owns the listing while it is open, and the sale list
    // can be hidden then. A reload can hide the container for a few frames after it closes.
    public bool EditorOpen;
    public SaleItem? Edited;
    public bool SaleListHidden;
    // Passive comparison state. The fake exposes one captured result so the watcher can read it
    // without a real result window.
    public bool ComparisonWindowOpen { get; set; }
    public ManualComparison? Manual;
    public int ManualBegins;
    public int ManualReads;
    public int ManualEnds;
    // An item with no competing listing sends no offerings packet; the search window and a new
    // request id are the only positive signals. EmptyRequestIssued models a stale proxy state.
    public HashSet<uint> EmptyItems = new();
    public bool EmptyRequestIssued = true;
    private long clock;
    private long compareClock;
    private ComparisonCapture capture = new(item.ItemId);
    public void BeginRun() { BeginCalls++; if (FailBegin) throw new InvalidOperationException("lock unavailable"); InteropHeld = true; }
    public void EndRun() { EndCalls++; InteropHeld = false; }
    public IReadOnlySet<ulong> OwnRetainers { get; } = new HashSet<ulong> { 30, 31 };
    public bool ListVisible => SellListOpen && !SaleListHidden;
    public bool PriceEditorOpen => EditorOpen;
    public void BeginManualComparison(SaleItem target) { ManualBegins++; ManualEnds = 0; }
    public ManualComparison? ReadManualComparison(SaleItem target) { ManualReads++; return Manual; }
    public void EndManualComparison() { ManualEnds++; }
    public SaleItem? EditedListing => EditorOpen ? Edited : null;
    public (short Slot, uint Price, long At)? ConfirmedEdit => Confirmed;
    public (short Slot, uint Price, long At)? Confirmed;
    public bool ListReady => ListVisible && ListContentReady;
    public bool HasListings => Capture().Count > 0;
    public bool SellListClosed => !SellListOpen;
    public string SelectedRetainerName => SelectedName;
    public bool RetainerMenuReady => MenuReady;
    public void OpenSellList()
    {
        SellListOpens++;
        MenuReady = false;
        SellListOpen = OpenListOnOpen;
    }
    public void CloseSellList()
    {
        SellListCloses++;
        if (CloseFails) throw new InvalidOperationException("close failed");
        SellListOpen = false;
        MenuReady = true;
    }
    public IReadOnlyList<SaleItem> Capture() => SnapshotItems ?? new[] { Item };
    public IReadOnlyList<short> SaleListSlots { get; set; } = Array.Empty<short>();
    public void ValidateSession(SaleItem target)
    {
        if (!SessionValid) throw new InvalidOperationException("Session changed");
    }
    public SaleItem ReadItem(short slot) => Inventory == null ? Item : Inventory[slot];
    public IReadOnlyList<SaleItem> Snapshot()
    {
        // The container can stay readable while the sale list addon is hidden behind the editor.
        if (!ListContentReady) return Array.Empty<SaleItem>();
        return Capture();
    }
    public void Open(SaleItem target) { OpenedSlot = target.Slot; OpenedSlots.Add(target.Slot); closed = false; }
    public bool EditorReady(SaleItem target) => Block != "editor";
    public void Compare(SaleItem target)
    {
        CompareCalls++;
        capture = new(target.ItemId);
        compareClock = clock;
    }
    public IReadOnlyList<MarketListing>? ReadComparison(SaleItem target)
    {
        if (Block == "quote") return null;
        clock += 500;
        if (!EmptyItems.Contains(target.ItemId)) return Listings;
        var windowAt = compareClock + 200;
        return capture.AcceptsEmptyMarket(target.ItemId, 0, clock, compareClock, windowAt, EmptyRequestIssued)
            ? Array.Empty<MarketListing>()
            : null;
    }
    public IReadOnlyList<HistorySale>? ReadHistory(SaleItem target) { HistoryReads++; return History; }
    public void SetPrice(SaleItem target, uint price) { SetCalls++; Proposed = price; }
    public void Confirm(SaleItem target, uint price) { ConfirmCalls++; }
    public bool EditorClosed => closed && !EditorOpen;
    public void CancelOwnedEditor() => closed = Block != "close";
    public void Acknowledge()
    {
        if (Inventory == null) Item = Item with { Price = Proposed };
        else Inventory[OpenedSlot] = Inventory[OpenedSlot] with { Price = Proposed };
        closed = true;
    }
}

sealed class FakeAutoRetainerIpc : IAutoRetainerIpc
{
    public bool IsAvailable { get; set; } = true;
    public bool Busy { get; set; } = true;
    public bool IsBusy => Busy;
    public bool FailRequest { get; set; }
    public bool FailFinish { get; set; }
    public int Requests { get; private set; }
    public int Finishes { get; private set; }
    public event Action<string>? PostprocessStep;
    public event Action<string>? ReadyToPostprocess;

    public void RequestPostprocess()
    {
        Requests++;
        if (FailRequest) throw new InvalidOperationException("request unavailable");
    }

    public void FinishPostprocess()
    {
        Finishes++;
        if (FailFinish) throw new InvalidOperationException("finish unavailable");
    }

    public void Step(string retainerName) => PostprocessStep?.Invoke(retainerName);
    public void Ready(string retainerName) => ReadyToPostprocess?.Invoke(retainerName);
}

sealed class HandoffFixture : IDisposable
{
    public readonly FakeMarket Game;
    public readonly FakeAutoRetainerIpc Ipc = new();
    public readonly MarketSession Session;
    public readonly AutoRetainerHandoff Handoff;
    public readonly List<string> Logs = new();
    public readonly List<string> Errors = new();
    public AutoRetainerSettings Settings;

    public HandoffFixture(FakeMarket game, AutoRetainerMode mode, PriceChangeHistory? history = null)
    {
        Game = game;
        Session = new MarketSession(game, history);
        Settings = new AutoRetainerSettings(mode, 1, 500, false);
        Handoff = new AutoRetainerHandoff(Session, game, Ipc, () => Settings, () => true,
            message => Logs.Add(message), message => Errors.Add(message));
    }

    public void Tick(long now)
    {
        Session.Update(now);
        Handoff.Update(now);
    }

    public void Dispose()
    {
        Handoff.Dispose();
        Session.Dispose();
    }
}
