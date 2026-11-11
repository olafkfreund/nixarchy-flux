self:
{
  config,
  lib,
  pkgs,
  ...
}:
let
  cfg = config.services.flux;
  core = cfg.package.core or cfg.package;
  host = {
    name = "org.omarchy.flux";
    description = "Flux host for the Flux browser extension";
    path = "${config.home.profileDirectory}/bin/flux-native";
    type = "stdio";
  };
  chromiumManifest = builtins.toJSON (
    host // { allowed_origins = [ "chrome-extension://jgfibbdalckcbmdcngbalobbhcbepamm/" ]; }
  );
  geckoManifest = builtins.toJSON (host // { allowed_extensions = [ "flux@omarchy.org" ]; });
in
{
  options.services.flux = {
    enable = lib.mkEnableOption "Flux, which connects this computer to your phone";

    package = lib.mkOption {
      type = lib.types.package;
      default = self.packages.${pkgs.stdenv.hostPlatform.system}.flux;
      defaultText = lib.literalExpression "flux.packages.\${pkgs.stdenv.hostPlatform.system}.flux";
      description = "The Flux package. Use the flux package of the flake, not a wrapped one.";
    };

    runtimePackages = lib.mkOption {
      type = lib.types.listOf lib.types.package;
      default = with pkgs; [
        pipewire
        pulseaudio
        ffmpeg
        wtype
        mpv
        gpu-screen-recorder
        wf-recorder
        libxkbcommon
        iproute2
        openssh
        wl-clipboard
        xdg-utils
      ];
      defaultText = lib.literalExpression "[ pkgs.pipewire pkgs.ffmpeg … ]";
      description = "Programs that fluxd runs. They go on the user profile, and fluxd is not wrapped.";
    };

    plugin.enable = lib.mkOption {
      type = lib.types.bool;
      default = true;
      description = "Copy the Omarchy plugin once if it is missing. fluxd keeps it current after that.";
    };

    browser = {
      enable = lib.mkEnableOption "the Flux browser extension host: the native messaging manifests and the unpacked extension";
      chromium = lib.mkOption {
        type = lib.types.listOf lib.types.str;
        default = [
          "chromium"
          "google-chrome"
          "BraveSoftware/Brave-Browser"
        ];
        description = "Folders in the XDG config home of the Chromium browsers that get the host manifest.";
      };
      firefox = lib.mkOption {
        type = lib.types.bool;
        default = true;
        description = "Write the host manifest for Firefox.";
      };
      zen = lib.mkOption {
        type = lib.types.bool;
        default = false;
        description = "Write the host manifest for Zen.";
      };
    };
  };

  config = lib.mkMerge [
    (lib.mkIf cfg.browser.enable {
      # services.flux.enable already installs cfg.package, which holds the
      # core; adding the core again would give the profile 2 copies of each
      # file.
      home.packages = lib.optional (!cfg.enable) core;

      # A copy: Chromium saves the realpath of an unpacked extension, and a
      # link would save a store path that garbage collection removes.
      home.activation.fluxBrowserExtension = lib.hm.dag.entryAfter [ "writeBoundary" ] ''
        # The Chromium folder and the Firefox one, whose manifest.json is the
        # Gecko manifest.
        for name in browser-extension browser-extension-firefox; do
          dir=${lib.escapeShellArg "${config.xdg.dataHome}/flux"}/$name
          $DRY_RUN_CMD rm -rf "$dir"
          $DRY_RUN_CMD mkdir -p "$(dirname "$dir")"
          $DRY_RUN_CMD cp -rLT --no-preserve=mode ${core}/share/flux/$name "$dir"
          $DRY_RUN_CMD chmod -R u+w "$dir"
        done
      '';

      xdg.configFile = lib.listToAttrs (
        map (
          b: lib.nameValuePair "${b}/NativeMessagingHosts/org.omarchy.flux.json" { text = chromiumManifest; }
        ) cfg.browser.chromium
      );

      home.file =
        lib.optionalAttrs cfg.browser.firefox {
          ".mozilla/native-messaging-hosts/org.omarchy.flux.json".text = geckoManifest;
        }
        // lib.optionalAttrs cfg.browser.zen {
          ".zen/native-messaging-hosts/org.omarchy.flux.json".text = geckoManifest;
        };
    })
    (lib.mkIf cfg.enable {
      home.packages = [ cfg.package ] ++ cfg.runtimePackages;

      systemd.user.services.fluxd = {
        Unit = {
          Description = "Flux daemon that connects this computer to your phone";
          PartOf = [ "graphical-session.target" ];
          After = [ "graphical-session.target" ];
          # fluxd restarts itself with exit 75 once idle, and is never wrapped.
          X-RestartIfChanged = false;
        };
        Service = {
          Type = "exec";
          ExecStart = "${config.home.profileDirectory}/bin/fluxd";
          ExecReload = "${pkgs.coreutils}/bin/kill -HUP $MAINPID";
          Restart = "on-failure";
          RestartSec = 2;
          SuccessExitStatus = 75;
          RestartForceExitStatus = 75;
        };
        Install.WantedBy = [ "graphical-session.target" ];
      };

      # A copy, not xdg.configFile: plugin.Sync refuses a linked folder, and
      # fluxd writes into the folder.
      home.activation.fluxPlugin = lib.mkIf cfg.plugin.enable (
        lib.hm.dag.entryAfter [ "writeBoundary" ] ''
          dir=${lib.escapeShellArg "${config.xdg.configHome}/omarchy/plugins/flux"}
          if [ ! -e "$dir" ] && [ ! -L "$dir" ]; then
            $DRY_RUN_CMD mkdir -p "$(dirname "$dir")"
            $DRY_RUN_CMD cp -rLT --no-preserve=mode ${core}/share/flux/omarchy-plugin "$dir"
            $DRY_RUN_CMD chmod -R u+w "$dir"
          fi
        ''
      );
    })
  ];
}
