# NFC Bridge

Lets the admin panel's **Write to NFC Badge** button (Personnel → Manage Accounts → click a
person → Login Card section) write that person's login token onto a physical NFC tag, via a
PC/SC contactless reader/writer (e.g. the CIR615A) plugged into this PC.

A browser page can't talk to USB hardware directly, so this is a small local program that bridges
the two: the admin page calls it over `http://127.0.0.1:8732`, and it talks to the reader via
Windows' own smart-card API.

## Setup (one time)

1. Plug in the reader. Windows should recognize it as a smart-card reader with no extra driver
   (check Device Manager if unsure).
2. Double-click `build.bat`. This compiles `NfcBridge.cs` into `NfcBridge.exe` using the C#
   compiler that ships with Windows -- nothing else to install.

## Running it

Double-click `NfcBridge.exe` and leave its console window open. The admin panel's "Write to NFC
Badge" button only works while this is running on the same PC the reader is plugged into. Close
the window (or Ctrl+C) when you're done.

## Using it

1. In the admin panel, open a person's profile (Manage Accounts → click their row) and make sure
   they already have a login card issued (Generate Login Card, if not).
2. Click **Write to NFC Badge**.
3. Tap a blank NFC tag (NTAG213/215/216-style) on the reader within 20 seconds when prompted.
4. The button reports success or exactly what went wrong (no reader found, no tag presented in
   time, the write didn't verify, etc.).

## What gets written

The same `login_token` the person's QR login card already encodes (a 32-character random value),
as a standard NDEF Text record -- so a future kiosk-side NFC reader can read it back the same way
a QR scan is read today, with no separate credential to manage.

## Troubleshooting

- **"No NFC/smart-card reader found"** -- check it's plugged in and shows up in Windows' Device
  Manager as a smart-card reader.
- **"No tag was presented within 20 seconds"** -- tap again, closer to the reader's top surface.
- **"the write did not verify"** -- the tag may be locked/read-only, or isn't an NTAG/Ultralight-
  compatible tag.
- Nothing happens when clicking the button at all -- make sure `NfcBridge.exe` is actually running
  (its console window open) on this same PC.
