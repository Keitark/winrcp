# WinMcp RCP Player

Windows-native RCP/G36 player prototype with:
- In-repo RCP parser (MIT-licensed code in this repository)
- Real-time scheduler and MIDI event renderer
- SC-88 Pro inspired desktop UI (WPF)
- Matrix-based SC-88 LCD display emulation (Roland display SysEx aware)

## Tech Stack

- .NET 9
- WPF (`src/RcpPlayer.App`)
- Core parser/playback library (`src/RcpPlayer.Core`)

## Windows MIDI Services Note

The app outputs MIDI through `Windows.Devices.Midi` (WinRT MIDI 1.0 API).  
On supported Windows 11 24H2/25H2 systems, these legacy APIs are routed through the newer Windows MIDI Services compatibility layer.

## Build

```powershell
dotnet build WinMcpRcpPlayer.sln -c Debug
```

## Run

```powershell
dotnet run --project src/RcpPlayer.App/RcpPlayer.App.csproj
```

## Test

```powershell
dotnet test tests/RcpPlayer.Core.Tests/RcpPlayer.Core.Tests.csproj -c Debug
```

## Current Parser Coverage

- RCP v2 and G36 header parsing
- Track parsing and event extraction
- Playback mapping for notes, CC/program/aftertouch/pitch/channel change
- Loop start/end (`F9`/`F8`) with bounded infinite-loop expansion
- User/track SysEx expansion with parameter placeholders
- SC-88 display SysEx handling:
  - Display text (`10 00 00`)
  - Dot page writes (`10 0p 00` / `10 0p 40`)
  - Display page/time (`10 20 00` / `10 20 01`)

## License

Code is distributed under the MIT License. See `LICENSE`.

## Acknowledgements

Thanks to the maintainers of `foo_midi` and related Recomposer tooling for public documentation and behavior references used to improve compatibility.

- `foo_midi`: https://github.com/stuerp/foo_midi
- ValleyBell `MidiConverters` (`RCPFormat.txt`, `rcp2mid.c`): https://github.com/ValleyBell/MidiConverters
- `rcm2smf`: https://github.com/shingo45endo/rcm2smf
