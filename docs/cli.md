# Flux CLI

[Documentation index](README.md)

The CLI sends requests to the local `fluxd` daemon.
Without a command, `flux-cli` opens the desktop window.

The command is `flux-cli`.
`flux` also works when no other program, such as `fluxcd`, uses that name.
See [the command name](install.md#the-command-name).

## Select a device

Flux uses the only connected paired device by default.
With multiple devices, select a name or device ID:

```sh
flux-cli status --json
flux-cli --device "Pixel 8" ring
flux-cli --device "Pixel 8" send "$HOME/Downloads/report.txt"
```

Names match without case.
Use the device ID when names are not unique.
`-d NAME` and `--device=NAME` also work.

Put the device flag before the command, or right after the command name.
`flux-cli` reads it only in these places.
The text after the first argument of a command stays as you typed it.
For example, `flux-cli commands add Sync rsync -a -d src dst` keeps `-d src` in the command.

## Service and window

```sh
flux-cli help
flux-cli version
flux-cli setup --dry-run
flux-cli setup
flux-cli setup --browser
flux-cli setup --no-plugin
flux-cli doctor
flux-cli update --check
flux-cli update
flux-cli status
flux-cli status --json
flux-cli off
flux-cli on
flux-cli open messages
```

Window pages: `chat`, `messages`, `notifications`, and `device`.
The earlier keys still open a page: `overview` and `commands` open `device`, and `clipboard` and `files` open `chat`.

`flux-cli setup` returns 1 when a step fails, so a script can check it:

```sh
flux-cli setup && echo "Flux is ready"
```

`flux-cli setup` and `flux-cli on` wait until `fluxd` answers a request on its socket.
`fluxd` answers only after it starts the network, so a connection alone does not count.
If `fluxd` stops at start, for example because `config.toml` has an error, the command prints the cause and returns 1.
In a checkout, `flux-cli setup` refuses a `fluxd` path with a quote or a backslash, because systemd cannot run it.
To see the log of the service, run `journalctl --user -u fluxd -e`.

After you install the package, `flux-cli setup` removes the `~/.config/systemd/user/fluxd.service` unit that an earlier `flux-cli setup` of a checkout or of `make install-user` wrote.
That unit hides the unit of the package.
`flux-cli setup` keeps a unit that you changed and prints its path.
To change the service, use a drop-in file:

```sh
systemctl --user edit fluxd
```

`flux-cli doctor` shows which unit file systemd loads for `fluxd.service`, and whether its program exists.
It also checks that no other program uses UDP port 12100, the discovery port of `fluxd`.

When `fluxd` does not answer, `flux-cli` tells you the cause:

| Message | Next step |
| --- | --- |
| `fluxd is off` | Run `flux-cli on`. |
| `fluxd does not run` | Run `flux-cli on`, then `flux-cli doctor`. |
| `cannot connect to fluxd on PATH` | The socket or its folder belongs to another user, or it is not a socket. See [data paths](configuration.md#data-paths). |

`flux-cli version` prints the version of `flux-cli` and of the running `fluxd`.
After an update, it also prints the new `fluxd` version that waits for its restart.
When the [release check](configuration.md#release-check) found a newer release, it prints that release.

`flux-cli update --check` asks GitHub for the latest release and prints it.
`flux-cli update` also installs it.
For a pacman package, it checks `SHA256SUMS.sig` with the public release key when the build has one.
Then it downloads the release package, checks it against `SHA256SUMS`, and runs `sudo pacman -U`.
When the signature is missing or does not match, it installs nothing.
See [update with flux-cli](install.md#update-with-flux-cli).
For a source install, it prints the commands for the checkout.
Without a network, it prints the error and returns 1.

`flux-cli --device "Pixel 8" update --phone` sends the latest Flux for Android to the phone.
`flux-cli status` shows when a phone has an earlier Android app.
See [update the app](android.md#update-the-app).
See [update](install.md#update).

`flux-cli open` starts `fluxd.service` when no `fluxd` answers.
After `flux-cli off`, it leaves `fluxd` off.

To select the Qt app or shell plugin explicitly:

```sh
FLUX_GUI=app flux-cli open files
FLUX_GUI=plugin flux-cli open notifications
```

## Browser extension

`flux-cli browser` writes the native messaging manifests that the [browser extension](browser.md) needs.
Each browser loads the unpacked extension from its folder by hand.

```sh
flux-cli browser install
flux-cli browser install --dry-run
flux-cli browser remove
flux-cli browser remove --dry-run
```

`flux-cli setup` does not write browser host manifests unless you pass `--browser` (`--no-browser` is also accepted and does nothing).
Without `--browser`, `flux-cli setup` prints `To send links from a browser, run: flux-cli browser install`.
`flux-cli browser install` writes the manifest once per installed browser, using the fixed Chromium extension ID and the fixed Gecko add-on ID (`flux@omarchy.org`).
`--dry-run` lists what would be written or removed, which is worth doing before the first run on a machine with many browsers.

## Pair and discover

```sh
flux-cli discover
flux-cli pair "Pixel 8"
flux-cli accept "Pixel 8"
flux-cli accept DEVICE_ID 5EE6 825F 974E D59A
flux-cli reject "Pixel 8"
flux-cli unpair "Pixel 8"
```

`flux-cli discover` sends the identity of the computer over UDP and asks Avahi for the address of each Flux device again.
`fluxd` then connects to each device that it can reach.
Use it when a phone that is on the network does not show.

The verification key has 16 uppercase hex digits in 4 groups of 4, for example `5EE6 825F 974E D59A`.
Compare all 16 characters on both devices before you accept.
An earlier Flux app shows only 8 characters, so update Flux on each device before you pair.

`flux-cli pair` asks the device to pair and prints the key.
After the device accepts, `flux-cli pair` asks you to compare the key:

```text
Confirm 5EE6 825F 974E D59A on Pixel 8 (DEVICE_ID)…
Does Pixel 8 show 5EE6 825F 974E D59A? [y/N] y
✓ Pixel 8 (DEVICE_ID) paired with the key 5EE6 825F 974E D59A
```

Type `y` only when the device shows the same key.
Any other answer rejects the pairing, and the device removes it.
The question ends when the pairing ends in another way.
For example, you select **Confirm** in the Flux window, the device ends the pairing, or the 30 seconds end.
When stdin is not a terminal, `flux-cli pair` prints the command that confirms the pairing, and then it stops:

```text
Pixel 8 accepted. Compare the key. When Pixel 8 shows 5EE6 825F 974E D59A, run:
  flux-cli accept DEVICE_ID 5EE6 825F 974E D59A
```

Run that command within 30 seconds, after you compare the key.

`flux-cli accept` accepts a pair request of a device.
It also confirms a pairing that this computer started and that the device accepted.
It always sends a key to `fluxd`, and `fluxd` accepts only the pairing with that key.
To accept only the key that you compared, give the key after the device.
The key can have spaces, for example `flux-cli accept DEVICE_ID "5EE6 825F 974E D59A"`.
Without a key, `flux-cli accept` shows the key of the open pairing and asks you to compare it:

```text
Does Pixel 8 show 5EE6 825F 974E D59A? [y/N] y
✓ Pixel 8 (DEVICE_ID) paired with the key 5EE6 825F 974E D59A
```

`flux-cli accept` sends the key that it showed, so `fluxd` refuses the answer when another pairing opened after the question.
Any answer other than `y` rejects the pairing.
When stdin is not a terminal, `flux-cli accept` without a key accepts nothing.
It prints the command with the key and exits with status 1:

```text
flux-cli: compare the key first. When Pixel 8 shows 5EE6 825F 974E D59A, run:
  flux-cli accept DEVICE_ID 5EE6 825F 974E D59A
```

`flux-cli reject` also sends a key, and it takes the same key argument.
Without a key, `flux-cli reject` rejects the pairing that is open when it runs.
With the device ID, it also cancels a pair request of this computer that the device did not answer.

Each command prints the device ID next to the name:

| Command | Output |
| --- | --- |
| `flux-cli pair` | `Confirm 5EE6 825F 974E D59A on Pixel 8 (DEVICE_ID)…`, then the question, then `✓ Pixel 8 (DEVICE_ID) paired with the key 5EE6 825F 974E D59A` |
| `flux-cli accept` | Without a key, the question. Then `✓ Pixel 8 (DEVICE_ID) paired with the key 5EE6 825F 974E D59A` |
| `flux-cli unpair` | `Unpaired Pixel 8 (DEVICE_ID), certificate FINGERPRINT` |

The fingerprint has 16 hex digits from the public key of the certificate of the device.
`flux-cli status` shows the ID and the fingerprint on the line under each device:

```text
  Pixel 8                phone   connected  78%    192.168.1.20    paired
                         ID DEVICE_ID · certificate 0A1B 2C3D 4E5F 6071
```

The `fingerprint` field of each device in `flux-cli status --json` has the same value, without spaces.

A name matches only a device that is paired or connected, and a paired device comes first.
`flux-cli pair` finds a name only among the devices that are connected and not paired.
`flux-cli accept` and `flux-cli reject` find a name only among the devices with an open pair request or a pairing in state `confirm`.
When the name still matches more than 1 device, the command returns the `ambiguous` error with the device IDs:

```text
flux-cli: 2 devices are named "Pixel 8": DEVICE_ID_1, DEVICE_ID_2. Give the device ID
```

Give the device ID in place of the name:

```sh
flux-cli pair 9f1c0e5b7a2d4c3e8b6a1f0d2c4e6a8b
```

See [phone pairing](features.md#pair-a-phone) and [security](security.md).

### Pair by address

On a network that blocks mDNS and broadcasts, for example eduroam or a guest Wi-Fi with client isolation, discovery finds no device.
To pair, open Flux on the phone, then give the IP address or the host name of the phone:

```sh
flux-cli pair --address 192.168.1.20
flux-cli pair --address 100.101.102.103
flux-cli pair --address pixel-8.tailnet.ts.net:12100
```

`fluxd` connects to the address and gets the device ID from the TLS certificate of the device.
Without a port, it tries each port from 12100 to 12108.
Then `flux-cli pair --address` shows the 16-character key and asks you to confirm it, as `flux-cli pair` does.
The key comparison authenticates the device, also when you typed a wrong address.

The phone takes a link from a new computer only while Flux is on the screen.
The 2 devices must reach each other over TCP.
A network that also blocks unicast traffic between its clients needs a VPN, such as [Tailscale](tailscale.md).
To find the IP address of an Android phone, open **Settings > About phone > Status information**, or the details of the Wi-Fi network.

## Reach a device away from the local network

```sh
flux-cli addresses
flux-cli --device "Pixel 8" addresses add pixel-8
flux-cli --device "Pixel 8" addresses add 100.101.102.103
flux-cli --device "Pixel 8" addresses remove 100.101.102.103
```

An extra address is a host name or an IP address without a port, for example the Tailscale name of the phone.
While the device is offline, `fluxd` dials the extra addresses after the last address.
The device must be paired.
See [Connect through Tailscale](tailscale.md) and [Connect through ZeroTier](zerotier.md).

## Share and communicate

```sh
flux-cli ring
flux-cli ping "Connection check"
flux-cli send "$HOME/Downloads/report.txt" "$HOME/Pictures/photo.png"
flux-cli text "Meet at 18:00"
flux-cli clip
flux-cli clip "Text from the desktop"
flux-cli url https://omarchy.org
flux-cli notify "Backup done" "412 files, 2.1 GB"
```

`flux-cli ring` rings only a phone or a tablet.
`flux-cli url` sends only an `http` or `https` URL with a host. The device shows the link in a notification and opens it after a tap.
`flux-cli send` queues files or folder archives and prints the transfer count.
`flux-cli clip` without text sends the desktop clipboard.
When the clipboard holds an image, the command sends the image and returns when the transfer ends.
See [clipboard images](features.md#clipboard-images).

### Share text

`flux-cli text` sends plain text to the device as a shared text entry:

```sh
flux-cli text "Meet at 18:00"
flux-cli --device "Pixel 8" text "Room 4B"
```

### Transfers

`flux-cli transfers` lists recent and queued file transfers, or acts on one transfer by ID:

```sh
flux-cli transfers
flux-cli transfers retry TRANSFER_ID
flux-cli transfers cancel TRANSFER_ID
flux-cli transfers open TRANSFER_ID
flux-cli transfers copy TRANSFER_ID
```

`open` opens the local file of a finished transfer in the default desktop app.
`copy` puts the local file of a finished transfer on the desktop clipboard (as image data for small images, else as a file URI).
See [workflow controls](workflows.md#desktop-outbox).

### Clipboard and snippets

```sh
flux-cli clipboard
flux-cli clipboard status
flux-cli clipboard pause
flux-cli clipboard resume
flux-cli clipboard clear
flux-cli clipboard clear --all
flux-cli clipboard delete CLIP_ID
flux-cli clipboard limit
flux-cli clipboard limit 100
flux-cli snippets
flux-cli snippets search "report"
flux-cli snippets save CLIP_ID 24h
flux-cli snippets copy CLIP_ID
flux-cli snippets remove CLIP_ID
```

`flux-cli clipboard` lists the clipboard history as JSON.
`flux-cli clipboard clear` removes unpinned clipboard history. Add `--all` to also remove saved snippets.
`flux-cli clipboard delete ID` deletes a history entry or saved snippet.
`flux-cli clipboard limit [N]` shows or sets the number of clipboard history entries to keep (1 to 500, default 50).
`flux-cli clipboard pause` and `resume` stop and restart the automatic sync of copies to your devices; `status` prints `paused`, `syncing`, or `off`. See [pause clipboard sync](features.md#pause-clipboard-sync).
`flux-cli snippets` searches, saves (with an optional duration such as `24h`), copies, or unpins saved clipboard entries. See [saved clipboard snippets](workflows.md#saved-clipboard-snippets).

### Text messages

Turn on **Text messages** on the phone first. See [text messages](features.md#text-messages).

```sh
flux-cli sms threads
flux-cli sms thread 12
flux-cli sms refresh
RECIPIENT='+15550100123'
MESSAGE='On my way'
flux-cli sms "$RECIPIENT" "$MESSAGE"
```

`flux-cli sms threads` prints `THREAD  NAME  LAST` for each conversation, newest first.
`flux-cli sms thread N` fetches the messages of conversation `N` (`N` > 0) and prints `YYYY-MM-DD HH:MM  NAME  BODY` in local time, oldest first, with `me` as the sender of outgoing messages.
`flux-cli sms refresh` asks the phone to send its conversation list again.
Any other first argument sends a text message (`flux-cli sms NUMBER TEXT...`) to 1 recipient.

### Do Not Disturb

```sh
flux-cli dnd
flux-cli dnd on
flux-cli dnd off
```

`flux-cli dnd` prints `Do Not Disturb is on on NAME`, `off`, or `unknown` when the phone is offline or has not reported its state.
`flux-cli dnd on` and `flux-cli dnd off` set Do Not Disturb on the selected phone.

### Notifications and rules

```sh
flux-cli notifications
flux-cli --json notifications
flux-cli notifications dismiss NOTIF_ID
flux-cli notifications reply NOTIF_ID On my way
flux-cli notifications action NOTIF_ID "Mark as read"
flux-cli notifications clear
flux-cli notification-rules
flux-cli --device "Pixel 8" notification-rules add Messages mute 1h
flux-cli notification-rules quiet 22:00 07:00
flux-cli notification-rules remove RULE_ID
```

`flux-cli notifications` prints one line per phone notification.
Get the notification `id`, `replyId`, `actions`, and `dismissable` fields from `flux-cli --json notifications` (or `flux-cli status --json`).
`flux-cli notifications dismiss ID` dismisses one notification on the phone and on the desktop.
`flux-cli notifications reply ID TEXT...` sends an inline reply to a notification that has a `replyId`.
`flux-cli notifications action ID ACTION` triggers a named button from the notification's `actions` list.
`flux-cli notifications clear` dismisses all dismissable notifications of the phone; ongoing notifications stay.
`flux-cli notification-rules` lists, adds, or removes desktop notification rules and quiet hours. See [notification rules](workflows.md#notification-rules).

## Settings

```sh
flux-cli settings
flux-cli settings checkUpdates
flux-cli settings checkUpdates false
flux-cli settings clipboardLimit 100
flux-cli settings name "Workstation"
```

`flux-cli settings` prints `key = value` for every global setting in sorted order (including `name` from `self.name`, and leaving out `deviceRules`, `notificationRules`, and `automationRules`).
`flux-cli settings KEY` prints the value of one setting.
`flux-cli settings KEY VALUE` updates one setting and saves `config.toml`:

| Type | Keys | Values |
| --- | --- | --- |
| Boolean | `autoClipboard`, `notifications`, `shareHome`, `pauseMediaOnCall`, `syncDnd`, `checkUpdates`, `herdr`, `herdrControl`, `herdrTerminals`, `remoteInput`, `remoteDesktop`, `motionCues`, `motionAuto` | `true`, `false`, `on`, or `off` |
| Integer | `clipboardLimit`, `motionIdle` | `1` to `500` for `clipboardLimit`; `30` to `3600` for `motionIdle` |
| Number | `motionSensitivity` | A finite number (`0.5`, `1`, or `1.6`) |
| String | `name`, `downloadDir`, `motionSize` | Text (`small`, `medium`, or `large` for `motionSize`) |

### Per-device access

```sh
flux-cli --device "Pixel 8" device-settings
flux-cli --device "Pixel 8" device-settings clipboard false
flux-cli --device "Pixel 8" device-settings clipboard inherit
```

`flux-cli device-settings` shows or sets per-device access overrides (`clipboard`, `notifications`, `shareHome`, `remoteInput`, `remoteDesktop`, `herdr`, `herdrControl`, `herdrTerminals`). See [per-device access](workflows.md#per-device-access).

## Notify when a command ends

```sh
flux-cli notify --run -- make -j8
flux-cli --device "Pixel 8" notify --run -- rsync -a ~/Photos nas:/backup
```

Flux runs the command in the current terminal.
It sends the result, exit code, and elapsed time to the phone.
The notification has only the program name, such as `make`, because the phone can show it on the lock screen.
The arguments of a command can hold a password or a token.
The CLI returns the command's exit code.
Ctrl+C stops the command and still sends the result.
Put Flux flags before `--`.

To send the full command line, add `--show-command` right after `--run`:

```sh
flux-cli notify --run --show-command -- make -j8
```

The phone then gets at most the first 200 bytes of the command line.

## Desktop commands

```sh
flux-cli commands
flux-cli commands add "Lock screen" omarchy-system-lock
flux-cli commands remove COMMAND_ID
flux-cli run COMMAND_ID
```

`flux-cli commands` lists desktop commands available to the phone.
Replace `COMMAND_ID` with an ID from that list.
`flux-cli run` executes the command on the desktop.

Give the command as 1 argument in quotes. `fluxd` runs it with `sh -c`:

```sh
flux-cli commands add Backup "rsync -a ~/Documents nas:/backup"
```

When you give more than 1 argument, `flux-cli` quotes each argument for `sh`.
So `flux-cli commands add Open xdg-open "My File.pdf"` stores `xdg-open 'My File.pdf'`.

### Automation

```sh
flux-cli automation
flux-cli automation add file.received COMMAND_ID
flux-cli --device "Pixel 8" automation add battery.low COMMAND_ID 20
flux-cli --device "Pixel 8" automation add device.disconnected COMMAND_ID 60
flux-cli automation remove RULE_ID
```

`flux-cli automation` lists or changes rules that run a desktop command when a device or agent event happens. See [local automation](workflows.md#local-automation).

## Streams and approval

```sh
flux-cli webcam
flux-cli webcam start
flux-cli webcam set aspect=1:1 brightness=0.2
flux-cli webcam reset
flux-cli webcam stop
flux-cli mic
flux-cli mic start
flux-cli mic stop
flux-cli speaker
flux-cli speaker start
flux-cli speaker stop
flux-cli screen
flux-cli screen stop
flux-cli desktop
flux-cli desktop stop
flux-cli browse
flux-cli browse stop
flux-cli approve
sudo flux-cli approve setup
sudo flux-cli approve enroll
sudo flux-cli approve enable
sudo flux-cli approve disable
sudo flux-cli approve remove
flux-cli ssh
flux-cli ssh key
flux-cli ssh socket
```

Start camera, microphone, and screen capture on the phone.
`flux-cli webcam start` and `flux-cli mic start` ask the phone to start its camera or its microphone.
`flux-cli speaker start` asks the phone to play the audio of this computer.
The phone asks you first, and the stream starts only after you tap start on the phone.
On success, the command prints the device:

```text
Asked Pixel 8 to start the webcam. Confirm on Pixel 8.
Asked Pixel 8 to play the audio of this computer. Confirm on Pixel 8.
```

Without `--device`, the command selects the only paired, connected device that can take the request.
For these 3 commands, `--device NAME` can also come after `start`:

```sh
flux-cli webcam start --device "Pixel 8"
```

See [start from the computer](camera.md#start-from-the-computer) for what the phone shows and for the errors.

`flux-cli speaker` shows whether a phone plays the audio of this computer, and which output `fluxd` muted for it.
`flux-cli speaker stop` ends the stream and turns the sound of that output on again:

```text
Pixel 8 plays the audio of this computer, 48000 Hz, stereo
Laptop Speakers is muted until the stream stops.
Stop it with: flux-cli speaker stop
```

See [phone as speaker](camera.md#phone-as-speaker).

`flux-cli desktop` shows whether a phone shows the screen of this computer, and `flux-cli desktop stop` ends it.
The same commands show and stop the [extended screen](remote-desktop.md#extend-the-screen) of a phone or a tablet.
`flux-cli browse` shows the devices that browse this computer with [Browse PC](features.md#browse-pc).
`flux-cli browse stop` ends each session, or only the session of the device that `--device` names.
See [remote desktop](remote-desktop.md).
See [camera and streams](camera.md) for settings and [fingerprint approval](approvals.md) for root setup commands.

`flux-cli ssh` prints the SSH key of each connected Android phone with SSH key on, and the ssh-agent socket of `fluxd`.
`flux-cli ssh key` prints only the lines for `authorized_keys`, and `flux-cli ssh socket` prints only the socket path.
Add `--json` for the socket and the keys with their device IDs and fingerprints.
When no connected phone has a key, the command exits with 1.
See [SSH key on the phone](ssh.md).

## Remote access

To let a paired phone or Mac show and control this computer, run:

```sh
flux-cli desktop on
flux-cli input on
```

`flux-cli desktop on` lets the device show the screen.
`flux-cli input on` lets the device move the pointer and type.
To take the access back, run `flux-cli desktop off` and `flux-cli input off`.
`flux-cli desktop off` also stops a stream that runs.
`flux-cli input` without an argument shows whether remote input is on.

`fluxd` saves the change in `config.toml` at once.
The **Access** card on the **Phone** page of the Flux window has the same 2 switches, as **Turn on** and **Turn off** under **Remote desktop** and **Remote input**.
See [remote desktop](remote-desktop.md) and [touchpad and keyboard](remote-input.md).

## Motion cues

To let a paired phone send its motion for the [motion cues](motion-cues.md), run:

```sh
flux-cli motion on
```

| Command | Effect |
| --- | --- |
| `flux-cli motion` | Show the state and the settings. |
| `flux-cli motion on`, `off` | Turn the feature on or off. |
| `flux-cli motion start`, `stop` | Start or stop the cues now. `start` takes `--device NAME`. |
| `flux-cli motion auto on`, `off` | Start the cues when the phone detects a vehicle. |
| `flux-cli motion sensitivity 0.5\|1\|1.6` | Set how far and how fast the dots move. |
| `flux-cli motion size small\|medium\|large` | Set the dot size. |
| `flux-cli motion idle SECONDS` | Stop after this time without motion, from 30 to 3600 seconds. |

## Watch state changes

```sh
flux-cli watch
```

The command prints one JSON event per line until you stop it.
When `fluxd` stops, the command prints `fluxd closed the connection` and returns 1.
Use `flux-cli status --json` for a single snapshot.
See [IPC](ipc.md) for the event envelope and socket protocol.

## JSON output

Add `--json` for one JSON value on stdout:

```sh
flux-cli --json status
flux-cli status --json
flux-cli --json --device "Pixel 8" notifications
flux-cli --device "Pixel 8" sms threads --json
flux-cli --json clip "Text from the desktop"
```

### Where `--json` goes

- Put `--json` before the command, or in the flag region right after the command (where `--device NAME`, `-d NAME`, and `--device=NAME` go).
- A trailing `--json` (the last argument) also selects JSON output when the form takes no free text.
- For a form that takes free text, `--json` counts only before the command. Right after the command name (`flux-cli text --json hi`), it is refused with `bad_args` before anything is sent. After the text, it stays part of the text. Those forms are:
  - `ping`, `clip`, `text`, `notify`, `send`
  - `sms NUMBER TEXT...`
  - `commands add`
  - `notifications reply`
  - `snippets search`
  - `settings KEY VALUE`
- Nothing after `--` is read as a flag.

JSON output passes the raw socket bytes or state projection through `safeJSON`, so control characters are escaped while newlines (`\n`) and all fields stay intact.

### Output of each form

| Form | JSON output |
| --- | --- |
| `status`, `devices` | The raw `state` snapshot |
| `ssh [key\|socket]` | `{"socket":…,"error":…,"keys":[…]}`, the same object for all three forms |
| `discover`, `ring`, `ping`, `clip`, `text`, `url`, `run`, `notify TITLE [BODY]`, `sms NUMBER TEXT...` | The method's result (`{}` when empty) |
| `addresses` | `{deviceID:[addresses]}` from the state |
| `addresses add\|remove HOST` | The method's result (`{"device":…,"address":…,"addresses":[…]}`) |
| `send PATH...` | The `share.files` result (`{"transfers":[…]}`) |
| `transfers` | The state's `transfers`, raw |
| `transfers retry\|cancel\|open\|copy ID` | The method's result |
| `clipboard` | The `clipboard.search` result |
| `clipboard status` | `{"paused":…,"autoClipboard":…}` from the raw state |
| `clipboard pause\|resume\|clear\|delete` | The method's result |
| `clipboard limit` | `{"clipboardLimit":N}` |
| `clipboard limit N` | The `settings.set` result |
| `snippets`, `snippets search TEXT` | The `clipboard.search` result |
| `snippets save ID [DURATION]` | The `clipboard.pin` result |
| `snippets remove\|copy ID` | The method's result |
| `sms threads` | `{deviceID:[conversations]}` from the state |
| `sms thread N` | The `sms.thread` result (`{"messages":[…]}`) |
| `sms refresh` | The `sms.refresh` result |
| `dnd` | `{deviceID: dnd or null}` from the state (`null` when the device is offline or has no `dnd` report) |
| `dnd on\|off` | The `dnd.set` result (`{"sent":true}`) |
| `settings` | The state's `settings` object (with `deviceRules`, `notificationRules`, and `automationRules`), plus `name` from `self.name` |
| `settings KEY` | `{"key":K,"value":V}` (`name` comes from `self.name`) |
| `settings KEY VALUE` | The `settings.set` result |
| `notifications` | `{deviceID:[notifications]}` from the state |
| `notifications clear` | The `notification.dismissAll` result (`{"dismissed":N}`) |
| `notifications dismiss\|reply\|action` | The method's result |
| `notification-rules`, `automation` | The raw `notificationRules` or `automationRules` array from `settings` |
| `notification-rules add\|quiet\|remove`, `automation add\|remove` | The method's result |
| `device-settings` | The `device.settings` result (`{device}`) |
| `device-settings KEY true\|false\|inherit` | The `device.settings.set` result |
| `commands` | The state's `commands`, raw |
| `commands add\|remove` | The method's result (`add` has the new `id`) |
| `webcam`, `mic`, `speaker`, `screen`, `desktop`, `browse`, `motion` (status forms) | The state key of the same name, raw |
| `input` (status) | `{"remoteInput":…}` |
| `webcam\|mic\|speaker start`, `webcam\|mic\|speaker\|screen\|desktop\|browse\|motion stop`, `webcam set\|reset`, `motion start` | The result of the action method |
| `input on\|off`, `desktop on\|off`, `motion on\|off\|auto\|sensitivity\|size\|idle` | The `settings.set` result |

Device-scoped reads (`addresses`, `notifications`, `sms threads`, and `dnd`) return an object keyed by paired device ID (`{}` when no device is paired).
With `--device`, `flux-cli` matches an exact ID first, else a paired device name without case.
No match returns `not_found`; more than 1 match returns `ambiguous`.

### Errors

With `--json`, a failure writes one JSON object to stdout, writes the message to stderr, and exits with status 1 (including for an unknown command; without `--json`, an unknown command exits with 2):

```json
{"error":{"code":"offline","message":"Pixel 8 is offline"}}
```

| Code | Meaning |
| --- | --- |
| Socket error codes | The code from `fluxd`, such as `offline`, `not_paired`, `ambiguous`, `not_found`, `no_device`, `not_supported`, `already_active`, `too_soon`, `bad_params`, `bad_setting`, `too_large`, `timeout`, `not_ready`, `launch_failed`, `not_saved`, or `unknown_method`. See [IPC errors](ipc.md#errors). |
| `daemon_off` | `fluxd` is turned off (`flux-cli on` starts it). |
| `daemon_down` | `fluxd` is not running or does not answer on its socket. |
| `daemon_unreachable` | `flux-cli` cannot connect to the socket path (for example wrong owner or mode). |
| `bad_args` | A required argument is missing, or an argument to a new command is invalid. |
| `unknown_command` | The command name is not known. |
| `no_json` | The command refuses `--json`. |
| `not_found` | `--device` matched no paired device on a device-scoped read, or `fluxd` returned `not_found`. |
| `error` | Any other CLI error, including usage errors from earlier commands. |

### Refusals

With `--json`, these commands return `no_json` before any socket call or child process:

- `open`, `pair`, `accept`, `reject`, `unpair`
- `setup`, `doctor`, `update`, `approve`, `browser`
- `on`, `off`, `version` (`--version`), `help` (`-h`, `--help`)
- `notify --run` (`notify Title --run` sends `--run` as the notification body and is not refused)

`watch` already prints JSON lines and ignores `--json`.

### Partial success

Some errors come after the action took effect, such as a setting change followed by a failing state read, or `launch_failed` from `transfer.open`.
Read the state again before you retry.
