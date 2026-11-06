using System;
using System.Collections.Generic;
using System.Linq;

namespace AutoMarketbuddy.Services;

public sealed record SaleItem(ulong CharacterId, uint WorldId, ulong RetainerId, short Slot,
    uint ItemId, bool IsHq, uint Quantity, uint Price);

public sealed record MarketListing(uint ItemId, bool IsHq, ulong RetainerId, uint Price, bool IsMannequin);
// SalePrice in the history packet is already a unit price; it is not divided by quantity.
public sealed record HistorySale(uint ItemId, bool IsHq, uint UnitPrice, bool OnMannequin);
public sealed record PricingEvidence(int Received, int Eligible, int WrongItem, int WrongQuality,
    int Mannequin, int OwnRetainer, int Invalid, uint? LowestCompetingPrice,
    int HistorySales = 0, uint? LatestHistoryPrice = null);
public sealed record PriceDecision(uint? Price, string Reason, PricingEvidence Evidence);
public sealed record ItemResult(SaleItem Item, uint? ProposedPrice, string Outcome);
public sealed record SessionEvent(long ElapsedMs, string Phase, string Message);
public sealed record DiagnosticListing(uint WorldId, string RetainerId, short Slot, uint ItemId,
    bool IsHq, uint Quantity, uint Price);
public sealed record SessionDiagnostics(string RunId, bool IsRunning, string Mode, string Phase,
    string Status, string WaitReason, long? TimeoutRemainingMs, long? DelayRemainingMs,
    int Completed, int Total, DiagnosticListing? Item, int Undercut, PriceDecision? Decision,
    long? QuoteAgeMs, string Confirmation, string LastError, SessionEvent[] Trace,
    string ComparisonSource = "None", int CachedItems = 0);

public static class Pricing
{
    public const uint MaximumPrice = 999_999_999;

    public static PriceDecision Decide(SaleItem item, IReadOnlyList<MarketListing> listings,
        IReadOnlySet<ulong> ownRetainers, int undercut, IReadOnlyList<HistorySale>? history = null)
    {
        if (undercut < 0 || undercut > MaximumPrice || item.Price is 0 or > MaximumPrice)
            throw new InvalidOperationException("Invalid price or undercut amount.");

        uint? lowest = null;
        var eligible = 0;
        var wrongItem = 0;
        var wrongQuality = 0;
        var mannequin = 0;
        var own = 0;
        var invalid = 0;
        // Each excluded listing has one reason. The decision and diagnostic counts use the same filter.
        foreach (var listing in listings)
        {
            if (listing.ItemId != item.ItemId) wrongItem++;
            else if (listing.IsHq != item.IsHq) wrongQuality++;
            else if (listing.IsMannequin) mannequin++;
            else if (listing.RetainerId == item.RetainerId || ownRetainers.Contains(listing.RetainerId)) own++;
            else if (listing.RetainerId == 0 || listing.Price is 0 or > MaximumPrice) invalid++;
            else
            {
                eligible++;
                if (lowest == null || listing.Price < lowest) lowest = listing.Price;
            }
        }
        var evidence = new PricingEvidence(listings.Count, eligible, wrongItem, wrongQuality, mannequin, own, invalid, lowest);
        if (lowest == null)
        {
            // No live listing to undercut. Optionally fall back to the newest completed same-quality sale.
            if (history == null)
                return new(null, "No competing listing of the same quality.", evidence);
            var recent = history.Where(x => x.ItemId == item.ItemId && x.IsHq == item.IsHq &&
                    !x.OnMannequin && x.UnitPrice is > 0 and <= MaximumPrice)
                .ToArray();
            evidence = evidence with { HistorySales = recent.Length, LatestHistoryPrice = recent.FirstOrDefault()?.UnitPrice };
            if (recent.Length == 0)
                return new(null, "No competing listing and no recent same-quality sale.", evidence);
            var recentTarget = (uint)Math.Max(1L, (long)recent[0].UnitPrice - undercut);
            if (recentTarget == item.Price)
                return new(null, "Current price already matches the sale-history target.", evidence);
            return new(recentTarget, "No live listing; undercut the newest same-quality sale once.", evidence);
        }

        var target = (uint)Math.Max(1L, (long)lowest.Value - undercut);
        if (target == item.Price)
            return new(null, "Current price is already at the target.", evidence);
        return target < item.Price
            ? new(target, "Undercut the lowest competing unit price once.", evidence)
            : new(target, "Raise to the lowest competing unit price minus the undercut.", evidence);
    }
}

