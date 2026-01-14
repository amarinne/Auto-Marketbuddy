# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
