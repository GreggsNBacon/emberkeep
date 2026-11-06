# Branding

Emberkeep's logo is an ember flame with a server rack cut out of it. Watchfire's is a gold flame sending out a signal.
Both are drawn in `source/emberkeep.svg` and `source/watchfire.svg`; everything else is generated from those two files.

| File | Size | Use |
|---|---|---|
| `emberkeep-logo.png` | 1024×1024 | Emberkeep's logo on transparent, for anything else. |
| `watchfire-logo.png` | 1024×1024 | Watchfire's logo on transparent. |
| `emberkeep-discord-avatar.png` | 1024×1024 | The Discord bot's avatar: the flame on a dark ember glow that fills the square, so Discord's circle crop looks clean. A good bot name to go with it: **Herald**. |
| `emberkeep-discord-banner.png` | 1360×480 | Discord profile banner (17:6). The bottom-left is kept clear for the avatar. |

To regenerate everything after changing an SVG (Windows, Microsoft Edge installed):

```powershell
powershell -ExecutionPolicy Bypass -File branding\build-icons.ps1
```

That writes the PNGs here, the app icons (`src\Emberkeep\Assets\emberkeep.ico` and Watchfire's `assets\watchfire.ico`, with every size from 16 to 256 px drawn at its own size), Emberkeep's `favicon.svg` and Watchfire's `assets\watchfire.svg`.