// All methods run on the framework thread. Only Confirm may submit a price change.
public interface IRetainerMarket
{
    void BeginRun();
    void EndRun();
    IReadOnlyList<SaleItem> Capture();
    IReadOnlySet<ulong> OwnRetainers { get; }
    void ValidateSession(SaleItem item);
    SaleItem ReadItem(short slot);
    void Open(SaleItem item);
    bool EditorReady(SaleItem item);
    void Compare(SaleItem item);
    IReadOnlyList<MarketListing>? ReadComparison(SaleItem item);
    IReadOnlyList<HistorySale>? ReadHistory(SaleItem item);
    void SetPrice(SaleItem item, uint price);
    void Confirm(SaleItem item, uint price);
    bool EditorClosed { get; }
    void CancelOwnedEditor();
}

public sealed class MarketSession(IRetainerMarket market) : IDisposable
{
    public const long CacheLifetimeMillis = 5 * 60 * 1000;
    private sealed record CachedComparison(MarketListing[] Listings, HistorySale[] History, long CapturedAt);
    private readonly Dictionary<(uint ItemId, bool IsHq), CachedComparison> cache = new();
    private ulong cacheCharacter;
    private uint cacheWorld;
    private enum Step { Idle, Open, WaitEditor, WaitQuote, Confirm, WaitApplied, WaitClosed }
    private Step step;
    private IReadOnlyList<SaleItem> items = Array.Empty<SaleItem>();
    private readonly List<ItemResult> results = new();
    private volatile ItemResult[] resultSnapshot = Array.Empty<ItemResult>();
    private int index;
    private int undercut;
    private int delay;
    private bool apply;
    private bool useHistory;
    private uint target;
    private long deadline;
    private long readyAt;
    private bool disposed;
    private readonly Queue<SessionEvent> trace = new();
    private string runId = "";
    private long startedAt;
    private long lastNow;
    private SaleItem? diagnosticItem;
    private PriceDecision? decision;
    private long? quoteAt;
    private string comparisonSource = "None";
    private string confirmation = "Not submitted";

    public bool IsRunning => step != Step.Idle;
    public bool IsApplying => IsRunning && apply;
    public string Status { get; private set; } = "Idle";
    public string LastError { get; private set; } = "";
    public IReadOnlyList<ItemResult> Results => resultSnapshot;
    public int CachedItemCount => cache.Count;

    public void ObserveContext(ulong characterId, uint worldId)
    {
        if (characterId == cacheCharacter && worldId == cacheWorld) return;
        cache.Clear();
        cacheCharacter = characterId;
        cacheWorld = worldId;
    }

    public void ExpireCache(long now)
    {
        foreach (var key in cache.Where(x => now < x.Value.CapturedAt ||
                     now - x.Value.CapturedAt >= CacheLifetimeMillis).Select(x => x.Key).ToArray())
            cache.Remove(key);
    }

    // This copies managed state only. Reading diagnostics cannot advance a run or touch the game.
    public SessionDiagnostics GetDiagnostics(long now) => new(runId, IsRunning,
        runId.Length == 0 ? "None" : apply ? "Apply" : "Preview", step.ToString(), Status,
        step switch
        {
            Step.Open => "Waiting to open the captured inventory slot.",
            Step.WaitEditor => "Waiting for the matching price editor.",
            Step.WaitQuote => "Waiting for a complete, fresh comparison for this item.",
            Step.Confirm => "Waiting to revalidate and submit one price confirmation.",
            Step.WaitApplied => "Confirmation submitted; waiting for the local price and editor closure.",
            Step.WaitClosed => "Waiting for owned market windows to close.",
            _ => LastError.Length > 0 ? LastError : Status,
        }, IsRunning ? Math.Max(0, deadline - now) : null, IsRunning ? Math.Max(0, readyAt - now) : null,
        results.Count, items.Count == 0 ? capturedCount : items.Count,
        diagnosticItem is { } item ? new(item.WorldId, item.RetainerId.ToString("X16"), item.Slot,
            item.ItemId, item.IsHq, item.Quantity, item.Price) : null, undercut,
        decision, quoteAt is long captured ? Math.Max(0, now - captured) : null, confirmation,
        LastError, trace.ToArray(), comparisonSource, cache.Count);

