# WWC - Wet Works Company

Source code for the WWC mod for SPT 4.1.6 (EFT 0.16.9.5.40743): The Jackal trader and his quests, the crew tees, wall flags, patches
and the patch wall, the Ferro Concepts Slickster carrier, and since 2.1 the WWC mags, wraps and Raid Tape, an armband, gun and helmet
displays, the gun case and patch case, the FTHS helmets, the Shaw Concepts ARC V2, the Crye JPC, the USMC AirLite, the Avon M50,
the Grunts, the nicotine pouch tins, the Dymok vape and the RC bomb car. One server mod and one client plugin.

The download with the mod's data, models and textures is on the Forge page. This repository holds the code the two DLLs in that
download are built from.

## Layout

- `src/WWC/Server` builds `WWC.Server.dll`. It compiles the server code of every part, linked from the folders next to it.
- `src/WWC/Client` builds `WWC.Client.dll`. It compiles the client code of every part that has one and adds them to one plugin.
- `src/WWC/BigBrain` builds `WWC.BigBrain.dll`: the RC bomb car's bot layer for BigBrain, kept out of `WWC.Client.dll` so that
  nothing in WWC's main DLL needs BigBrain. The client loads it only when BigBrain is installed.
- `src/WWC/Client/ForgeSettings.cs` puts every part's config file into the F12 menu under WWC's own entry. The release build writes
  `settings-schema.json` (each setting's type, note and range) from the shipped config files, next to `WWC.Client.dll`.
- Each other folder under `src` holds one part's code (`Server` and, where there is one, `Client`). The parts were separate mods
  first; built into WWC (`WWC` and `WWC_FORGE` defined) they read their files from their own folder inside WWC's folders.

## Building

You need the .NET 10 SDK and an SPT 4.1.6 install (the projects reference its DLLs, they are not copied). `WWC.BigBrain` also
references BigBrain (`BepInEx\plugins\DrakiaXYZ-BigBrain.dll`); in game BigBrain stays optional. Build the client before it.

```
dotnet build src/WWC/Server/WWC.Server.csproj -c Release -p:Forge=true -p:SptRoot="C:\path\to\SPT" -p:SptRuntimeDir="C:\path\to\SPT\SPT_Runtime\"
dotnet build src/WWC/Client/WWC.Client.csproj -c Release -p:Forge=true -p:SptRoot="C:\path\to\SPT"
dotnet build src/WWC/BigBrain/WWC.BigBrain.csproj -c Release -p:Forge=true -p:SptRoot="C:\path\to\SPT"
```

`-p:Forge=true` gives the release build: mod GUID `com.mrpenguin5292.wwc`, one BepInEx plugin, version 2.1.1. The DLLs land in each
project's `bin\Release` folder. Put `WWC.Server.dll` in `SPT_Runtime\user\mods\Widowz-WWC.Server`, and `WWC.Client.dll` and
`WWC.BigBrain.dll` in `BepInEx\plugins\WWC`, next to the data from the download.

## License

The code is MIT licensed, see `LICENSE`. The models, textures and art in the download are not part of this repository and are not
covered by that license.
