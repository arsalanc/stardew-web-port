# Stardew Valley in the browser (unofficial)

Run **your own copy** of Stardew Valley 1.6 in a web browser, compiled to WebAssembly, with two
extras the desktop game doesn't have: a live **wiki panel** and an in-game **AI assistant** that runs
on your own PC.

> **This is an unofficial fan project.** It is not affiliated with or endorsed by ConcernedApe.
> **This repository contains no part of Stardew Valley**: no code, art, audio or data. The setup
> script builds everything from a copy of the game you already own. You need to buy the game
> ([Steam](https://store.steampowered.com/app/413150/Stardew_Valley/), GOG, etc.) to use this.
> Please don't share the `local/` folder or the built output: they contain the game.

## What you get

- **The full game in a browser tab** at desktop speed (about 60 fps with the AOT build), with music
  and sound effects.
- **Saves that persist** in the browser (IndexedDB), including options and startup preferences.
- **Wiki panel (F1)**: shows facts about whatever you hover, hold or talk to (sell price, crop and
  fish details, which villagers love an item, Community Center bundles that still need it, a villager's
  birthday and gift preferences), read from the game's own data, plus a short summary from the
  [Stardew Valley Wiki](https://stardewvalleywiki.com) with a link.
- **Ask panel (F2)**: chat with a local model (via [Ollama](https://ollama.com)) that looks things up
  in your actual save: "How do I make a chest?", "What should I give Shane?", "Which bundles am I
  closest to finishing?". It's free and private: nothing leaves your PC except wiki lookups.
<img width="1872" height="1023" alt="image" src="https://github.com/user-attachments/assets/9f8a84cf-0124-47b9-8c67-505759ab2907" />

## Requirements

- **Windows 10/11** (setup is a PowerShell script).
- **Stardew Valley 1.6** installed (Steam or GOG). Tested with 1.6.15.
- **[.NET 10 SDK](https://dotnet.microsoft.com/download)**.
- **A modern browser with WebGL 2** (tested with Chromium-based browsers).
- Optional, for the fast build: the `wasm-tools-net8` workload
  (`dotnet workload install wasm-tools-net8` from an administrator terminal).
- Optional, for the Ask panel: [Ollama](https://ollama.com) and a model that supports tool calling
  (see [The Ask panel](#the-ask-panel)).

## Setup

```powershell
git clone <this repo>
cd stardew-web-port
.\setup.ps1
```

Setup finds your Steam or GOG install automatically; otherwise point it at the folder that contains
`Stardew Valley.dll`:

```powershell
.\setup.ps1 -GameDir "D:\Games\Stardew Valley"
```

It then decompiles your copy of the game into `local/`, applies the browser patches, exports the
game's audio and builds everything. It takes a few minutes and is safe to re-run (for example after
the game updates).

## Playing

There are two ways to run it. Browser saves belong to a web address, so pick one for playing and
stick with it.

**Fast build (recommended for playing)**: compiled ahead of time to native WebAssembly. Building it
takes about 10 minutes (only needed after code changes), and it runs on port **5280**:

```powershell
.\tools\publish-play.ps1
.\tools\play.ps1
```

Then open <http://localhost:5280>. You can rebuild while playing: the new build is installed the next
time you start `play.ps1`.

**Dev build**: quick to build, but interpreted, so busy scenes (rain on the farm) run at about 25 fps.
It runs on port **5281**:

```powershell
dotnet run --project web\StardewWeb.Server --launch-profile StardewWeb
```

### Controls and switches

| | |
|---|---|
| **F1** | Show/hide the side panel (Wiki tab) |
| **F2** | Open the Ask tab (press again to close) |
| `?perf` | Add to the URL for a frame-timing overlay (FPS, game time per frame, location) |
| `?bgtick` | Keep the game running in a hidden tab (testing only) |

Browsers only allow sound after you interact with the page, so the music starts after your first
click or key press. "Exit to Desktop" returns to the title screen, because a web page can't close its
own tab.

## The Ask panel

1. Install [Ollama](https://ollama.com). Models are several GB; to keep them off your system drive,
   run `setx OLLAMA_MODELS "D:\Ollama\models"` before installing (and optionally
   `setx OLLAMA_KEEP_ALIVE "1h"` so the model stays loaded between questions).
2. Download a model that supports tool calling and fits your GPU. On an 8 GB card, `qwen3:8b` works
   well (about 60 tokens/s on an RTX 2070 Super, and it called the right tool every time in testing):

   ```powershell
   ollama pull qwen3:8b
   ```

3. Open the game, press **F2** and ask away. Pick a different model from the panel's menu, and tick
   **Think harder** for planning questions (slower, better reasoning).

The server talks to Ollama at `http://localhost:11434` with an 8K-token context. Override either with
`--ollama <url>` or `--ollamaContext <tokens>` (use a smaller context if replies become slow because
the model no longer fits in video memory).

## How it works

```
Your Stardew Valley install ──setup.ps1──► local/  (git-ignored)
    Stardew Valley.dll ── ILSpy ──► local/src    decompiled game, patched by tools/PortPatcher
    xTile / GameData / BmFont ────► local/libs
    Content/XACT ── AudioExport ──► local/audio  one .wav per sound + audio.json

web/StardewValley.Web   the game (local/src) + this repo's platform layer, compiled against KNI
web/StardewWeb.Client   Blazor WebAssembly host: canvas, frame loop, side panel, audio, storage
web/StardewWeb.Server   serves the app, your game's Content/ and the exported audio; proxies the assistant
```

- **Engine**: the game targets MonoGame. [KNI](https://github.com/kniEngine/kni), a MonoGame fork with a
  WebGL backend, runs it in the browser with very few changes.
- **Patches**: `tools/PortPatcher` edits the decompiled game with Roslyn. Each patch finds code by
  type, member and syntax shape, then adds or wraps this repo's code around it (mostly `#if WEB`
  blocks), so no game code is stored here. It also repairs a few constructs ILSpy decompiles into
  invalid C#, and recovers three local functions ILSpy drops. If the game changes so much that a
  required patch no longer matches, setup stops and says which one.
- **Audio**: the game's XACT sound banks can't run in a browser (the main one is ~440 MB), so setup
  splits them into individual MS-ADPCM `.wav` files plus a cue manifest. `stardewAudio.js` decodes and
  plays them with Web Audio, following the desktop engine's volume, pitch, looping and category rules.
- **Saves**: .NET's file system in the browser is in memory only, so `WebStorage` mirrors the game's
  save folder to IndexedDB and restores it before the game starts.
- **Wiki and Ask panels**: `WikiContext` and `ChatTools` read the live game (hovered item, villager,
  recipes, inventory, bundles). The Ask panel runs the tool-calling loop in the page, and the server
  forwards requests to Ollama through a small provider interface, so other backends can be added.

## Limitations

- No multiplayer, Steam or GOG features (achievements, invites), and no split-screen.
- A few sounds play without reverb or filter effects; mod-added audio cues aren't supported.
- Mods (SMAPI) don't work.
- Gamepads, touch screens and non-Windows setup are untested.
- The fast build's first load downloads about 20 MB of runtime; game content loads on demand.

## Troubleshooting

- **"Some required patches didn't apply"**: your game version differs from the one the patches were
  written for (1.6.15). Please open an issue with the setup output.
- **Port already in use**: another copy of the server is running. Stop it, or pass `--urls` with a
  different port (saves won't carry over to a different port).
- **The Ask panel says no models**: check Ollama is running (`ollama list`) and you've pulled a model.
- **Replies suddenly get slow**: the model may have spilled out of video memory (other apps use
  it too). Try `--ollamaContext 6144` or a smaller model.

## Credits and license

- Stardew Valley is © ConcernedApe LLC. This project only works with a legally owned copy.
- [KNI](https://github.com/kniEngine/kni) (Ms-PL), [ILSpy](https://github.com/icsharpcode/ILSpy) (MIT),
  [Roslyn](https://github.com/dotnet/roslyn) (MIT), [Ollama](https://ollama.com), and the
  [Stardew Valley Wiki](https://stardewvalleywiki.com) (CC BY-NC-SA 3.0). See
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- This repository's own code is MIT licensed ([LICENSE](LICENSE)).