    private int capturedCount;

    private void Record(string message)
    {
        trace.Enqueue(new(Math.Max(0, lastNow - startedAt), step.ToString(), message));
        while (trace.Count > 30) trace.Dequeue();
    }

    public void Start(bool applyPrices, int undercutAmount, int waitDelayMillis, long now,
        bool useHistoryFallback = false)
    {
        if (disposed || IsRunning) return;
        runId = Guid.NewGuid().ToString("N");
        startedAt = lastNow = now;
        trace.Clear();
        diagnosticItem = null;
        decision = null;
        quoteAt = null;
        comparisonSource = "None";
        confirmation = "Not submitted";
        capturedCount = 0;
        apply = applyPrices;
        useHistory = useHistoryFallback;
        undercut = undercutAmount;
        LastError = "";
        results.Clear();
        resultSnapshot = Array.Empty<ItemResult>();
        try
        {
            if (undercutAmount < 0 || undercutAmount > Pricing.MaximumPrice)
                throw new InvalidOperationException("Undercut must be between 0 and 999,999,999 gil.");
            market.BeginRun();
            items = market.Capture();
            capturedCount = items.Count;
            if (items.Count == 0) throw new InvalidOperationException("The retainer has no active listings.");
            if (items.Any(x => x.Price is 0 or > Pricing.MaximumPrice || x.Quantity == 0 ||
                    x.CharacterId == 0 || x.WorldId == 0 || x.RetainerId == 0 || x.ItemId == 0 || x.Slot is < 0 or >= 20) ||
                items.Select(x => x.Slot).Distinct().Count() != items.Count)
                throw new InvalidOperationException("The retainer listings are not ready.");
            var first = items[0];
            if (items.Any(x => x.CharacterId != first.CharacterId || x.WorldId != first.WorldId ||
                    x.RetainerId != first.RetainerId))
                throw new InvalidOperationException("The retainer changed during capture.");
            ObserveContext(first.CharacterId, first.WorldId);
            ExpireCache(now);
            if (!apply)
                foreach (var item in items) cache.Remove((item.ItemId, item.IsHq));
            delay = Math.Clamp(waitDelayMillis, 250, 5000);
            index = 0;
            Move(Step.Open, now);
        }
        catch (Exception ex) { Fail(ex); }
    }

