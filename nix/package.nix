{
  lib,
  buildGoModule,
  go_1_27,
  stdenv,
  cmake,
  ninja,
  qt6,
  symlinkJoin,
  file,
  version,
}:
let
  fs = lib.fileset;

  flux-core = (buildGoModule.override { go = go_1_27; }) (finalAttrs: {
    pname = "flux-core";
    inherit version;

    src = fs.toSource {
      root = ../.;
      fileset = fs.unions [
        ../go.mod
        ../go.sum
        ../cmd
        ../internal
        ../scripts
        ../browser
        ../gui/omarchy
        ../gui/qml
        ../dist
        ../LICENSE
      ];
    };

    vendorHash = "sha256-nNqh6FrnnA60w24T+TzFSYB2cLqG+v4sB2NpHQ7e1+c=";

    subPackages = [
      "cmd/fluxd"
      "cmd/flux"
      "cmd/flux-native"
    ];

    ldflags = [
      "-s"
      "-w"
      "-X main.version=${version}"
      "-X main.packager=nix"
      "-X flux/internal/approve.HelperPath=${placeholder "out"}/lib/flux/flux-approve"
    ];

    # The PAM helper is static, so that it depends on no shared library.
    postBuild = ''
      CGO_ENABLED=0 go build -trimpath -ldflags "''${ldflags[*]}" \
        -o "$GOPATH/bin/flux-approve" ./cmd/flux-approve
    '';

    # shortcut: these tests need git, /bin/sh, /usr/bin, a real HOME or timing
    # the build sandbox lacks; re-check each when the sandbox gets them.
    checkFlags = [
      "-skip=^(TestDesktopEndsAfterAnUnpair|TestDesktopEndsWhenThePhoneClosesTheStream|TestDesktopOverlappingStartsWaitForTheOldRecorder|TestDesktopStartFloodRunsOneRecorder|TestDesktopStartWaitsForTheOldRecorder|TestEnroll|TestExtendedScreen|TestHelperApproves|TestHelperCancelsWhenStopped|TestHelperDenied|TestHelperRefusesAChangedField|TestHelperRefusesAReplay|TestHelperRefusesAWrongKey|TestHelperStopsAtOnce|TestHelperTimeoutFromFluxd|TestHelperTimesOut|TestHerdrCreate|TestHerdrCreateCloseOnFailure|TestHerdrFollowsNamedSessions|TestKeyboardReleasesMods|TestPolkitNeedsSetuidHelper|TestReadKey|TestReadKeyRefusesOtherContent|TestReviewShowsStagedAndUntrackedFiles|TestRootInstall|TestRunCommandBackground|TestRunCommandOutput|TestSpeakerSession)$"
    ];

    checkPhase = ''
      runHook preCheck
      go test $checkFlags ./cmd/... ./internal/... ./scripts/...
      runHook postCheck
    '';

    postInstall = ''
      # buildGoModule names the binary after its directory. bin/flux stays
      # absent because the fluxcd package owns that name.
      mv $out/bin/flux $out/bin/flux-cli
      install -Dm755 $out/bin/flux-approve $out/lib/flux/flux-approve
      rm $out/bin/flux-approve
      install -dm755 $out/lib/flux/bin
      ln -sfn ../../../bin/flux-cli $out/lib/flux/bin/flux

      plugin=$out/share/flux/omarchy-plugin
      mkdir -p $plugin/Flux
      for f in manifest.json Service.qml Backend.qml BarWidget.qml Panel.qml; do
        install -m644 gui/omarchy/$f $plugin/$f
      done
      (cd gui/qml && find . -path ./tools -prune -o -type f \
        \( -name '*.qml' -o -name qmldir -o -name '*.js' \) -print |
        while read -r f; do install -Dm644 "$f" "$plugin/Flux/$f"; done)

      # As the Makefile's install-extension, with a version a browser accepts
      # (N.N.N: Chromium and Firefox reject the "+" of the Nix version).
      ext=$out/share/flux/browser-extension
      extVersion=$(echo '${version}' | sed -E 's/^v?([0-9]+\.[0-9]+\.[0-9]+).*/\1/; t; s/.*/0.0.0/')
      (cd browser/extension && find . -type f ! -name 'manifest*.json' \
        -exec install -Dm644 {} "$ext"/{} \;)
      for m in manifest.json manifest.firefox.json; do
        sed "s/\"version\": *\"[^\"]*\"/\"version\": \"$extVersion\"/" browser/extension/$m > "$ext/$m"
      done

      install -Dm644 dist/61-flux-v4l2loopback.rules $out/lib/udev/rules.d/61-flux-v4l2loopback.rules
      install -Dm644 dist/flux.desktop $out/share/applications/flux.desktop
      install -Dm644 dist/flux.svg $out/share/icons/hicolor/scalable/apps/flux.svg
      install -Dm644 dist/flux-symbolic.svg $out/share/icons/hicolor/symbolic/apps/flux-symbolic.svg
    '';

    nativeInstallCheckInputs = [ file ];
    doInstallCheck = true;
    installCheckPhase = ''
      file $out/lib/flux/flux-approve | grep -q 'statically linked'
      $out/bin/flux-cli version | grep -q +
    '';

    passthru.core = finalAttrs.finalPackage;

    meta = {
      description = "Connect this computer to your phone";
      license = lib.licenses.mit;
      mainProgram = "flux-cli";
      platforms = lib.platforms.linux;
    };
  });

  flux-gui = stdenv.mkDerivation {
    pname = "flux-gui";
    inherit version;

    src = fs.toSource {
      root = ../.;
      fileset = fs.unions [
        ../gui/app
        ../gui/qml
        ../dist
        ../LICENSE
      ];
    };

    # The cmake hook runs from build/, so the source is one level up.
    cmakeDir = "../gui/app";
    cmakeFlags = [ "-DFLUX_VERSION=${version}" ];

    nativeBuildInputs = [
      cmake
      ninja
      qt6.wrapQtAppsHook
    ];
    buildInputs = with qt6; [
      qtbase
      qtdeclarative
      qtsvg
      qtwayland
    ];

    meta = {
      description = "Qt6 app of Flux";
      license = lib.licenses.mit;
      mainProgram = "flux-gui";
      platforms = lib.platforms.linux;
    };
  };

  flux = symlinkJoin {
    name = "flux-${version}";
    paths = [
      flux-core
      flux-gui
    ];
    passthru.core = flux-core;
    meta.mainProgram = "flux-cli";
  };
in
{
  inherit flux-core flux-gui flux;
}
