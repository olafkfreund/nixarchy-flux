# NixOS and Home Manager

[Documentation index](README.md)

The repository includes a Nix flake for `x86_64-linux` and `aarch64-linux`.
It builds Flux from source and provides a NixOS module and a Home Manager module.

## Add the flake input

Add `flux` to the `inputs` of your `flake.nix`:

```nix
{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flux = {
      url = "github:olafkfreund/nixarchy-flux";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };
}
```

## NixOS module

Import `inputs.flux.nixosModules.default` and configure `services.flux`:

```nix
{ inputs, ... }:
{
  imports = [ inputs.flux.nixosModules.default ];

  services.flux = {
    enable = true;
    users = [ "alice" ];
    webcam.enable = true;
    approve.services = [ "sudo" ];
    openFirewall = false;
  };
}
```

The module installs `flux`, the runtime tools, the udev rule, and Avahi discovery.
`services.flux.users` enables `fluxd.service` only for the listed accounts with `ConditionUser`.
`services.flux.webcam.enable` loads `v4l2loopback` and sets `services.flux.webcam.moduleOptions` (default `"devices=0"`).
`services.flux.approve.services` adds the PAM rule for fingerprint approval before `pam_unix`.
Only `"sudo"` and `"hyprlock"` are supported.
`polkit-1` is not supported on NixOS because polkit 126 and later run the agent helper as a system service that cannot reach the socket of `fluxd`.
`services.flux.openFirewall` opens TCP ports 12100 to 12108 and UDP port 12100 when set to `true`.
`fluxd` also connects outbound to paired devices, so most setups leave `openFirewall` as `false`.

## Home Manager module

Import `inputs.flux.homeManagerModules.default` in a user configuration:

```nix
{ inputs, ... }:
{
  imports = [ inputs.flux.homeManagerModules.default ];

  services.flux = {
    enable = true;
    plugin.enable = true;
  };
}
```

The Home Manager module installs `flux`, adds the runtime tools to `home.packages`, defines the `fluxd` user service, and copies the Omarchy shell plugin into `~/.config/omarchy/plugins/flux` when that folder does not exist yet.
`fluxd` updates the plugin files on each start.

Each user must choose one way to run `fluxd.service`.
Use either `services.flux.users` in the NixOS module or `services.flux.enable` in the Home Manager module for a given user, not both.
When Home Manager manages the user service, leave `services.flux.users = [ ]` on NixOS and keep `services.flux.enable = true` on NixOS for Avahi, udev, webcam, and PAM.

## Package outputs and apps

| Output | Contents |
| --- | --- |
| `packages.<system>.flux` (`default`) | Combined package with `flux-core` and `flux-gui`. |
| `packages.<system>.flux-core` | `fluxd`, `flux-cli`, static `flux-approve`, Omarchy plugin assets, udev rule, desktop entry, and icons. |
| `packages.<system>.flux-gui` | Native Qt6 desktop window (`flux-gui`). |
| `apps.<system>.flux-cli` | CLI target for `nix run`. |
| `overlays.default` | Overlay that adds `flux`, `flux-core`, and `flux-gui`. |

To run a check from the repository without installing it first:

```sh
nix run .#flux-cli -- doctor
```

The short name `flux` is not on `PATH` on Nix, because `fluxcd` owns it; use `flux-cli`.

## CLI commands on a Nix build

| Command | Behavior on Nix |
| --- | --- |
| `flux-cli setup` | Copies and enables the Omarchy shell plugin when needed, leaves `fluxd.service` to the NixOS or Home Manager module, and prints the `services.flux` options for system features. |
| `flux-cli update` | Refuses to install a release package and tells you to update the flake input and rebuild, while `--check` and `--phone` still work. |
| `flux-cli approve` | `enroll`, `status`, and `remove` manage the user key in `/etc/flux/approve/<user>.pub`, while `enable` and `disable` point to `services.flux.approve.services` because the module owns PAM. |

`/etc/flux/approve` is mutable user state, not store configuration.
After you enable `services.flux.approve.services`, run `sudo flux-cli approve enroll` once per user to pair the phone key.

## Rebuilds and active transfers

A rebuild does not restart `fluxd.service` or cut an active transfer.
The service runs `/run/current-system/sw/bin/fluxd` (or the Home Manager profile path) with `restartIfChanged = false`.
When a rebuild switches the profile to a new Flux build, the running `fluxd` detects the new binary on disk.
It waits while a file transfer, camera or audio stream, remote desktop session, Browse PC session, APK send, or fingerprint approval is active.
Once idle, `fluxd` exits with status 75 and systemd starts the new version.
