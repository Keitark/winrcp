# WinRCP
![License](https://img.shields.io/github/license/Keitark/winrcp)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![Framework](https://img.shields.io/badge/framework-.NET%209-512BD4)

## Overview
`winrcp` is a Windows-native RCP/G36 player focused on accurate playback behavior and an SC-88-inspired desktop UI. It includes an in-repo parser, sequence builder, playback engine, and LCD display emulation.

## Features
- RCP v2 and G36 parsing inside the repository.
- Realtime playback scheduler and MIDI event renderer for Windows.
- SC-88-inspired WPF monitor UI with Roland display SysEx awareness.

## Current Status
- The desktop app currently builds and runs as a local Windows player.
- Core playback behavior is being refined around observed x68-compatible expectations.

## Requirements
- Windows 10/11
- .NET 9 SDK
- A MIDI output target available through Windows MIDI APIs

## Quick Start
1. Build the application:

```powershell
dotnet build src/RcpPlayer.App/RcpPlayer.App.csproj -c Release
```

2. Run the player:

```powershell
dotnet run --project src/RcpPlayer.App/RcpPlayer.App.csproj
```

## Build Notes
- `src/RcpPlayer.App` contains the WPF desktop application.
- `src/RcpPlayer.Core` contains parsing, sequencing, playback, and display-state logic.

## Windows MIDI Services Note
The app outputs MIDI through `Windows.Devices.Midi` (WinRT MIDI 1.0 API). On supported Windows 11 systems, these legacy APIs can be routed through the Windows MIDI Services compatibility layer.

## Project Structure
- `src/`: application and core library code
- `src/RcpPlayer.App/`: WPF desktop application
- `src/RcpPlayer.Core/`: parsing, sequencing, playback, and display-state logic

## License
This project is distributed under the MIT License. See [`LICENSE`](LICENSE).

## Acknowledgements
- [`foo_midi`](https://github.com/stuerp/foo_midi)
- [ValleyBell MidiConverters](https://github.com/ValleyBell/MidiConverters)
- [`rcm2smf`](https://github.com/shingo45endo/rcm2smf)
