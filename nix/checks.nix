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
    assert lib.assertMsg (
      !lib.hasInfix "PATH=" unit
    ) "the unit must not set PATH, so fluxd gets the user manager's PATH";
    assert lib.assertMsg (lib.hasInfix "X-RestartIfChanged=false" unit)
      "the NixOS unit must not restart on a switch";
    # Force the package lists and the webcam module, so a renamed or
    # unsupported package fails the check instead of a user's rebuild.
    assert lib.assertMsg (builtins.deepSeq (map (p: p.drvPath) (
      good.environment.systemPackages ++ good.boot.extraModulePackages ++ good.services.udev.packages
    )) true) "packages do not evaluate";
    assert lib.assertMsg (lib.hasInfix "options v4l2loopback devices=0" good.boot.extraModprobeConfig)
      "webcam module options missing";
    assert lib.assertMsg (lib.any (
      p: lib.getName p == "ffmpeg"
    ) good.environment.systemPackages) "runtimePackages must be on the system path";
    assert lib.assertMsg (lib.any (
      l:
      lib.hasPrefix "auth sufficient" l
      && lib.hasInfix (builtins.unsafeDiscardStringContext "quiet stdout ${core}/lib/flux/flux-approve") l
    ) (pamLines good)) "the PAM rule must be sufficient and use the helper path baked into flux-cli";
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
        xdg.dataHome = lib.mkOption {
          type = lib.types.str;
          default = "/home/alice/.local/share";
        };
        xdg.configFile = lib.mkOption {
          type = lib.types.attrsOf lib.types.anything;
          default = { };
        };
        home.file = lib.mkOption {
          type = lib.types.attrsOf lib.types.anything;
          default = { };
        };
        systemd.user.services = lib.mkOption {
          type = lib.types.attrsOf lib.types.anything;
          default = { };
        };
      };
    };
  evalHm =
    extra:
    (lib.evalModules {
      specialArgs.lib = hmLib;
      modules = [
        stub
        (self.homeManagerModules.default)
        {
          _module.args.pkgs = pkgs;
          services.flux.enable = true;
        }
        extra
      ];
    }).config;
  hm = evalHm { };
  hmBrowser = evalHm { services.flux.browser.enable = true; };
  hmOnlyBrowser = evalHm {
    services.flux.enable = lib.mkForce false;
    services.flux.browser.enable = true;
  };
  chromiumFile = hmBrowser.xdg.configFile."chromium/NativeMessagingHosts/org.omarchy.flux.json";
  geckoFile = hmBrowser.home.file.".mozilla/native-messaging-hosts/org.omarchy.flux.json";
  hostPath = "/home/alice/.nix-profile/bin/flux-native";
  hostBase = {
    name = "org.omarchy.flux";
    description = "Flux host for the Flux browser extension";
    path = hostPath;
    type = "stdio";
  };
  hmChecked =
    assert lib.assertMsg (
      hm.systemd.user.services.fluxd.Unit.X-RestartIfChanged == false
    ) "HM unit must set X-RestartIfChanged false";
    assert lib.assertMsg (lib.hasInfix "cp -rLT" hm.home.activation.fluxPlugin.data)
      "HM activation must copy with cp -rLT";
    assert lib.assertMsg (
      hm.systemd.user.services.fluxd.Service.ExecStart == "/home/alice/.nix-profile/bin/fluxd"
    ) "HM ExecStart must be the profile path";
    assert lib.assertMsg (builtins.deepSeq (map (
      p: p.drvPath
    ) hm.home.packages) true) "HM packages do not evaluate";
    assert lib.assertMsg (
      builtins.fromJSON chromiumFile.text
      == hostBase // { allowed_origins = [ "chrome-extension://jgfibbdalckcbmdcngbalobbhcbepamm/" ]; }
    ) "HM Chromium manifest differs from the CLI's";
    assert lib.assertMsg (
      builtins.fromJSON geckoFile.text == hostBase // { allowed_extensions = [ "flux@omarchy.org" ]; }
    ) "HM Gecko manifest differs from the CLI's";
    assert lib.assertMsg (
      builtins.attrNames hmBrowser.xdg.configFile == [
        "BraveSoftware/Brave-Browser/NativeMessagingHosts/org.omarchy.flux.json"
        "chromium/NativeMessagingHosts/org.omarchy.flux.json"
        "google-chrome/NativeMessagingHosts/org.omarchy.flux.json"
      ]
    ) "HM Chromium manifests are not the 3 defaults";
    assert lib.assertMsg (
      builtins.attrNames hmBrowser.home.file
      == [ ".mozilla/native-messaging-hosts/org.omarchy.flux.json" ]
    ) "HM must write only the Firefox manifest by default";
    assert lib.assertMsg (
      let
        a = hmBrowser.home.activation.fluxBrowserExtension.data;
      in
      lib.hasInfix "/home/alice/.local/share/flux" a
      && lib.hasInfix "browser-extension-firefox" a
      && lib.hasInfix "cp -rLT" a
    ) "HM extension activation must copy into the data home";
    assert lib.assertMsg (
      lib.elem packages.flux hmBrowser.home.packages && !(lib.elem core hmBrowser.home.packages)
    ) "with services.flux.enable, HM browser must not add the core a second time";
    assert lib.assertMsg (
      hmOnlyBrowser.systemd.user.services == { } && lib.elem core hmOnlyBrowser.home.packages
    ) "HM browser alone must work without services.flux.enable";
    assert lib.assertMsg (
      hm.xdg.configFile == { } && hm.home.file == { } && !(hm.home.activation ? fluxBrowserExtension)
    ) "HM browser off must write no manifest and no extension step";
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

  browser-extension = pkgs.runCommand "flux-browser-extension-check" { } ''
    test -x ${core}/bin/flux-native
    m=${core}/share/flux/browser-extension
    test -f $m/manifest.json -a -f $m/manifest.firefox.json
    f=${core}/share/flux/browser-extension-firefox
    test -f $f/manifest.json -a ! -e $f/manifest.firefox.json
    grep -q '"gecko"' $f/manifest.json
    for x in $m/manifest.json $m/manifest.firefox.json $f/manifest.json; do
      v=$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' $x)
      if ! echo "$v" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then echo "bad version $v in $x"; exit 1; fi
    done
    touch $out
  '';

  ldflags = pkgs.runCommand "flux-ldflags-check" { nativeBuildInputs = [ pkgs.binutils ]; } ''
    strings ${core}/bin/flux-cli | grep -qF '${core}/lib/flux/flux-approve'
    test ! -e ${packages.flux}/bin/flux
    out_text=$(HOME=$TMPDIR ${core}/bin/flux-cli update 2>&1 || true)
    echo "$out_text" | grep -q 'installed by Nix'
    touch $out
  '';
}
