# Interstellar Voice Chat

Real-time proximity voice chat for Among Us — a single BepInEx plugin. Voice goes **direct** between players (STUN hole punching, AES-256-GCM Opus over UDP) and falls back to **TURN relay** when a direct path can't be opened. Signalling rides a BCL-compatible websocket; the client dials one fixed server and deliberately offers no server picker.

**Voice server:** [Fungle-Galaxy/Interstellar-server](https://github.com/Fungle-Galaxy/Interstellar-server) — the Go signalling server (rooms, roster, STUN/TURN `clientPeerConfig`) that this client connects to: `https://bcl.server.amongusclub.cn`.

## Requirements

- Among Us (v18) with **BepInEx 6** (IL2CPP, .NET 6)
- Microphone + speakers/headphones

## Build

**Prerequisites:** .NET 6 SDK

```bash
dotnet build Interstellar.Client/Interstellar.Client.csproj -c Release
```

NAudio and Concentus are resolved from NuGet and embedded into the plugin DLL automatically, so a single build pass produces a self-contained plugin.

**Install:** copy `Interstellar.Client.dll` into `BepInEx/plugins/`. On Android, the same DLL is loaded from the loader's `localMods` folder.

## Project Structure

```
Interstellar/
├── Interstellar.Client/    # BepInEx plugin — audio engine + game integration
│   ├── Network/            #   Socket.IO signalling, P2P transport, TURN client
│   ├── Routing/            #   Audio routing graph (mixer, filters, panner)
│   ├── Voice/              #   Mic, speaker, VCRoom, HUD buttons
│   ├── Game/               #   Among Us integration (HUD, config, settings panel)
│   │   └── UI/
│   ├── Patches/            #   Harmony patches
│   ├── Android/            #   Android mic/speaker via Starlight
│   ├── Resources/          #   Embedded sprites + Locales/*.xml
│   └── Libs/               #   Embedded libraries
└── .github/workflows/      # CI
```

The voice server is a separate repository: [Interstellar-server](https://github.com/Fungle-Galaxy/Interstellar-server).

## How Voice Reaches Other Players

1. **Signalling** — peers exchange ephemeral public keys and candidate lists (local addresses + STUN server-reflexive addresses) over the server's `signal` event. The websocket is control-plane only; audio never rides it.
2. **Direct first** — both sides hole-punch every candidate (100 ms probes); a binding response validates the path and adopts the peer's true egress address. A silent path re-punches automatically.
3. **TURN fallback** — when the punch fails (or from the start under **NAT fix**), audio is relayed through the lowest-latency TURN server the voice server sent in `clientPeerConfig`. Relay addresses are announced per peer; TURN permissions and channels are bound on demand.
4. **Media** — Opus frames sealed with AES-256-GCM on the validated path. Exactly one path per peer — no frame is sent twice.

TURN servers and regions arrive **per connect, after joining a room** — credentials never live in the config file.

### Status line (bottom-left)

While connecting, the bottom-left chip narrates the current step in one line (localized, all 7 languages); once every peer is up it reports the **real** path in use:

```
TURN：分配中… → 等待对方加入… → 交换中继公告… → 绑定通道/探测中…
P2P · TURN 中继 2/2          (or P2P · 直连 2/2 / P2P · 中继/直连 1+1)
```

### Connection controls — Settings → Connection

- **NAT fix** — relay every peer through TURN from the start; for strict/symmetric NATs where punching keeps failing. Toggling **re-joins the voice room** so the new mode applies immediately.
- **TURN region** — Auto (default: race every relay the server sent, keep the lowest latency) or a pinned region. Also re-joins the room.
- **Refresh connection** — rebuilds the voice connection from scratch. Use it if voice dies (no sound, stuck status). A couple of seconds; mic/speaker state is kept.

## Plugin Config

`BepInEx/config/com.interstellar.voice.cfg`:

```ini
[VoiceChat]
MicrophoneDevice =
SpeakerDevice =
MasterVolume = 1.0           # 0.1–3.0
MicVolume = 1.0              # 0.1–3.0
HotkeyButtonScale = 1.0      # HUD mic/speaker buttons, 0.5–2.0 (stock = 1.0)
NoiseSuppression = true
EchoCancellation = true
VADEnabled = true
AutoVolume = false           # auto-level every player's volume

[VoiceChat.P2P]
NatFix = false               # true = TURN relay from the start
TurnRegion =                 # empty = Auto (lowest latency)
StunServers = stun:stun.l.google.com:19302,…   # fallback list (14 built-ins),
                                              # used only when the server sends none
TurnServerUrl =              # local TURN override — leave empty (normal case)
TurnUsername =               #   credentials for server TURNs arrive per connect
TurnCredential =             #   via clientPeerConfig and never live in this file

[VoiceChat.Keybinds]
SettingsPanel = F11          # was F1 — migrated automatically on first run
PublicLobby = F2
PlayerVolume = F3
CycleMic = M
ToggleSpeaker = N

[VoiceChat.Room]
MaxChatDistance = 6
WallsBlockSound = true
OnlyHearInSight = false
ImpostorHearGhosts = false
OnlyGhostsCanTalk = false
HearInVent = true
HearVentPlayers = true
VentPrivateChat = false
CommsSabDisables = true
CameraCanHear = true
ImpostorPrivateRadio = false
OnlyMeetingOrLobby = false
PublicLobby = false          # advertise this room on the public lobby list
PublicTitle = Among Us Lobby
PublicLanguage = en
```

There is no server section: the client always dials the one built-in server (see the link at the top), on purpose.

### Keyboard Shortcuts (defaults — all rebindable)

| Key | Function |
|-----|----------|
| `F11` | Toggle VC settings panel |
| `F2` | Public lobby browser |
| `F3` | Player volume |
| `M` | Cycle mic mode: Global → Impostor Radio → Muted |
| `N` | Toggle speaker on/off |

Chords with `Ctrl` / `Shift` / `Alt` / `Cmd` modifiers are supported (either side of a modifier counts). Rebind in **Settings → Keybinds** or edit the `VoiceChat.Keybinds` entries directly.

## Settings UI

Seven categories: **Audio** · **Keybinds** · **Player Volume** · **Public Lobby** · **Room** · **Connection** · **Advanced**

- **Audio** — mic/speaker devices, processing (noise suppression, echo cancellation, VAD), keybind windows.
- **Keybinds** — chord list; hidden on Android (chords need a hardware keyboard).
- **Room** — proximity/radio rules; the host's values sync to the lobby.
- **Connection** — NAT fix, TURN region, Refresh connection. No server row: there is exactly one server.
- **Advanced** — Opus tuning (bitrate/FEC/DTX/update rate), diagnostics logging, HUD button scale.

On Android the bottom-left HUD buttons are raised for touch; scale them via `HotkeyButtonScale`.

## Localization

UI text is loaded from `Resources/Locales/*.xml`. Maintained locales: **en**, **zh-Hans**, **zh-Hant**, **ja**, **ko**, **de**, **ar**.

## Troubleshooting: Log Markers

The console shows **Info** milestones only — per-server STUN results, punch attempts, channel binds and the full roster payloads are logged at **Debug** level and live in `BepInEx/LogOutput.log`:

| Line | Meaning |
|------|---------|
| `[Srv] Connecting … / Open sid=… / setClients: N peer(s)` | Signalling socket lifecycle |
| `[Ice] server config: stun=… turn=… regions=[…]` | Server config landed (STUN/TURN lists arrive after joining a room) |
| `[P2P] STUN answers 7/14` | How many STUN servers replied (public address found) |
| `[P2P] TURN selected turn:… rtt=… (…)` | Relay race winner + why TURN was needed: `natfix`, `punch failed`, `peer announced relay` |
| `[P2P] cid=N peer relayed=…` | Peer's relay address arrived (announce exchange) |
| `[P2P] connected cid=N via ip:port [TURN]` | Path validated — `[TURN]` tag = relayed, no tag = direct |
| `[P2P] media tx / media rx (first frame)` | First audio frames on the path |
| `[TURN] first ChannelData ch=… / first inbound ChannelData` | Relay channel traffic flowing |
| `[VC:Diag] … tx= rx= idle= farEnd=…` | 30 s heartbeat: send/recv counters, jitter buffer, far-end level |
| `[VC] Room refresh (…)` | Voice room re-joined (NAT fix / region toggle, or the Refresh button) |

A connect sequence reads roughly: `started` → `STUN answers` → `peer relayed` → `TURN selected` → `connected` → `media tx/rx`.

## CI

GitHub Actions (`.github/workflows/build.yml`) builds the plugin on push and attaches it to a release on tags.

## Credits

- [NAudio](https://github.com/naudio/NAudio) — .NET audio library
- [Concentus](https://github.com/lostromb/concentus) — .NET Opus codec
- [BetterCrewLink](https://github.com/OhMyGuus/BetterCrewLink) — voice server protocol reference

## License

MIT
