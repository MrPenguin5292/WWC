# WWC - Wet Works Company

Source code for the WWC mod for SPT 4.1.6 (EFT 0.16.9.5.40743): The Jackal trader and his quests, the crew tees, wall flags, patches
and the patch wall, and the Ferro Concepts Slickster carrier. One server mod and one client plugin.

The download with the mod's data, models and textures is on the Forge page. This repository holds the code the two DLLs in that
download are built from.

## Layout

- `src/WWC/Server` builds `WWC.Server.dll`. It compiles the server code of every part, linked from the folders next to it.
- `src/WWC/Client` builds `WWC.Client.dll`. It compiles the client code of the patches, wall flags and Slickster parts.
- `src/Jackal`, `src/JackalOrigin`, `src/JackalTee`, `src/WallFlag`, `src/JackalPatches`, `src/Slickster`, `src/WWCJobs` hold each part's code.

## Building

You need the .NET 10 SDK and an SPT 4.1.6 install (the projects reference its DLLs, they are not copied).

```
dotnet build src/WWC/Server/WWC.Server.csproj -c Release -p:Forge=true -p:SptRoot="C:\path\to\SPT" -p:SptRuntimeDir="C:\path\to\SPT\SPT_Runtime\"
dotnet build src/WWC/Client/WWC.Client.csproj -c Release -p:Forge=true -p:SptRoot="C:\path\to\SPT"
```

`-p:Forge=true` gives the release build: mod GUID `com.mrpenguin5292.wwc`, one BepInEx plugin, version 2.0.0. The DLLs land in each
project's `bin\Release` folder. Put `WWC.Server.dll` in `SPT_Runtime\user\mods\Widowz-WWC.Server` and `WWC.Client.dll` in
`BepInEx\plugins\WWC`, next to the data from the download.

## License

The code is MIT licensed, see `LICENSE`. The models, textures and art in the download are not part of this repository and are not
covered by that license.