    public void Update(long now)
    {
        if (!IsRunning) return;
        lastNow = now;
        try
        {
            var item = items[index];
            market.ValidateSession(item);
            if (now >= deadline) throw new TimeoutException($"Timed out in {step}. Stopped without retrying.");
            if (now < readyAt) return;
            switch (step)
            {
                case Step.Open:
                    RequireUnchanged(item);
                    if (!market.EditorClosed) return;
                    market.Open(item);
                    Move(Step.WaitEditor, now);
                    break;
                case Step.WaitEditor:
                    RequireUnchanged(item);
                    if (!market.EditorReady(item)) return;
                    ExpireCache(now);
                    if (cache.TryGetValue((item.ItemId, item.IsHq), out var cached))
                    {
                        UseComparison(item, cached, true, now);
                        break;
                    }
                    market.Compare(item);
                    Move(Step.WaitQuote, now);
                    break;
                case Step.WaitQuote:
                    RequireUnchanged(item);
                    if (!market.EditorReady(item)) throw new InvalidOperationException("The price editor was closed.");
                    var listings = market.ReadComparison(item);
                    if (listings == null) return;
                    // An empty market has no undercut target. Only then wait for the sale-history packet,
                    // and only while the fallback is enabled; otherwise this item is left unchanged.
                    HistorySale[] history = Array.Empty<HistorySale>();
                    if (useHistory && listings.Count == 0)
                    {
                        var sales = market.ReadHistory(item);
                        if (sales == null) return; // Same 30-second deadline bounds this wait.
                        history = sales.ToArray();
                    }
                    var quote = new CachedComparison(listings.ToArray(), history, now);
                    cache[(item.ItemId, item.IsHq)] = quote;
                    UseComparison(item, quote, false, now);
                    break;
                case Step.Confirm:
                    RequireUnchanged(item);
                    if (comparisonSource == "Cache" && quoteAt is long captured &&
                        now - captured >= CacheLifetimeMillis)
                    {
                        market.CancelOwnedEditor();
                        cache.Remove((item.ItemId, item.IsHq));
                        Record("Cache expired; refreshing comparison.");
                        Move(Step.Open, now);
                        break;
                    }
                    confirmation = "Confirmation attempted; outcome not yet established";
                    market.Confirm(item, target);
                    confirmation = "Submitted; local acknowledgement pending";
                    Record($"Submitted {target} gil once for slot {item.Slot + 1}.");
                    Move(Step.WaitApplied, now);
                    break;
                case Step.WaitApplied:
                    var actual = market.ReadItem(item.Slot);
                    if (actual with { Price = item.Price } != item)
                        throw new InvalidOperationException("The listing identity or quantity changed after confirmation. Check it manually.");
                    if (actual.Price != target || !market.EditorClosed) return;
                    confirmation = "Local price verified; server persistence is not proven";
                    Record($"Local listing now matches {target} gil.");
                    results.Add(new(item, target, "Local price verified"));
                    resultSnapshot = results.ToArray();
                    Move(Step.WaitClosed, now);
                    break;
                case Step.WaitClosed:
                    if (!market.EditorClosed) return;
                    if (++index == items.Count) Stop("Completed");
                    else Move(Step.Open, now);
                    break;
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void UseComparison(SaleItem item, CachedComparison quote, bool cached, long now)
    {
        // Cache market responses, never an old retainer's target or price decision.
        decision = Pricing.Decide(item, quote.Listings, market.OwnRetainers, undercut,
            useHistory ? quote.History : null);
        quoteAt = quote.CapturedAt;
        comparisonSource = cached ? "Cache"
            : decision.Evidence.Eligible == 0 && decision.Evidence.HistorySales > 0 ? "History"
            : "In-game";
        Record($"{comparisonSource}: {Math.Max(0, now - quote.CapturedAt)} ms old; " +
            $"{decision.Evidence.Eligible}/{decision.Evidence.Received} eligible. {decision.Reason}");
        if (!apply || decision.Price == null)
        {
            results.Add(new(item, decision.Price, decision.Price == null ? decision.Reason : "Preview only"));
            resultSnapshot = results.ToArray();
            market.CancelOwnedEditor();
            Move(Step.WaitClosed, now);
            return;
        }
        target = decision.Price.Value;
        market.SetPrice(item, target);
        Move(Step.Confirm, now);
    }

    private void RequireUnchanged(SaleItem item)
    {
        if (market.ReadItem(item.Slot) != item)
            throw new InvalidOperationException("The listing changed. Start a new run after checking the retainer.");
    }

    private void Move(Step next, long now)
    {
        step = next;
        diagnosticItem = items[index];
        if (next == Step.Open)
        {
            decision = null;
            quoteAt = null;
            comparisonSource = "None";
            confirmation = "Not submitted";
        }
        readyAt = now + delay;
        deadline = now + 30_000;
        Status = $"{(apply ? "Apply" : "Preview")}: {index + 1}/{items.Count} — {next}";
        Record($"{Status}; slot {diagnosticItem.Slot + 1}, item {diagnosticItem.ItemId}.");
    }

    private void Fail(Exception ex)
    {
        LastError = ex.Message;
        Stop("Stopped after an error");
    }

    public void Stop(string status = "Stopped")
    {
        // Invalidate the run before cleanup. No later update can submit a confirmation.
        var wasRunning = IsRunning;
        if (step == Step.WaitApplied && status != "Completed")
            status += "; a submitted price may still complete. Check the retainer.";
        Record(status + (LastError.Length > 0 ? $": {LastError}" : ""));
        step = Step.Idle;
        Status = status;
        items = Array.Empty<SaleItem>();
        target = 0;
        if (!wasRunning)
        {
            // A run that failed during Start may already hold the interop lock.
            try { market.EndRun(); }
            catch (Exception ex) { LastError = $"{LastError} Cleanup: {ex.Message}".Trim(); }
            return;
        }
        try { market.CancelOwnedEditor(); }
        catch (Exception ex) { LastError = $"{LastError} Cleanup: {ex.Message}".Trim(); }
        try { market.EndRun(); }
        catch (Exception ex) { LastError = $"{LastError} Cleanup: {ex.Message}".Trim(); }
    }

    public void Dispose()
    {
        Stop();
        cache.Clear();
        disposed = true;
    }
}
