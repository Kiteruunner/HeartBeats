# HeartBeats (v0.1.0)

A lightweight WPF HUD that displays live heart-rate data over Bluetooth LE. The app scans for devices broadcasting the standard Heart Rate service (0x180D), lets you pick one, and shows BPM plus a rolling chart.

## Features
- Automatic scan for BLE heart-rate devices; remembers the last device you chose.
- Device picker when multiple HR devices are present; single device auto-selected.
- Live BPM display with animated dot and configurable grid styles (Off / Minimal / Ticks).
- Adjustable time window (30s / 60s / 120s) with smoothing to keep the chart readable.
- Context menu actions: disconnect, exit, grid mode, and window length.
- Auto-reconnect when a live session drops (unless you explicitly disconnect).
- HUD position/size and settings are persisted between launches.

## Requirements
- Windows 10 (19041) or later with Bluetooth LE.
- .NET 10 SDK (TargetFramework `net10.0-windows10.0.19041.0`).

## Build & Run
```bash
# from repo root
dotnet build

dotnet run --project HeartBeats/HeartBeats.csproj
```

## Usage
- On launch, the app scans for HR devices. If multiple are found, a picker appears; otherwise it connects automatically.
- Left-click & drag anywhere on the card to move; the position is saved.
- Toggle button collapses/expands the chart.
- Right-click the card for options:
  - Grid: Off / Minimal / Ticks
  - Window: 30s / 60s / 120s
  - Disconnect: stop current session (no auto-reconnect)
  - Exit: close the app
- Status button triggers a rescan/reconnect and forces device re-selection.

## Troubleshooting
- If no devices appear, ensure your HR device is advertising the 0x180D service and Bluetooth is enabled.
- If connection drops, the status shows `DISCONNECTED` and auto-reconnect will try again after ~1s unless you clicked Disconnect/Exit.

## Versioning
- Current version: **0.1.0**
- Tag suggestion: `git tag v0.1.0 && git push origin v0.1.0`
