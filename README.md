# Sekiro Bingo

## What is it?
This mod is created for playing Sekiro Bingo

## What are the changes?
1. 2 Idols are accessible right off the bat
   - Ashina Castle
   - Fountainhead Palace
2. Give the Player Owl Father Bell at the start
3. Make Four Monkey accessible without talking to Isshin
4. Keep the Master of Senpou available to talk to while preserving early Monkeys.
5. Spawn Isshin in Ashina Tower after defeating Genichiro and accepting or
   refusing Tengu's quest. The two actions can occur in either order; completing
   the rat quest and Kuro's tower referral are not required by this spawn gate.
6. Move Tengu to Great Serpent Shrine after completing Isshin's Mortal Blade
   dialogue. Obtaining the Mortal Blade early does not trigger this transition.
7. Make the Master of Senpou disappear after defeating Genichiro and enable
   Holy Chapter: Infested's underwater pickup at Senpou Temple Grounds.

Existing later quest stages, Tengu's retirement conditions and reward acquisition
guards are preserved. These are the reconciled canonical NPC rules; the older
progression alternatives in the staging source are not active.

## How to Install and Play
1. Navigate to the [latest release](https://github.com/ignitesouls/SekiroBingo/releases/latest)
2. Download the attached zipfile `SekiroBingo.v.0.0.1.zip`
3. Extract to your file system
4. Double click `launch-sekiro-bingo.bat` to launch the mod

## Launcher and quality of life

The package includes Mod Engine 3 v0.13.0 and a Windows x64 NativeAOT
`qol/BingoQoL.dll`. The batch launcher calls Mod Engine directly and always
skips startup logos. The profile loads the DLL and calls its
`BingoQoL_Initialize` export. No PowerShell compilation or separately installed
.NET runtime is needed for the QoL helper.

Edit `qol/settings.json` before launching to enable or disable `DisableTutorials`
and `Borderless`. Both values must be JSON booleans. Borderless requires WINDOWED
mode in Sekiro; select your monitor resolution. The helper runs inside the game,
so no separate launcher must stay open. Diagnostics are written to `qol/qol.log`.
Unrecognized tutorial code is left unchanged. A tutorial readiness timeout is
120 seconds. Invalid settings disable the helper and are logged.

### Building

Install the .NET 10 SDK and Visual Studio C++ Build Tools with the Windows SDK.
Run `dotnet publish SekiroBingo/SekiroBingo.csproj -c Release` to build the QoL
native library and include it in the application package. The first build
restores NativeAOT dependencies. The existing WPF application is still .NET 8;
its deployment requirements are separate from the self-contained QoL DLL.

Run `dotnet build BingoQoL -c Release` and
`dotnet run --project BingoQoL.Tests -c Release` for settings and tutorial tests.
The NativeAOT library must remain loaded until game exit; unloading is unsupported.
NPC assets and reproducible development tooling are maintained under `tools/npc`.
See `tools/npc/README.md` for validation, rebuilding and original-game input requirements.

Bundled engine source: https://github.com/garyttierney/me3/releases/tag/v0.13.0
Official Windows ZIP SHA256:
`a8b693c574106f20532ab1e8b58b2452f37370679b938a884fe75f87f9b68c2b`.
