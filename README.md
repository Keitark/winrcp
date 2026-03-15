# WinRCP
![License](https://img.shields.io/github/license/Keitark/winrcp)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![Framework](https://img.shields.io/badge/framework-.NET%209-512BD4)

## Overview
`winrcp` is a Windows-native RCP/G36 player focused on accurate playback behavior, practical x68 comparison work, and an SC-88-inspired desktop UI. It includes an in-repo parser, sequence builder, realtime playback engine, LCD display emulation, and reference-comparison tooling used to measure parity against x68 behavior.

## Features
- RCP v2 and G36 parsing inside the repository.
- Realtime playback scheduler and MIDI event renderer for Windows.
- SC-88-inspired WPF monitor UI with Roland display SysEx awareness.
- X68000 reference-comparison tooling for playback validation and mismatch analysis.
- Automated regression coverage for parser and sequencing edge cases.

## Current Status
- Core playback defaults are being aligned toward observed x68 behavior.
- The desktop app currently builds and runs as a local Windows player.
- Reference comparison work is ongoing for the remaining edge mismatches and x68 runner limitations.

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

3. Run the core test project:

```powershell
dotnet test tests/RcpPlayer.Core.Tests/RcpPlayer.Core.Tests.csproj
```

## Build Notes
- `src/RcpPlayer.App` contains the WPF desktop application.
- `src/RcpPlayer.Core` contains parsing, sequencing, playback, and display-state logic.
- `tests/RcpPlayer.Core.Tests` contains parser and sequence-builder regression coverage.
- `scripts/compare-rcp-reference.ps1` drives the x68 reference-comparison workflow.

## Windows MIDI Services Note
The app outputs MIDI through `Windows.Devices.Midi` (WinRT MIDI 1.0 API). On supported Windows 11 systems, these legacy APIs can be routed through the Windows MIDI Services compatibility layer.

## Project Structure
- `src/`: application and core library code
- `tests/`: automated tests
- `scripts/`: developer automation and x68 comparison tooling
- `docs/`: handoff notes and architecture/project notes
- `assets/`: static assets and fixtures

## License
This project is distributed under the MIT License. See [`LICENSE`](LICENSE).

## Acknowledgements
- [`foo_midi`](https://github.com/stuerp/foo_midi)
- [ValleyBell MidiConverters](https://github.com/ValleyBell/MidiConverters)
- [`rcm2smf`](https://github.com/shingo45endo/rcm2smf)
