{
  description = "Runic editor extensions development environment";
  # Locked to the same nixpkgs revision as runic-translations-sdk, so both shells share their store paths.
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
  outputs = { self, nixpkgs }:
    let systems = [ "x86_64-linux" "aarch64-linux" ]; in {
      devShells = nixpkgs.lib.genAttrs systems (system:
        let
          pkgs = import nixpkgs { inherit system; };
          # global.json disables SDK roll-forward and CI installs `.node-version`; nix/toolchain provides exactly
          # those versions (from the official binaries while nixpkgs is older) and fails evaluation otherwise.
          toolchain = import ./nix/toolchain { inherit pkgs; };
          dotnet = toolchain.dotnet-sdk;
          inherit (toolchain) nodejs;
          # Bun from the official release archive; the hashes match the release's SHASUMS256.txt.
          bunArchive =
            if system == "x86_64-linux" then
              {
                platform = "linux-x64";
                hash = "sha256-SQxqWbkwuEx42tLBeD8BdpDH1bxUasS1yOkkDULCz3U=";
              }
            else
              {
                platform = "linux-aarch64";
                hash = "sha256-NhWc7T3/G+CA3TE/q/vQR5oOPfBLPgZ6Exdqa1CjD5w=";
              };
          bunSource = pkgs.fetchzip {
            url = "https://github.com/oven-sh/bun/releases/download/bun-v1.4.4/bun-${bunArchive.platform}.zip";
            inherit (bunArchive) hash;
          };
          bun_1_4_4 = pkgs.stdenvNoCC.mkDerivation {
            pname = "bun";
            version = "1.4.4";
            src = bunSource;
            nativeBuildInputs = [ pkgs.autoPatchelfHook ];
            buildInputs = [ pkgs.stdenv.cc.cc.lib ];
            dontBuild = true;
            installPhase = ''
              mkdir -p "$out/bin"
              cp bun "$out/bin/bun"
              chmod +x "$out/bin/bun"
            '';
          };
        in {
          # .NET runs the language server tool (`dotnet tool restore`) and cross-compiles the Visual Studio
          # extension; Node and Bun build, test and package the VS Code extension; Python runs the VSIX checks.
          # The VS Code host test uses an installed `code` (or VSCODE_EXECUTABLE_PATH), which is not part of
          # this shell. actionlint is nixpkgs' version; CI pins its own (.github/workflows/ci.yml).
          default = pkgs.mkShell {
            packages = [ pkgs.git dotnet nodejs bun_1_4_4 pkgs.python3 pkgs.actionlint pkgs.shellcheck ];
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_ROOT = "${dotnet}/share/dotnet";
            shellHook = ''export NUGET_PACKAGES="$PWD/.cache/nuget"'';
          };
        });
    };
}
