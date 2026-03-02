{
  description = "Cloudflare Dynamic DNS updater";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
  };

  outputs = { self, nixpkgs }:
    let
      supportedSystems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAllSystems = nixpkgs.lib.genAttrs supportedSystems;
      pkgsFor = system: nixpkgs.legacyPackages.${system};
    in
    {
      packages = forAllSystems (system:
        let
          pkgs = pkgsFor system;
        in
        {
          default = pkgs.buildDotnetModule {
            pname = "cloudflare-dynamic-dns";
            version = "1.0.3";

            src = ./.;

            projectFile = "CloudflareDynamicDns.csproj";
            nugetDeps = ./deps.json;

            dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
            dotnet-runtime = pkgs.dotnetCorePackages.runtime_10_0;

            meta = with pkgs.lib; {
              description = "Update a Cloudflare DNS A record with the machine's public IP";
              homepage = "https://github.com/bruno-garcia/cloudflare-dynamic-dns";
              license = licenses.mit;
              mainProgram = "CloudflareDynamicDns";
            };
          };
        });

      devShells = forAllSystems (system:
        let
          pkgs = pkgsFor system;
        in
        {
          default = pkgs.mkShell {
            buildInputs = [
              pkgs.dotnetCorePackages.sdk_10_0
              pkgs.nuget-to-json
            ];
          };
        });
    };
}
