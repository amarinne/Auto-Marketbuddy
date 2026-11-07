# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.1] - 2026-11-08

### Changed
- Allow Allagan Market to stay loaded.
- Pause AutoRetainer through `AutoRetainer.SetSuppressed` for each run. Restore only a pause that
  this plugin set, and continue when an older AutoRetainer has no suppression interface.

## [1.1.0] - 2026-11-06

### Changed
- Target Dalamud API 15 and .NET SDK 10.0.403.
- Replace speculative IPC, clipboard parsing, and frame-count timing with fresh native market comparisons and bounded waits.
- Keep plugin identity and the command. Make `start` preview; require `apply` for price changes.
- Bind each run to the character, world, retainer, inventory slot, item quality, quantity, and original price.
- Subtract the undercut once, skip owned retainers and mannequins, and set each listing to that target (raising or lowering as needed); leave it unchanged only at the target or with no competitor.
- Add an optional sale-history fallback: when the market has no competing listing, price from the newest same-quality completed sale instead of leaving the listing unchanged. It is off by default; the window toggle "Use recent sale price when no listings" enables it. A missing history packet still stops on the existing deadline rather than reusing an old price.
- Verify the local listing price before advancing. Stop on errors without retries.
- Reuse comparisons across retainers for five minutes. Fetch cache misses in game and refresh entries with Preview.
- Stop on cancellation, window close, target changes, timeout, and unload. Remove event subscriptions on disposal.
- Allow Marketbuddy to stay loaded: hold its `Marketbuddy.Lock` interop lock for each run (released on completion, Stop, failure, and unload) instead of refusing to start.
- Add offline pricing, response-capture, control, and package checks.
- Remove the obsolete checked-in release ZIP. Pin catalog URLs to the release version.

## [1.0.0] - 2026-01-15

### Added
- Initial release
- Automated retainer price undercutting
- AllaganMarket integration for market data
- Chat message monitoring for data ready signal
- Direct UI manipulation for price setting
- Configurable undercut amount
- Verbose logging option
- Record mode for debugging window states
- Multi-item batch processing
- Automatic confirmation

### Technical Details
- 11-state finite state machine for reliable async operations
- Regex-based clipboard parsing for AllaganMarket format
- Direct `AtkComponentNumericInput` manipulation
- Window state management and cleanup
- 3-second delay for API call completion

### Known Issues
- Double undercut behavior (applies undercut to already undercut price)
- No mid-process cancellation
- Fixed timing may not suit all network conditions
