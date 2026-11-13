# dvmcli: DVM console proof of concept for Linux ARM64

A headless test of the [DVM Desktop Dispatch Console](https://github.com/DVMProject/dvmconsole) on Linux aarch64 (built for Omarchy). It connects to an FNE as a console peer, follows one P25 talkgroup, decodes clear voice with libvocoder, and plays it through PipeWire.

It exists to prove the networking, vocoder and audio work on Linux ARM64 before any decision about a full desktop port.

**What works:** FNE login using your dvmconsole codeplug, P25 receive and transmit (clear, AES-256, DES, ADP/ARC4 with keys from the FNE), PipeWire audio, a terminal console (channel cards, mouse support, toggle push-to-talk), and WAV recording.

**Not yet:** DMR/NXDN/analog, and a graphical UI.

## Install on the Surface Pro 11

Download the latest `dvmcli-*-linux-arm64.tar.gz` from the [Releases](../../releases) page (or from a CI run's artifacts), then extract it:

```bash
tar -xzf ~/Downloads/dvmcli-*-linux-arm64.tar.gz -C ~
```

The build is self-contained, so no .NET install is needed. Playback uses `pw-play`, which ships with PipeWire on Omarchy.

## Codeplug

`dvmcli` reads the same codeplug YAML as the Windows console. Give it a **separate peer ID** from any console that is online at the same time, because the FNE accepts each peer ID only once. Copy your codeplug and change `peerId`, `identity` and `password` under the system to the second peer's values.

Keep codeplugs out of git; they contain passwords. This repo's `.gitignore` ignores `*.yml` outside the workflows folder.

## Use

Change into the extracted folder (the name matches the release you downloaded):

```bash
cd ~/dvmcli-*-linux-arm64
```

Run the offline self-test (vocoder, frame decoding, codeplug parsing):

```bash
./dvmcli selftest
```

List the systems and channels in your codeplug:

```bash
./dvmcli list --codeplug ~/dvm/codeplug-linux.yml
```

Follow a talkgroup by channel name and play it:

```bash
./dvmcli listen --codeplug ~/dvm/codeplug-linux.yml --channel "DVM Calling"
```

Other `listen` options:

| Option | What it does |
|---|---|
| `--tg TGID` | Follow a talkgroup by number instead of channel name |
| `--system NAME` | Pick the system (default: the channel's, or the first one) |
| `--wav FILE` | Also record decoded audio to a WAV file |
| `--no-play` | Don't play audio (useful with `--wav`, or to just watch activity) |
| `--gain N` | Multiply output volume, for example `2` |
| `--player CMD` | Use a different player command than `pw-play` |
| `--verbose` | Log every network frame and fnecore debug messages |

Press Ctrl+C to stop.

## Terminal console (TUI)

Start the terminal console for the codeplug's first system (or pick one with `--system NAME`):

```bash
./dvmcli tui --codeplug ~/dvm/codeplug-linux.yml
```

Channels are shown as cards, up to four across. The mouse works in any terminal with xterm mouse reporting (Alacritty, Ghostty, Kitty, foot and most others):

| Click | Action |
|---|---|
| **PTT** (title bar) | Toggle transmit on the focused card (click again to stop) |
| **MUTE** (title bar) | Mute or unmute all receive audio |
| **T1**–**T5** (title bar) | Send alert tone 1–5 on every listening channel (click again to stop) |
| **PAGE** (title bar) | Page: a QuickCall II tone page on the listening channels, or a digital page (P25 call alert) to one radio ID |
| **MARK** (title bar) | Turn the channel marker on or off |
| **EXIT** (title bar) | Quit |
| A card | Focus it and start listening |
| `[●]` on a card | Stop listening |
| `[-]` `[+]` `[mute]` on a card | That card's volume and mute |
| Wheel | Scroll the cards, or the log when the pointer is over it |

| Key | Action |
|---|---|
| Arrows, Tab, PgUp / PgDn, Home / End | Move between cards |
| Enter | Start or stop listening on the focused card |
| Space | Toggle transmit on the focused card (press again to stop) |
| Esc | Stop transmitting or stop a tone |
| 1 – 5 | Send alert tone 1–5 on every listening channel |
| p / d | Open the page box in tone or digital mode (←/→ switch modes, Tab switches tone A / B, Enter sends, Esc cancels) |
| c | Turn the channel marker on or off |
| m / M | Mute the focused card / mute everything |
| + / - | Volume of the focused card |
| k | Request the focused card's key from the FNE again |
| q or Ctrl+C | Quit |

Alert tones match the Windows console's built-in tones 1–5 (5 seconds each). A digital page is a P25 call alert from the console's RID to one radio (the Windows console's "Page Subscriber"); call alerts and acknowledgements addressed to the console's RID are logged when the FNE passes them on. A QuickCall II page is tone A for 1 s then tone B for 3 s (250–3000 Hz, as the Windows console's manual page), sent on every listening channel; the last page's tones are filled in next time. On P25, tones travel as IMBE tone frames, which carry the frequency in steps of about 31 Hz, so radios regenerate the nearest step. The channel marker is the console's "channel hold" beep: 0.5 s of 650 Hz every 10 seconds on each listening channel that isn't busy; change the interval with `--marker-interval SECONDS`. Tones are encrypted on secure channels and skipped on channels whose key isn't held. The tone buttons are hidden on terminals narrower than 110 columns; the keys still work.

Transmit uses the default PipeWire microphone and the system's `rid` as the source ID. A secure channel never transmits in the clear: without its key, transmit is refused. Transmissions stop automatically after 120 seconds; change that with `--tx-limit SECONDS`. Add `--log FILE` to keep a copy of the log.

## Building from source

CI builds every push on GitHub's ARM64 Linux runner. To build locally on Omarchy you need the .NET 10 SDK, CMake and a C++ compiler. Clone with submodules:

```bash
git clone --recursive https://github.com/monkey1911/dvmconsole-linux.git
```

Then follow the same steps as `.github/workflows/build.yml`.

## How it is put together

- `extern/dvmconsole` is a submodule of [monkey1911/dvmconsole-ARM64](https://github.com/monkey1911/dvmconsole-ARM64) (`dev` branch). The CLI compiles its Windows-independent sources directly (codeplug, vocoder interop, logging) and uses its `fnecore` networking library.
- `extern/dvmvocoder` is [DVMProject/dvmvocoder](https://github.com/DVMProject/dvmvocoder), built as `libvocoder.so`.
- `src/dvmcli` is the CLI: the FNE peer, the P25 voice receiver, PipeWire/WAV output, and the self-test.

## License

dvmcli is licensed under the GNU AGPL v3 (see `LICENSE`), as is the dvmconsole code it builds on, which is © DVMProject and contributors. libvocoder is GPL-2.0-only and is built and loaded as a separate shared library, the same way the Windows console uses it.

IMBE and AMBE are trademarks of Digital Voice Systems, Inc. This project is not affiliated with DVSI or DVMProject.
