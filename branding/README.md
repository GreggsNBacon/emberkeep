# Branding

| File | Size | Use |
|---|---|---|
| `hostbay-discord-avatar.png` | 1024×1024 | Discord bot / app icon. The gradient fills the square, so Discord's circle crop looks clean. |
| `hostbay-logo.png` | 1024×1024 | The app logo (rounded square, transparent corners) for anything else. |
| `hostbay-discord-banner.png` | 1360×480 | Discord profile banner (17:6). The bottom-left is kept clear for the avatar. |

`source/` has the HTML each image is drawn from. To re-render one (Windows, Microsoft Edge installed):

```powershell
powershell -ExecutionPolicy Bypass -File source\render.ps1 -Html source\banner.html -Out hostbay-discord-banner.png -Width 1360 -Height 480
powershell -ExecutionPolicy Bypass -File source\render.ps1 -Html source\logo-avatar.html -Out hostbay-discord-avatar.png
```
