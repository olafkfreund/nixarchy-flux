{
  description = "Flux: connect this computer to your phone";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { self, nixpkgs }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAll = nixpkgs.lib.genAttrs systems;
      version = "${import ./nix/version.nix}+${self.shortRev or "dirty"}";
      build =
        pkgs:
        let
          p = pkgs.callPackage ./nix/package.nix { inherit version; };
        in
        {
          inherit (p) flux-core flux-gui flux;
        };
    in
    {
      packages = forAll (
        system:
        let
          p = build nixpkgs.legacyPackages.${system};
        in
        p // { default = p.flux; }
      );

      apps = forAll (system: {
        flux-cli = {
          type = "app";
          program = "${self.packages.${system}.flux-core}/bin/flux-cli";
        };
      });

      overlays.default = final: _prev: build final;

      nixosModules.default = import ./nix/nixos.nix self;
      homeManagerModules.default = import ./nix/home-manager.nix self;

      checks = forAll (system: import ./nix/checks.nix { inherit self nixpkgs system; });

      formatter = forAll (system: nixpkgs.legacyPackages.${system}.nixfmt);
    };
}
