{
  description = "C# dev shell for Unity decomp sources";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
  let
    system = "x86_64-linux";
    pkgs = import nixpkgs { inherit system; };
  in
  {
    devShells.${system}.default = pkgs.mkShell {
      packages = [
        pkgs.dotnet-sdk_8
        pkgs.msbuild
        pkgs.mono
        pkgs.nuget
        pkgs.git
      ];

      # helps some tools find SDKs consistently
      DOTNET_ROOT = "${pkgs.dotnet-sdk_8}";
      DOTNET_CLI_TELEMETRY_OPTOUT = "1";
      DOTNET_NOLOGO = "1";
    };
  };
}