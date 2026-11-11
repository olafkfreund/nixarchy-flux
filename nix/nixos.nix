self:
{
  config,
  lib,
  pkgs,
  ...
}:
let
  cfg = config.services.flux;
  # flux is a symlinkJoin, so the helper path comes from the core package.
  # PAM must get the same text that is baked into HelperPath.
  core = cfg.package.core or cfg.package;
  helper = "${core}/lib/flux/flux-approve";
  allowedApprove = [
    "sudo"
    "hyprlock"
  ];
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
      description = "Programs that fluxd runs. They go on the system path, and fluxd is not wrapped.";
    };

    users = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ ];
      example = [ "alice" ];
      description = "Users that get the fluxd user service. Leave empty to use the Home Manager module.";
    };

    openFirewall = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Open TCP 12100-12108 and UDP 12100 for the phone.";
    };

    webcam = {
      enable = lib.mkEnableOption "the v4l2loopback module for the phone as a webcam";
      moduleOptions = lib.mkOption {
        type = lib.types.nullOr lib.types.str;
        default = "devices=0";
        description = "Options of the v4l2loopback module. Null sets none.";
      };
    };

    approve.services = lib.mkOption {
      type = lib.types.listOf lib.types.str;
      default = [ ];
      example = [ "sudo" ];
      description = "PAM services that the phone can approve. Only sudo and hyprlock work.";
    };
  };

  config = lib.mkIf cfg.enable (
    lib.mkMerge [
      {
        assertions = [
          {
            assertion = lib.all (s: lib.elem s allowedApprove) cfg.approve.services;
            message = "services.flux.approve.services accepts only ${lib.concatStringsSep " and " allowedApprove}.";
          }
        ];

        environment.systemPackages = [ cfg.package ] ++ cfg.runtimePackages;
        services.udev.packages = [ core ];

        services.avahi = {
          enable = lib.mkDefault true;
          publish = {
            enable = lib.mkDefault true;
            userServices = lib.mkDefault true;
          };
          settings.publish.publish-addresses = lib.mkDefault true;
        };

        networking.firewall = lib.mkIf cfg.openFirewall {
          allowedTCPPortRanges = [
            {
              from = 12100;
              to = 12108;
            }
          ];
          allowedUDPPorts = [ 12100 ];
        };

        # fluxd is never wrapped or restarted by a switch: it compares argv[0]
        # with /proc/self/exe, and it restarts itself with exit 75 once idle.
        systemd.user.services.fluxd = lib.mkIf (cfg.users != [ ]) {
          description = "Flux daemon that connects this computer to your phone";
          partOf = [ "graphical-session.target" ];
          after = [ "graphical-session.target" ];
          wantedBy = [ "graphical-session.target" ];
          unitConfig.ConditionUser = map (u: "|" + u) cfg.users;
          restartIfChanged = false;
          # NixOS writes an explicit PATH into the unit, which hides the user
          # manager's PATH, so the tools and the desktop's own programs
          # (hyprctl, omarchy-shell) are named here.
          path = cfg.runtimePackages ++ [
            "/run/wrappers"
            "/etc/profiles/per-user/%u"
            "/run/current-system/sw"
          ];
          serviceConfig = {
            Type = "exec";
            ExecStart = "/run/current-system/sw/bin/fluxd";
            ExecReload = "${pkgs.coreutils}/bin/kill -HUP $MAINPID";
            Restart = "on-failure";
            RestartSec = 2;
            SuccessExitStatus = 75;
            RestartForceExitStatus = 75;
          };
        };

        security.pam.services = lib.genAttrs cfg.approve.services (s: {
          rules.auth.flux = {
            control = "sufficient";
            modulePath = "${pkgs.pam}/lib/security/pam_exec.so";
            args = [
              "quiet"
              "stdout"
              helper
            ];
            # Before the first password prompt: unix-early when an option
            # such as enableGnomeKeyring turns it on, else unix.
            order =
              let
                auth = config.security.pam.services.${s}.rules.auth;
              in
              (if auth ? unix-early && auth.unix-early.enable then auth.unix-early.order else auth.unix.order)
              - 10;
          };
        });
      }

      (lib.mkIf cfg.webcam.enable {
        boot.extraModulePackages = [ config.boot.kernelPackages.v4l2loopback ];
        boot.kernelModules = [ "v4l2loopback" ];
        boot.extraModprobeConfig = lib.mkIf (
          cfg.webcam.moduleOptions != null
        ) "options v4l2loopback ${cfg.webcam.moduleOptions}";
      })
    ]
  );
}
