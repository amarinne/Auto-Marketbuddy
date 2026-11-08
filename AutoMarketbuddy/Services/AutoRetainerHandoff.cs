using System;

namespace AutoMarketbuddy.Services;

public enum AutoRetainerMode { Off, Preview, Apply }

public readonly record struct AutoRetainerSettings(AutoRetainerMode Mode, int Undercut,
    int WaitDelayMillis, bool UseHistory);

// AutoRetainer's public handoff: it asks plugins for extra retainer work, then waits for each
// interested plugin once. Keep the IPC surface separate so the lifecycle is testable offline.
public interface IAutoRetainerIpc
{
    bool IsAvailable { get; }
    bool IsBusy { get; }
    void RequestPostprocess();
    void FinishPostprocess();
    event Action<string>? PostprocessStep;
    event Action<string>? ReadyToPostprocess;
}

// Opt-in AutoRetainer participant. It runs the normal MarketSession at the retainer stage
// AutoRetainer hands over, then returns the retainer menu and releases the wait exactly once.
// AutoRetainer's task manager keeps running while its scheduler is suppressed, so the normal
// BeginRun suppression cannot deadlock this handoff.
public sealed class AutoRetainerHandoff : IDisposable
{
    public const long MenuTimeoutMillis = 10_000;
    public const long ListTimeoutMillis = 15_000;
    public const long CloseTimeoutMillis = 10_000;
    public const long SessionLimitMillis = 10 * 60 * 1000;

    private enum Stage { Idle, Requested, Opening, WaitingList, Running, Closing, WaitingMenu }

    private readonly MarketSession session;
    private readonly IRetainerMarket market;
    private readonly IAutoRetainerIpc ipc;
    private readonly Func<AutoRetainerSettings> settings;
    private readonly Func<bool> canRun;
    private readonly Action<string> log;
    private readonly Action<string> error;
    private Stage stage;
    private long deadline;
    private long sessionDeadline;
    private string retainer = "";
    private string requestedRetainer = "";
    private bool sellListOpened;
    private bool lockHeld;
    private bool releaseOnReady;
    private bool disposed;
    private string status = "Idle";
    private string lastError = "";

    public AutoRetainerHandoff(MarketSession session, IRetainerMarket market, IAutoRetainerIpc ipc,
        Func<AutoRetainerSettings> settings, Func<bool> canRun, Action<string> log, Action<string> error)
    {
        this.session = session;
        this.market = market;
        this.ipc = ipc;
        this.settings = settings;
        this.canRun = canRun;
        this.log = log;
        this.error = error;
        ipc.PostprocessStep += OnPostprocessStep;
        ipc.ReadyToPostprocess += OnReadyToPostprocess;
    }

    public bool IsActive => stage >= Stage.Opening;
    public bool IsBusy => stage != Stage.Idle;
    public string Status => status;
    public string LastError => lastError;

    private void OnPostprocessStep(string retainerName) => PostprocessStep(retainerName, Environment.TickCount64);
    private void OnReadyToPostprocess(string retainerName) => ReadyToPostprocess(retainerName, Environment.TickCount64);

    // Called from AutoRetainer.OnRetainerAdditionalTask. Requesting inside that event is the
    // supported contract; a request outside it is not visible to the current retainer pass.
    public void PostprocessStep(string retainerName, long now)
    {
        if (disposed || stage != Stage.Idle) return;
        var current = settings();
        if (current.Mode == AutoRetainerMode.Off || !canRun() || !ipc.IsAvailable || session.IsRunning) return;
        try
        {
            ipc.RequestPostprocess();
            stage = Stage.Requested;
            requestedRetainer = retainerName;
            releaseOnReady = false;
            status = $"AutoRetainer: waiting for {retainerName}";
            lastError = "";
            log($"AutoRetainer: requested the retainer post-process handoff for {retainerName}.");
        }
        catch (Exception ex)
        {
            lastError = $"AutoRetainer request failed: {ex.Message}";
            status = lastError;
            error($"{lastError} This retainer is left alone.");
        }
    }

