# dvmcli: DVM console proof of concept for Linux ARM64

A headless test of the [DVM Desktop Dispatch Console](https://github.com/DVMProject/dvmconsole) on Linux aarch64 (built for Omarchy). It connects to an FNE as a console peer, follows one P25 talkgroup, decodes clear voice with libvocoder, and plays it through PipeWire.

It exists to prove the networking, vocoder and audio work on Linux ARM64 before any decision about a full desktop port.

**What works:** FNE login using your dvmconsole codeplug, P25 receive and transmit (clear, AES-256, DES, ADP/ARC4 with keys from the FNE), PipeWire audio, a terminal console with toggle push-to-talk, and WAV recording.

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

| Key | Action |
|---|---|
| ↑ / ↓, PgUp / PgDn, Home / End | Move between channels |
| Enter | Select (listen to) or deselect the highlighted channel |
| Space | Toggle transmit on the highlighted channel (press again to stop) |
| Esc | Stop transmitting |
| m | Mute or unmute the highlighted channel |
| + / - | Volume of the highlighted channel |
| k | Request the highlighted channel's key from the FNE again |
| q | Quit |

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
