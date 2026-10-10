# Building a release and publishing on GitHub

For whoever builds FreeMon and publishes releases. Everything runs on a Linux build server
(tested with openSUSE Tumbleweed; Debian / Ubuntu / RHEL work the same, only package installation commands differ).

## Output

`./release.sh` creates `release/<version>/`:

| Path | What |
| --- | --- |
| `assets/freemon-<version>-win-x64-setup.exe` | Windows installer |
| `assets/freemon-<version>-win-x64-portable.zip` | Windows portable |
| `assets/freemon_<version>-1_amd64.deb` | Debian / Ubuntu package |
| `assets/freemon-<version>-1.x86_64.rpm` | RHEL / Fedora / openSUSE package |
| `assets/freemon-<version>-linux-x64-portable.tar.gz` | Linux portable |
| `assets/SHA256SUMS` | checksums of all files |
| `source/` | project files to upload to GitHub |
| `RELEASE_NOTES.md` | release description template |
| `build/` | intermediate files (not needed on GitHub) |

## Tools and why Podman

| What | Tool | Source |
| --- | --- | --- |
| the program (Linux and Windows) | .NET 10 SDK (`dotnet publish`) | installed once (below) |
| `.deb` and `.rpm` | nFPM 2.47.0 (MIT) | `release.sh` downloads it into `.tools/` and verifies the checksum |
| Windows installer | NSIS (`makensis`) | **not in the official openSUSE repositories** |
| archives | `tar`, `python3` | already in the system |

**Why Podman.** `makensis`, the Windows installer compiler, is available for openSUSE only from third-party repositories.
Instead of adding them to the server, `release.sh` runs `makensis` in a throw-away Debian container (`--rm`), where NSIS is a
regular package. Nothing but Podman itself is installed on the server. If `makensis` is installed (e.g. Debian / Ubuntu:
`sudo apt install nsis`), Podman is not needed. Portainer is not needed for building — it is only a web UI for containers.

## Server preparation (once)

1. **.NET 10 SDK** into a tools folder, without changing the system (example for `/appdev`):
   ```
   mkdir -p /appdev/tools
   curl -fL -o /appdev/tools/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
   bash /appdev/tools/dotnet-install.sh --channel 10.0 --install-dir /appdev/tools/dotnet
   ```
   `/appdev/env.sh` (picked up automatically by `build.sh` and `release.sh` when the project is in `/appdev/freemon`):
   ```
   export DOTNET_ROOT=/appdev/tools/dotnet
   export PATH="$DOTNET_ROOT:$PATH"
   export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
   ```
   Check: `. /appdev/env.sh && dotnet --version` — must print `10.0.…`.
2. **Podman:** `sudo zypper install podman`. Check: `podman --version`.
3. **The project** in `/appdev/freemon`.

## Building a release

1. Raise `<Version>` in `FreeMon.csproj`. The release number is taken from there; if `release/<version>` already exists
   the script stops and overwrites nothing.
2. Make sure the program builds and runs: `./build.sh linux-x64`.
3. Build the release (the argument is the repository URL, it goes into the package metadata):
   ```
   ./release.sh https://github.com/<user>/freemon
   ```
   The first run takes longer: nFPM and the Debian image for NSIS are downloaded.
4. Check the result:
   ```
   ls -lh release/<version>/assets
   cd release/<version>/assets && sha256sum -c SHA256SUMS
   ```
5. Preferably test the packages on test machines: openSUSE — `sudo zypper install ./freemon-<version>-1.x86_64.rpm`,
   Debian / Ubuntu — `sudo apt install ./freemon_<version>-1_amd64.deb`, Windows — run `setup.exe`.

**Limits of building on Linux:** `freemon.exe` has no icon and no version details in its file properties (shortcuts and
the installer do have the icon). A fully correct exe is produced only by `build.cmd` on Windows. The program is not
code-signed — Windows SmartScreen shows a warning.

## First publication on GitHub (browser)

1. **Repository:** github.com → "+" → **New repository** → name `freemon`, **Public**, do **not** tick README / .gitignore /
   license → **Create repository**.
2. **Code:** on the empty repository page click **"uploading an existing file"**. Drag **all contents** of
   `release/<version>/source/` into the browser window (including the hidden `.gitignore` and the `src`, `assets`, `lang`,
   `packaging`, `docs` folders). Message `FreeMon <version>` → **Commit changes**.
   Browser limits: up to 100 files at once and 25 MB per file — the project fits.
   If the files are on a server accessed over SSH, copy the folder to your computer first (`scp -r`, or Dolphin `sftp://`).
3. **Repository description:** ⚙ next to "About" → description, topics (`monitoring`, `ping`, `network`, `avalonia`, `dotnet`).

## GitHub release (browser)

1. **Releases → Create a new release** (or "Draft a new release").
2. **Choose a tag** → type `v<version>` (e.g. `v1.10.0`) → "Create new tag … on publish". Target `main`.
3. **Release title:** `FreeMon <version>`.
4. **Description:** paste `release/<version>/RELEASE_NOTES.md` and fill in "Changes".
5. **Attach binaries:** drag all files from `release/<version>/assets/` (6 files).
6. **Set as the latest release** → **Publish release**.

GitHub adds source code archives for the tag automatically.

## Next releases

1. Change the code → raise `<Version>` → `./release.sh …`.
2. Update the code on GitHub: **Add file → Upload files** in the repository root, drag the contents of the new `source/`
   (files with the same paths are replaced). Files no longer in the project are deleted manually on their page.
3. Create a new release with a new tag (see above).

Once the project lives in git on the server, code updates and releases will be done with commands
(`git push`, `gh release create`) instead of the browser — the next step after the move.
