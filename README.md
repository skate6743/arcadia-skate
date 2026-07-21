# Arcadia

This is a fork of [Arcadia](https://github.com/valters-tomsons/arcadia) tailored specifically for EA Skate 1/2 with required FESL/Theater side changes and dedicated lobby servers implemented. Further documentation on the lobby servers and its stack is found at the [docs](docs/) folder.

Not affiliated, associated, authorized, endorsed by, or in any way officially connected with Electronic Arts Inc. or any of its subsidiaries or affiliates. The use of any trademarks, logos, or brand names is for identification purposes only and does not imply endorsement or affiliation.

## What this fork adds

Upstream Arcadia is a general emulator for many EA Plasma titles. This fork focuses entirely on Skate and adds the in-game layer those games need to actually play online:

- **Dedicated lobby servers** — the full in-game multiplayer layer for Skate: lobbies, free-skate sessions, challenges, the lockstep relay, character (recipe) sharing, etc.
- **Skate-only focus** — for other EA titles, use [upstream Arcadia](https://github.com/valters-tomsons/arcadia).

## Features
* Working matchmaking into Unranked Matches & Freeskate lobbies
* Dedicated servers for each lobby (no P2P connections between players)

## Setup Guide
### Joining Our Public Server (RPCS3)
#### Automated Setup (Recommended for Windows)

[VIDEO GUIDE](https://youtu.be/h-1X4jnx6X4)
1. Open RPCS3, and at the top bar click on Help->Check for Updates, and proceed with updating to latest version. After updating just close out of RPCS3.
2. Download this tool which auto adjusts your config with the right online related settings: [Skate RPCS3 Config Adjuster by BWKingsnake](https://github.com/bwkingsnake/rpcs3-skate-3-config-editor/releases/download/v3.0.0/bwkingsnakes.ConfigEditor.--Release.7z)
3. Run the "ConfigEditorV3.exe" file inside that archive and locate to your rpcs3.exe path when it asks.
4. Select a number for the game that you wanna patch to work online and press `Enter` on keyboard. (Type 2 for Skate 2, etc.)
5. Open RPCS3, click on the "RPCN" icon (right next to pads settings), and go to Account->Create Account if you don't have one already. You will need to fully go through the account creation process with verifying your email, etc.
6. You will now be able to sign into EA Nation on Skate 1/2, whichever game you chose in the config editor tool!


EXTRA STEP REQUIRED FOR SKATE 2 PLAYERS: When RPCS3 is open, make sure your Skate 2 is on version 1.02. If it says "1.00", you must download the 1.02 update PKG and go to File->Install Packages/Raps/Edats and select the PKG file you downloaded. [Skate 2 1.02 PKG files, pick one that matches your game serial on RPCS3](https://www.mediafire.com/folder/juaylpx733apq/Skate_Updates#xhj9w15h1v6w1)

#### Manual Setup (Recommended for Linux)

1. Open RPCS3, and at the top bar click on Help->Check for Updates, and proceed with updating to latest version.
2. On the RPCS3 games list, right click on whichever Skate game you wanna play online, and select "Create Custom Configuration" (If you have made a custom config before then select "Change Custom Configuration")
3. In the CPU tab, set SPU XFloat Accuracy to Approximate XFloat, and SPU Decoder to Recompiler (LLVM). This is mandatory or you will desync out of lobbies.
4. Go to the Network tab and set Network Status to Connected, PSN Status to RPCN, and IP/Hosts switches to `skate2-ps3.fesl.ea.com=172.237.109.212&&skate-ps3.fesl.ea.com=172.237.109.212&&downloads.skate.online.ea.com=172.237.109.212`, and you can save these settings.
5. Click on the "RPCN" icon (right next to pads settings), and go to Account->Create Account if you don't have one already. You will need to fully go through the account creation process with verifying your email, etc.
6. You will now be able to sign into EA Nation on Skate 1/2!

### PS3 Guide (For HEN/CFW Users)

1. Download the [Skate Server IP Changer Tool](https://comingsoon.com) and open the "Skate PS3 Server IP Changer.exe"
2. Press connect, type in your PS3 IP, and hit Enter, then attach to main EBOOT.
3. If you want to simply connect to our publicly hosted server, click on the "Override Server IP" button, the default IP for connecting to our server is `172.237.109.212` and you don't have to modify this at all.
4. After changing the Server IP you can connect to EA Nation, keep in mind PS3 has separate lobbies from RPCS3 due to desync issues that would happen with crossplay.

### Hosting Your Own Server

#### Before getting started you must forward the following ports:

1. Forward the following ports:
Port range 17000-17500 for UDP (used for dedicated lobby servers)

Ports 18420, 18040, 18231, 18126, 18236, 42069, 80 for TCP
2. Download the latest Arcadia Release
3. Extract the archive and run `Arcadia.exe` to start the server
4. Get your [Public IP Address](https://myip.com/) and in Skate 1/2 custom config Network tab set IP/Hosts switches to `skate2-ps3.fesl.ea.com=YOURIPHERE&&skate-ps3.fesl.ea.com=YOURIPHERE&&downloads.skate.online.ea.com=YOURIPHERE`
5. Skate will now connect to your own hosted server

## Known Issues

* Whenever a host leaves, it will kick everyone else out of the lobby. Host migration lobby server packets are currently in the works.
* Skate 1 currently only supports 2 player lobbies. This is due to massive physics stalls that would happen with more than 2 players, we suspect this is something with the lockstep networking model Skate uses in lobbies between players and it's being looked into.

## Game Compatibility

Both PSN and RPCN clients are supported and can play in their own separate lobby servers.

Game     |   Status      | Live status
---------| ----------    | ----- 
Skate    | Online        | 2-player lobbies.
Skate 2  | Online        | Multiplayer lobbies.

## Special Thanks

* *[valters-tomsons](https://github.com/valters-tomsons)* for the original Arcadia repo, with FESL/Theater side functionality implemented
* *[cetteup](https://github.com/cetteup)* - lot of proxy stuff, lots of knowledge of ea systems, lots of captures and for fixing my ea packet implementation! Thanks! 
* *[Aim4kill](https://github.com/Aim4kill)* for the great ProtoSSL vulnerability write-up
* *[And799](https://www.youtube.com/@andersson799)* for devmenu and general frostbite knowledge
* [PSRewired](https://psrewired.com): `1UP` for inclusion in DNS, `Dorian_D` for packet captures
* [Battlefield Modding](https://duckduckgo.com/?t=ffab&q=battlefield+modding+discord) community

## Resources

* https://github.com/valters-tomsons/arcadia
* https://github.com/Aim4kill/Bug_OldProtoSSL
* https://github.com/Tratos/BFBC2_MasterServer
* https://github.com/GrzybDev/BFBC2_MasterServer
* https://github.com/zivillian/ism7mqtt
* https://github.com/RipleyTom/rpcn
* https://www.psdevwiki.com/ps3/X-I-5-Ticket

## Licenses

All code in this project is licensed under GNU General Public License v2.0 (GPL 2.0) except as otherwise noted. 

Certain parts of this project may be covered by different licenses as explicitly indicated in either a license header at the beginning of the relevant file, or separate LICENSE file within the applicable directory.

Please refer to the documentation of each NuGet package for their specific license details.
