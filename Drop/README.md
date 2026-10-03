# Drop for Playnite

Your [Drop](https://github.com/Drop-OSS/drop) library as Playnite cards. Drop's server does storage, chunking, encryption and accounts; this plugin puts a card in front of it and speaks Drop's client protocol directly (the desktop app has no CLI to drive).

## Sign in

1. Plugin settings: set the instance URL (the root, e.g. `https://drop.example.com`).
2. Press **Sign in with a code**. A 7-character code appears.
3. In Drop's web UI, Settings, Clients, enter the code and approve it. Press OK in Playnite.
4. Update the library. One card per game in your Drop library.

The credential Drop hands out (a client id and an EC private key) is stored in this plugin's settings file in your Playnite profile, the same way the Drop desktop app stores its own in its database. Every request is signed with it as a ten-second token; nothing long-lived is sent over the wire. Sign out clears it.

## Install and play

Install downloads the newest Windows version through Drop's manifest and depot protocol: every chunk is fetched, decrypted (AES-128, Drop's counter mode), checked against its SHA-256 and written in place. Games land under `%LOCALAPPDATA%\Playnite\Drop\<game>` unless you set another root. Play runs the version's launch command from that folder.

Installs are the plugin's own; the Drop desktop app and this plugin do not see each other's installs.

## Send to Drop (admins)

Set an admin API token (system mode, with the import ACLs) and the Drop library folder as this machine sees it (a network share is fine). Then any Playnite game with an install folder gets a right-click item, **Send to Drop**:

1. its install folder is copied to `<library>\<game>\v<date>\` (nothing at the source is touched);
2. the game is imported with Drop's admin import API, then the version, with the game's own play action as the launch command;
3. the game is added to the plugin's "sent" list.

## Hide what you sent

The admin who sent a game already has it as a Playnite card, so by default the plugin hides your own Drop copies of anything on the sent list. Turn the flag off, or clear the list, in settings.