    // AutoRetainer has set its post-process lock and is waiting for FinishPostprocessRequest.
    public void ReadyToPostprocess(string retainerName, long now)
    {
        if (disposed || stage == Stage.Idle) return;
        if (stage != Stage.Requested)
        {
            log("AutoRetainer: ignored a duplicate post-process callback.");
            return;
        }
        lockHeld = true;
        var current = settings();
        if (releaseOnReady)
        {
            ReleaseNow("AutoRetainer: released without running.");
            return;
        }
        if (current.Mode == AutoRetainerMode.Off || !canRun() || !ipc.IsAvailable)
        {
            ReleaseNow("AutoRetainer: released because the run is disabled or unavailable.");
            return;
        }
        if (session.IsRunning)
        {
            ReleaseNow("AutoRetainer: released because a run is already active.");
            return;
        }
        if (!string.Equals(retainerName, requestedRetainer, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseNow($"AutoRetainer: released because the callback named '{retainerName}', not '{requestedRetainer}'.");
            return;
        }
        retainer = retainerName;
        stage = Stage.Opening;
        deadline = now + MenuTimeoutMillis;
        status = $"AutoRetainer: opening the sale list for {retainer}";
        log($"AutoRetainer: handoff accepted for {retainer}.");
    }

    public void Update(long now)
    {
        if (disposed || stage == Stage.Idle) return;
        var current = settings();
        if (stage == Stage.Requested)
        {
            if (!ipc.IsAvailable)
            {
                ResetRequest("AutoRetainer unloaded before the handoff.");
                return;
            }
            // AutoRetainer's task manager is idle, so a request made during its retainer pass
            // cannot still be pending. This recovers from an AutoRetainer stop or abort.
            if (!ipc.IsBusy)
            {
                ResetRequest("AutoRetainer stopped before the handoff.");
                return;
            }
            if (current.Mode == AutoRetainerMode.Off || !canRun())
            {
                releaseOnReady = true;
                status = "AutoRetainer: waiting to release its next callback.";
            }
            return;
        }
        if (current.Mode == AutoRetainerMode.Off)
        {
            Cancel("AutoRetainer automation was turned off.", now);
            return;
        }
        if (!canRun() || !ipc.IsAvailable)
        {
            Cancel("AutoRetainer is no longer available.", now);
            return;
        }
        switch (stage)
        {
            case Stage.Opening:
                if (now >= deadline)
                {
                    Fail("The retainer menu did not become ready.", now);
                    break;
                }
                if (!market.RetainerMenuReady) break;
                var selected = market.SelectedRetainerName;
                if (!string.Equals(selected, retainer, StringComparison.OrdinalIgnoreCase))
                {
                    Fail($"AutoRetainer selected '{selected}' instead of '{retainer}'.", now);
                    break;
                }
                try
                {
                    sellListOpened = true;
                    market.OpenSellList();
                    stage = Stage.WaitingList;
                    deadline = now + ListTimeoutMillis;
                    status = $"AutoRetainer: waiting for the sale list for {retainer}";
                }
                catch (Exception ex)
                {
                    Fail($"Could not open the retainer sale list: {ex.Message}", now);
                }
                break;

            case Stage.WaitingList:
                if (now >= deadline)
                {
                    Fail("The retainer sale list did not open.", now);
                    break;
                }
                if (!market.ListVisible) break;
                try
                {
                    if (!market.ListReady) break;
                    if (!string.Equals(market.SelectedRetainerName, retainer, StringComparison.OrdinalIgnoreCase))
                    {
                        Fail("The selected retainer changed while the sale list loaded.", now);
                        break;
                    }
                    if (!market.HasListings)
                    {
                        BeginClose(now, $"AutoRetainer: {retainer} has no active listings.");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Fail($"The retainer sale list is not ready: {ex.Message}", now);
                    break;
                }
                session.Start(current.Mode == AutoRetainerMode.Apply, current.Undercut,
                    current.WaitDelayMillis, now, current.UseHistory);
                if (!session.IsRunning)
                {
                    Fail($"Auto-Marketbuddy could not start: {session.LastError}", now);
                    break;
                }
                stage = Stage.Running;
                sessionDeadline = now + SessionLimitMillis;
                status = $"AutoRetainer: running for {retainer}";
                break;

            case Stage.Running:
                if (session.IsRunning)
                {
                    if (now < sessionDeadline) break;
                    session.Stop("Stopped by the AutoRetainer session limit.");
                    Fail("The AutoRetainer handoff exceeded its time limit.", now);
                    break;
                }
                if (session.LastError.Length > 0)
                {
                    Fail(session.LastError, now, notify: false);
                    break;
                }
                BeginClose(now, session.Status == "Completed"
                    ? $"AutoRetainer: closing the sale list for {retainer}"
                    : $"AutoRetainer: {session.Status}");
                break;

            case Stage.Closing:
                if (now >= deadline)
                {
                    ReleaseNow("AutoRetainer: released after the sale list did not close.");
                    break;
                }
                try
                {
                    market.CloseSellList();
                }
                catch (Exception ex)
                {
                    error($"AutoRetainer: could not close the sale list: {ex.Message}");
                    ReleaseNow("AutoRetainer: released with the sale list still open.");
                    break;
                }
                if (market.SellListClosed)
                {
                    stage = Stage.WaitingMenu;
                    deadline = now + MenuTimeoutMillis;
                }
                break;

            case Stage.WaitingMenu:
                if (market.RetainerMenuReady)
                {
                    ReleaseNow(lastError.Length > 0
                        ? $"AutoRetainer: stopped for {retainer}."
                        : $"AutoRetainer: completed {retainer}.");
                    break;
                }
                if (now >= deadline) ReleaseNow("AutoRetainer: released without its retainer menu.");
                break;
        }
    }

    // User Stop and unload use this path. A pending request must still release AutoRetainer's
    // future callback, so it stays in Requested until that callback arrives or AutoRetainer unloads.
    public void Cancel(string reason, long now)
    {
        if (disposed || stage == Stage.Idle) return;
        if (stage == Stage.Requested)
        {
            releaseOnReady = true;
            status = reason;
            return;
        }
        if (session.IsRunning) session.Stop(reason);
        CloseOwnedSaleList();
        ReleaseNow(reason);
    }

    private void CloseOwnedSaleList()
    {
        if (!sellListOpened) return;
        sellListOpened = false;
        try { market.CloseSellList(); }
        catch (Exception ex) { log($"AutoRetainer: sale-list cleanup skipped: {ex.Message}"); }
    }

    private void Fail(string reason, long now, bool notify = true)
    {
        lastError = reason;
        if (notify) error($"AutoRetainer handoff for {retainer}: {reason}");
        if (session.IsRunning) session.Stop("Stopped after an AutoRetainer error.");
        BeginClose(now, $"AutoRetainer: stopped for {retainer}");
    }

    private void BeginClose(long now, string nextStatus)
    {
        status = nextStatus;
        if (!sellListOpened)
        {
            ReleaseNow(nextStatus);
            return;
        }
        stage = Stage.Closing;
        deadline = now + CloseTimeoutMillis;
    }

    // The only place the AutoRetainer wait is released. lockHeld is cleared before the call so a
    // duplicate callback or a second update cannot finish the same handoff twice.
    private void ReleaseNow(string nextStatus)
    {
        status = nextStatus;
        if (lockHeld)
        {
            lockHeld = false;
            try { ipc.FinishPostprocess(); }
            catch (Exception ex)
            {
                lastError = $"{lastError} AutoRetainer release failed: {ex.Message}".Trim();
                error($"AutoRetainer release failed: {ex.Message}");
            }
        }
        stage = Stage.Idle;
        deadline = 0;
        sessionDeadline = 0;
        retainer = "";
        requestedRetainer = "";
        sellListOpened = false;
        releaseOnReady = false;
    }

    private void ResetRequest(string reason)
    {
        log(reason);
        status = reason;
        stage = Stage.Idle;
        requestedRetainer = "";
        releaseOnReady = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        ipc.PostprocessStep -= OnPostprocessStep;
        ipc.ReadyToPostprocess -= OnReadyToPostprocess;
        if (stage != Stage.Idle)
        {
            if (session.IsRunning) session.Stop("Auto-Marketbuddy unloaded.");
            CloseOwnedSaleList();
            // Finish has no owner argument. A pending request can be queued behind another
            // participant, so release only after AutoRetainer has handed its lock to AMB.
            if (lockHeld)
            {
                lockHeld = false;
                try { ipc.FinishPostprocess(); }
                catch (Exception ex) { log($"AutoRetainer: release during unload failed: {ex.Message}"); }
            }
        }
        disposed = true;
        stage = Stage.Idle;
    }
}
