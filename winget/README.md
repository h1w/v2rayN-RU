# Publishing v2rayN-RU to winget

The package identifier is **`h1w.v2rayN-RU`**. winget manifests live in Microsoft's
community repo [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs);
"publishing" means opening a PR there. Every submission is auto-validated and
reviewed by Microsoft moderators before merge (hours–days).

Once a user has it installed: `winget install h1w.v2rayN-RU`, `winget upgrade`, etc.

## Prerequisites (one-time)

1. **Stable releases.** winget wants non-prerelease releases. The release workflow now
   publishes stable releases (`prerelease: false`), and the asset names are
   `v2rayN-RU-windows-64.zip` / `v2rayN-RU-windows-arm64.zip`.
2. **PAT secret `PT_WINGET`.** Create a GitHub **classic** personal access token with the
   `public_repo` scope and add it under **Settings → Secrets and variables → Actions**
   of `h1w/v2rayN-RU` as `PT_WINGET`. `wingetcreate` uses it to fork winget-pkgs (under
   the token owner's account) and open the PR. *Only you can create this — it is your token.*

## First submission (manual, once)

`wingetcreate update` (used by the CI workflow) only works for a package that already
exists in winget-pkgs, so the **first** version must be submitted by hand.

1. Cut a stable release (e.g. `1.2.2`) so the assets exist:
   `https://github.com/h1w/v2rayN-RU/releases/download/1.2.2/v2rayN-RU-windows-64.zip`
2. Generate the manifests (computes SHA256 from the released zips):
   ```bash
   ./winget/generate.sh 1.2.2
   ```
   Output: `winget/out/manifests/h/h1w/v2rayN-RU/1.2.2/` with 3 YAML files.
3. Validate locally (on Windows, with winget installed):
   ```powershell
   winget validate --manifest winget/out/manifests/h/h1w/v2rayN-RU/1.2.2
   ```
4. Submit — either of:
   - **wingetcreate** (opens the PR for you):
     ```powershell
     wingetcreate submit --token <YOUR_PAT> winget/out/manifests/h/h1w/v2rayN-RU/1.2.2
     ```
   - **Manual PR**: copy the folder into a fork of `microsoft/winget-pkgs` under
     `manifests/h/h1w/v2rayN-RU/1.2.2/` and open a PR.

   Alternatively, skip `generate.sh` entirely and let wingetcreate build the manifests
   from the URLs the first time:
   ```powershell
   wingetcreate new https://github.com/h1w/v2rayN-RU/releases/download/1.2.2/v2rayN-RU-windows-64.zip
   ```
   (then fill the prompts; identifier `h1w.v2rayN-RU`, nested exe `v2rayN-windows-64\v2rayN-RU.exe`.)

Microsoft moderators review the PR; once merged, the package is live.

## Ongoing releases (automatic)

After the first version is live, `.github/workflows/winget-publish.yml` runs on every
stable release (`release: released`) and calls `wingetcreate update h1w.v2rayN-RU`,
opening the update PR automatically. It submits x64 always and arm64 if that asset is
present. You can also run it manually via **workflow_dispatch** with a version input.

## Notes

- The zips are **self-contained** (.NET bundled), so the manifest declares **no**
  runtime dependency (unlike upstream `2dust.v2rayN`, which depends on the .NET Desktop
  Runtime).
- `RequireExplicitUpgrade: true` — the app self-updates, so winget won't auto-upgrade it.
- The nested exe path is `v2rayN-windows-64\v2rayN-RU.exe` (arm64: `v2rayN-windows-arm64\…`);
  the zip's internal folder name is intentionally kept as `v2rayN-windows-<arch>`.
