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
  };

  config = lib.mkIf cfg.enable {
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
  };
}
