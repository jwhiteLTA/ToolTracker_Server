# ToolTracker NFC Tool

A standalone Windows desktop app (not a browser feature) for writing a user's login card or a
tool's barcode onto a physical NFC tag, via a PC/SC contactless reader/writer (e.g. the CIR615A)
plugged into this PC.

It's a real app with its own login and tabs, not something driven from a web page -- see
`NfcTool.cs`'s own header comment for why: a browser page talking to local USB hardware runs
straight into Content-Security-Policy, CORS, and Chrome's Private Network Access, none of which
apply to a desktop app. It logs into the real ToolTracker API the same way the admin panel does,
so it automatically respects the same permissions (tool_rep+ for tools, hierarchy rules for user
badges) with nothing extra to configure.

## Setup (one time)

1. Plug in the reader. Windows should recognize it as a smart-card reader with no extra driver
   (check Device Manager if unsure).
2. **Important:** some readers (including the CIR615A) ship defaulted to "keyboard emulation"
   mode, where tapping a tag just types its raw ID into whatever has focus instead of exposing it
   to this app. If writes never detect a tag even though the reader visibly reacts, that's almost
   certainly why -- switch it to its normal PC/SC mode first using the manufacturer's own
   configuration utility (for the CIR615A: AB Circle's "Circle Keyboard Configurator").
3. Double-click `build.bat`. This compiles `NfcTool.cs` into `NfcTool.exe` using the C# compiler
   that ships with Windows -- nothing else to install.

## Running it

Double-click `NfcTool.exe` (or download it straight from the admin panel's My Account page). Log
in with a normal ToolTracker badge ID/username and PIN -- the same credentials you'd use on the
admin panel or kiosk.

## Using it

- **My Badge** tab -- writes your own login card (open to every role).
- **Users** tab -- pick anyone your account can manage and write their login card. Only shows
  people you actually outrank, same as Manage Accounts on the admin panel.
- **Tools** tab -- only shown for tool_rep and above. Pick any tool and write its own barcode to
  a tag; no separate permission check needed beyond that, since a tool's barcode isn't a secret
  (it's already printed on the tool's own label).

Search either list by typing part of a name or ID, select a row, click the write button, then tap
a blank NFC tag (NTAG213/215/216-style) on the reader within 20 seconds when prompted.

## What gets written

- For a person: the same `login_token` their QR login card already encodes (a 32-character
  random value), as a standard NDEF Text record -- a future kiosk-side NFC reader could read it
  back the same way a QR scan is read today, with no separate credential to manage.
- For a tool: its own `qr_code` (barcode ID), the same value already printed on its label.

## Troubleshooting

- **"No NFC/smart-card reader found"** -- check it's plugged in and shows up in Windows' Device
  Manager as a smart-card reader.
- **"No tag was presented within 20 seconds"** even though the reader visibly reacted to the
  tap -- see the keyboard-emulation-mode note under Setup above.
- **"the write did not verify"** -- the tag may be locked/read-only, or isn't an NTAG/Ultralight-
  compatible tag.
- Login fails with a connection error -- check the Server URL field matches the real ToolTracker
  address and that this PC can reach it.
