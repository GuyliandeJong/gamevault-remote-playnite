# GameVault Remote Library

A [Playnite](https://playnite.link) library plugin that imports your [GameVault](https://gamevau.lt) server library and installs games straight from the server, without the GameVault desktop client.

## Features
- Imports the library with covers and titles from the server (Playnite metadata fills the gaps)
- Login with username/password or SSO
- Installs portable, software and setup-type games (extracts .zip/.tar/.7z, runs the installer, binds the install folder)
- Background installs with notifications; Linux games are tagged as Linux

## Setup
1. Install the `.pext` from the releases page (or build it yourself, see below).
2. Playnite → Add-ons → Libraries → GameVault Remote Library: enter the server URL (include `http://` for a local address), sign in, pick an install folder.
3. Install [7-Zip](https://7-zip.org) if your server serves `.7z` archives.

## Build
`dotnet build -c Release`, then copy `GameVaultRemote.dll`, `extension.yaml` and `icon.png` from `bin/Release/net462` into a `.pext` (zip) or into `%APPDATA%\Playnite\Extensions\GameVaultRemote`.

## Notes
SSO must be done through the server's public URL (the identity provider redirects there); you can switch to a local address afterwards.
