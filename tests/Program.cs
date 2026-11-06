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
    game.Snapshot = new[] { item, second };
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
    game.Snapshot = new[] { item, second };
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
        game.Snapshot = new[] { item, bad };
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
    game.Snapshot = Enumerable.Range(0, 20).Select(n => item with { Slot = (short)n }).ToArray();
    game.Inventory = game.Snapshot.ToDictionary(x => x.Slot);
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
            Equal("1.1.0.0", doc.GetProperty("AssemblyVersion").GetString());
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
        Equal(new Version(1, 1, 0, 0), assembly.Version);
        if (!catalog.RootElement[0].GetProperty("DownloadLinkInstall").GetString()!.Contains("/v1.1.0/"))
            throw new Exception("Catalog must use the candidate's versioned package URL.");
        // The sale-history fallback changes prices, so its default must stay opt-in.
        var config = File.ReadAllText(Path.Combine(args[1], "AutoMarketbuddy/Configuration.cs"));
        if (!config.Contains("UseSaleHistoryWhenNoListings { get; set; } = false;"))
            throw new Exception("Sale-history fallback must default to off.");
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
    public IReadOnlyList<SaleItem>? Snapshot;
    public Dictionary<short, SaleItem>? Inventory;
    public List<short> OpenedSlots = new();
    private bool closed = true;
    public int BeginCalls;
    public int EndCalls;
    public bool InteropHeld;
    public bool FailBegin;
    public void BeginRun() { BeginCalls++; if (FailBegin) throw new InvalidOperationException("lock unavailable"); InteropHeld = true; }
    public void EndRun() { EndCalls++; InteropHeld = false; }
    public IReadOnlySet<ulong> OwnRetainers { get; } = new HashSet<ulong> { 30, 31 };
    public IReadOnlyList<SaleItem> Capture() => Snapshot ?? new[] { Item };
    public void ValidateSession(SaleItem target)
    {
        if (!SessionValid) throw new InvalidOperationException("Session changed");
    }
    public SaleItem ReadItem(short slot) => Inventory == null ? Item : Inventory[slot];
    public void Open(SaleItem target) { OpenedSlot = target.Slot; OpenedSlots.Add(target.Slot); closed = false; }
    public bool EditorReady(SaleItem target) => Block != "editor";
    public void Compare(SaleItem target) { CompareCalls++; }
    public IReadOnlyList<MarketListing>? ReadComparison(SaleItem target) => Block == "quote" ? null : Listings;
    public IReadOnlyList<HistorySale>? ReadHistory(SaleItem target) { HistoryReads++; return History; }
    public void SetPrice(SaleItem target, uint price) { SetCalls++; Proposed = price; }
    public void Confirm(SaleItem target, uint price) { ConfirmCalls++; }
    public bool EditorClosed => closed;
    public void CancelOwnedEditor() => closed = Block != "close";
    public void Acknowledge()
    {
        if (Inventory == null) Item = Item with { Price = Proposed };
        else Inventory[OpenedSlot] = Inventory[OpenedSlot] with { Price = Proposed };
        closed = true;
    }
}
