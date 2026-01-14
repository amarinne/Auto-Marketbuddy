# Auto-Marketbuddy

A Dalamud plugin for FFXIV that automates retainer price adjustments by integrating with AllaganMarket for market data.

## Features

- 🤖 **Automated Price Undercutting**: Automatically adjusts retainer item prices based on current market data
- 📊 **AllaganMarket Integration**: Uses AllaganMarket plugin to fetch real-time market prices
- ⚙️ **Configurable Undercut Amount**: Set your own undercut value (default: 1 gil)
- 🔄 **Batch Processing**: Processes all items in your retainer's sell list sequentially
- 📝 **Verbose Logging**: Optional detailed logging for debugging

## Requirements

- **Dalamud**: Version 14.0.1 or later
- **FFXIV**: Latest version
- **AllaganMarket Plugin**: Required for market data fetching

## Installation

### From Custom Repository (Recommended)

1. Add the custom repository URL to Dalamud:
   - Open Dalamud Settings → Experimental
   - Add repository: `[YOUR_REPO_URL_HERE]`

2. Install from Plugin Installer:
   - Open `/xlplugins`
   - Search for "Auto-Marketbuddy"
   - Click Install

### Manual Installation

1. Download the latest release from the [Releases](https://github.com/[YOUR_USERNAME]/Auto-Marketbuddy/releases) page
2. Extract to your Dalamud plugins folder:
   ```
   %APPDATA%\XIVLauncher\devPlugins\AutoMarketbuddy\
   ```
3. Restart the game or reload plugins with `/xlplugins`

## Usage

1. Open your retainer and navigate to the "Sell Items" menu
2. Open the plugin window with `/undercut` or via the plugin installer
3. Configure your undercut amount (default: 1 gil)
4. Click "Start Undercut Process"
5. The plugin will:
   - Iterate through each item in your sell list
   - Right-click each item and select "Adjust Price"
   - Wait for AllaganMarket to fetch market data
   - Extract the lowest market price
   - Apply your undercut
   - Confirm the new price automatically

## Configuration

- **Undercut Amount**: The amount (in gil) to undercut the market price by
- **Verbose Logging**: Enable detailed step-by-step logging for troubleshooting
- **Record Mode**: Logs window state changes for debugging

## How It Works

1. **Item Selection**: Clicks through items in RetainerSellList
2. **Context Menu**: Opens context menu and selects "Adjust Price"
3. **Market Data**: Waits for AllaganMarket to fetch data via Universalis API
4. **Price Extraction**: Monitors chat for "copied to clipboard" message
5. **Price Setting**: Directly manipulates the RetainerSell window to set the undercut price
6. **Confirmation**: Automatically confirms the new price

## Technical Details

### Architecture

- **State Machine**: 11-state finite state machine for reliable async operations
- **Chat Monitoring**: Uses `IChatGui` to detect when market data is ready
- **Direct UI Manipulation**: Uses FFXIVClientStructs to directly set price values via `AtkComponentNumericInput`

### State Flow

```
Idle → StartLoop → CheckItem → SelectCurrentItem 
→ WaitForContextMenu → WaitForMarketWindow 
→ WaitForMarketData → ClickMarketItem 
→ ConfirmPrice → WaitForPriceSet → NextItem
```

### Dependencies

- [Dalamud](https://github.com/goatcorp/Dalamud)
- [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)
- [AllaganMarket](https://github.com/Critical-Impact/AllaganMarket)

## Known Issues

- **Double Undercut**: The plugin applies an additional undercut on top of AllaganMarket's recommended price (which is already the lowest market price). This results in undercutting by 2x the configured amount.
  - Example: Market lowest is 15000, AllaganMarket returns 14999, plugin sets 14998 with undercut of 1
  - This is by design and may be addressed in future versions

## Development

### Building

```bash
cd AllaganUndercut
dotnet build
```

### Testing

1. Build the plugin
2. Copy `bin/Debug/AllaganUndercut.dll` and `AllaganUndercut.json` to devPlugins folder
3. Reload plugins in game

### Contributing

Pull requests are welcome! Please ensure:
- Code follows existing patterns
- Include detailed commit messages
- Test thoroughly with retainers

## Troubleshooting

### Plugin doesn't start
- Ensure AllaganMarket is installed and enabled
- Check Dalamud logs for errors (`/xllog`)

### Prices not updating
- Enable Verbose Logging to see detailed state transitions
- Ensure RetainerSell window opens properly
- Check that AllaganMarket overlay appears

### Items being skipped
- Check clipboard is reading correctly (enable Verbose Logging)
- Ensure market data exists for the item
- Verify AllaganMarket is functioning properly

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Acknowledgments

- **AllaganMarket** team for the excellent market data plugin
- **Marketbuddy** for price setting approach inspiration
- **Dalamud** team for the plugin framework

## Disclaimer

This plugin automates game interactions. Use at your own risk. The author is not responsible for any consequences of using this plugin.

---

**Version**: 1.0.0  
**Last Updated**: January 15, 2026  
**Author**: LocalUser
