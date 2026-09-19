# Installing Dudu Desktop (for the person receiving it)

You only need to do this once. It takes about two minutes.

## 1. Run the installer

1. Download `DuduDesktop-1.0.0-win-x64-private.exe` from the link you were sent.
2. Double-click it.
3. Windows will show a blue **"Windows protected your PC"** box. This is
   normal for a small private app that is not signed by a company.
   Click **More info**, then **Run anyway**.
4. Click **Next** through the setup pages, then leave the **Launch Dudu
   Desktop** box ticked and click **Finish** so Dudu opens right away. Once
   you finish the short setup below, it also starts with Windows from then
   on (this is on by default; you can turn it off in Dudu's settings).

Nothing is installed for other users of the PC, and no administrator
password is needed.

## 2. Connect it (once)

1. In Dudu, open **Connection**.
2. Click the button to make a pairing code. An 8-character code appears.
   It only works for 10 minutes.
3. Read the code out (or send it) to the person who gave you the app. They
   type it into their phone and you are paired.
4. From then on their notes just show up on your desktop. Nothing else to do.

## If something looks off

- **"pairing offline dudu still works here"** on the Connection page means
  the internet link is not reachable right now. Dudu itself keeps working.
  Try again later.
- **"dudu key changed pair again pls"** on the sender's phone means Dudu was
  reinstalled or its data was reset. Make a new code and pair again.
- To uninstall: Windows Settings → Apps → Installed apps → Dudu Desktop →
  Uninstall.

## Requirements

Windows 11, version 24H2 (build 26100) or newer, 64-bit (Intel/AMD). Not for
Windows on ARM, macOS, or Windows 10 — the app refuses to start on anything
older. To check your build: press **Win+R**, type `winver`, press Enter, and
read the build number on the About Windows page.

**The Microsoft Store version of Dudu is the default and recommended way to
install.** It is Microsoft-signed, updates itself, and is not blocked by
Smart App Control. Use the EXE installer in this guide only when the person
who sent it told you to use it instead of the Store.

The EXE installer is unsigned. Verify the SHA-256 line in `SHA256SUMS.txt`
before choosing **Run anyway** in SmartScreen. If your PC has **Smart App
Control** turned on (Settings → Privacy & security → Windows Security →
App & browser control), it blocks the unsigned EXE outright — there is no
"Run anyway" option, and only the Store version will install.
