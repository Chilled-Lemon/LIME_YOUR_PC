LIME_YOUR_PC v0.4.0 WPF
======================

What changed from v0.2
----------------------
1. WPF graphical interface.
2. UAC administrator request is declared in app.manifest.
3. Optimization runs on a worker thread so the UI stays responsive.
4. Live activity log, progress bar and verification summary.
5. Re-running the app no longer creates a new power plan every time.
   - If a plan named exactly LIME_YOUR_PC already exists, it is reused.
   - The whitelist is applied again and verified.
   - The plan is activated again.
6. Original Windows/user plans are not deleted or overwritten.
7. Only AC values are written. DC/battery values are not written.

Build
-----
Double-click build.bat, or run:

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None

Final EXE:
bin\Release\net8.0-windows\win-x64\publish\LIME_YOUR_PC.exe

Important
---------
This source package was generated in a Linux environment where the Windows .NET SDK/WPF build toolchain is unavailable, so the final Windows EXE could not be compiled here. Build it on your Windows PC using the .NET 8 SDK (or a compatible newer SDK targeting net8.0-windows).

The current Intel P/E-core detection is still heuristic. A later version should use the Windows CPU Set / native topology APIs for exact core-type detection.


v0.4.0 UI changes:
- More visible primary Apply button with dark text and larger hit target.
- Left column can scroll on smaller windows, preventing Safety content from being clipped.
- ADMIN/READY/SCAN labels replaced with clearer Chinese status text.
- Pixel-art green lemon icon embedded into window and EXE.

This v0.4.0 refresh updates the app icon to a minimalist Famicom/NES-style flat green lemon.

This v0.4.0 refresh updates the app icon using the user's lime reference image.

兼容迁移：若检测到旧版 LEMON_YOUR_PC 电源计划，v0.4.0 会直接复用并重命名为 LIME_YOUR_PC，不会额外复制一份。
