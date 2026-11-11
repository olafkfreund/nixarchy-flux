{
  self,
  nixpkgs,
  system,
}:
let
  pkgs = nixpkgs.legacyPackages.${system};
  inherit (nixpkgs) lib;
  packages = self.packages.${system};
  core = packages.flux-core;

  nixos =
    approve: extra:
    nixpkgs.lib.nixosSystem {
      modules = [
        self.nixosModules.default
        {
          nixpkgs.hostPlatform = system;
          services.flux = {
            enable = true;
            users = [
              "alice"
              "bob"
            ];
            approve.services = approve;
            webcam.enable = true;
          };
          boot.loader.grub.enable = false;
          fileSystems."/" = {
            device = "none";
            fsType = "ext4";
          };
          system.stateVersion = "25.11";
        }
        extra
      ];
    };

  good = (nixos [ "sudo" ] { }).config;
  bad = (nixos [ "sshd" ] { }).config;
  # enableGnomeKeyring adds unix-early, a password prompt before unix.
  keyring = (nixos [ "sudo" ] { security.pam.services.sudo.enableGnomeKeyring = true; }).config;

  # Reads only text and options. It never forces system.build.toplevel,
  # which would build v4l2loopback and a whole system.
  pamLines = config: lib.splitString "\n" config.security.pam.services.sudo.text;
  # the index of the first match, or the length when nothing matches
  indexOf =
    lines: p:
    (lib.foldl'
      (acc: l: if acc.found || p l then acc // { found = true; } else acc // { n = acc.n + 1; })
      {
        n = 0;
        found = false;
      }
      lines
    ).n;
  # flux must come before the first pam_unix auth line, and that line must exist
  fluxFirst =
    config:
    let
      lines = pamLines config;
      fluxAt = indexOf lines (l: lib.hasInfix "flux-approve" l);
      unixAt = indexOf lines (l: lib.hasPrefix "auth" l && lib.hasInfix "pam_unix.so" l);
    in
    fluxAt < unixAt && unixAt < lib.length lines;
  unit = good.systemd.user.units."fluxd.service".text;
  failing = config: lib.filter (a: !a.assertion) config.assertions;

  nixosChecked =
    assert lib.assertMsg (fluxFirst good)
      "the flux-approve PAM line must come before the pam_unix.so auth line";
    assert lib.assertMsg (fluxFirst keyring)
      "with unix-early, the flux-approve PAM line must come before the first pam_unix.so auth line";
    assert lib.assertMsg (lib.hasInfix "/run/current-system/sw/bin" unit)
      "unit PATH must reach the system profile, since NixOS sets PATH explicitly";
    assert lib.assertMsg (lib.hasInfix "-ffmpeg-" unit) "unit PATH must include the runtime packages";
    assert lib.assertMsg (lib.hasInfix "ConditionUser=|alice" unit) "unit lacks ConditionUser=|alice";
    assert lib.assertMsg (lib.hasInfix "ConditionUser=|bob" unit) "unit lacks ConditionUser=|bob";
    assert lib.assertMsg (lib.hasInfix "ExecStart=/run/current-system/sw/bin/fluxd" unit)
      "unit ExecStart must be the profile path";
    assert lib.assertMsg good.services.avahi.publish.userServices "avahi userServices is off";
    assert lib.assertMsg good.services.avahi.settings.publish.publish-addresses
      "avahi addresses is off";
    assert lib.assertMsg (failing good == [ ]) "a good config fails an assertion";
    assert lib.assertMsg (failing bad != [ ]) "approve.services = [ sshd ] must fail an assertion";
    true;

  # Structural only: stubs stand in for Home Manager, so this proves the
  # unit flag and the copy command, not that Home Manager accepts them.
  hmLib = lib.extend (
    _: _: {
      hm.dag.entryAfter = after: data: { inherit after data; };
    }
  );
  stub =
    { lib, ... }:
    {
      options = {
        home.packages = lib.mkOption {
          type = lib.types.listOf lib.types.package;
          default = [ ];
        };
        home.activation = lib.mkOption {
          type = lib.types.attrsOf lib.types.anything;
          default = { };
        };
        home.profileDirectory = lib.mkOption {
          type = lib.types.str;
          default = "/home/alice/.nix-profile";
        };
        xdg.configHome = lib.mkOption {
          type = lib.types.str;
          default = "/home/alice/.config";
        };
        systemd.user.services = lib.mkOption {
          type = lib.types.attrsOf lib.types.anything;
          default = { };
        };
      };
    };
  hm =
    (lib.evalModules {
      specialArgs.lib = hmLib;
      modules = [
        stub
        (self.homeManagerModules.default)
        {
          _module.args.pkgs = pkgs;
          services.flux.enable = true;
        }
      ];
    }).config;
  hmChecked =
    assert lib.assertMsg (
      hm.systemd.user.services.fluxd.Unit.X-RestartIfChanged == false
    ) "HM unit must set X-RestartIfChanged false";
    assert lib.assertMsg (lib.hasInfix "cp -rLT" hm.home.activation.fluxPlugin.data)
      "HM activation must copy with cp -rLT";
    assert lib.assertMsg (
      hm.systemd.user.services.fluxd.Service.ExecStart == "/home/alice/.nix-profile/bin/fluxd"
    ) "HM ExecStart must be the profile path";
    true;
in
packages
// {
  nixos-module = pkgs.runCommand "flux-nixos-module-check" { } ''
    echo ${lib.boolToString nixosChecked} > $out
  '';

  hm-module = pkgs.runCommand "flux-hm-module-check" { } ''
    echo ${lib.boolToString hmChecked} > $out
  '';

  ldflags = pkgs.runCommand "flux-ldflags-check" { nativeBuildInputs = [ pkgs.binutils ]; } ''
    strings ${core}/bin/flux-cli | grep -qF '${core}/lib/flux/flux-approve'
    out_text=$(HOME=$TMPDIR ${core}/bin/flux-cli update 2>&1 || true)
    echo "$out_text" | grep -q 'installed by Nix'
    touch $out
  '';
}
