# 0002. Build, version and release in GitHub Actions

Status: accepted (2026-11-09)

## Decision

1. **CI builds everything.** The repository is public, so GitHub's Windows
   runners cost nothing. `ci.yml` runs on every pull request and every push
   to `main`:
   - `core`: `dotnet test` of `LLMTray.Core` on Ubuntu;
   - `app (x64)` on `windows-latest` and `app (ARM64)` on `windows-11-arm`:
     the Core tests again, then the app built as an unsigned MSIX. The MSIX
     is kept as a run artifact for 7 days.

   Nothing has to build on a developer's Mac. Core can still be tested there
   with the .NET 10 SDK.

2. **Releases come from tags.** Pushing a `v*` tag (admins only) runs
   `release.yml`. It builds signed MSIX packages for x64 and ARM64 and
   creates the GitHub release. `vX.Y.Z-beta.N` is a pre-release and
   `vX.Y.Z` is stable, the same as in the Mac app.

3. **Package versions.** MSIX wants four numbers, each ≤ 65535, and the
   Store wants the last one to be 0. A tag maps to
   `Major.Minor.(Patch × 1000 + N).0`. N is the beta number (1–998), or
   999 for stable:

   | Tag | Package version |
   |---|---|
   | `v0.1.0-beta.1` | `0.1.1.0` |
   | `v0.1.0-beta.12` | `0.1.12.0` |
   | `v0.1.0` | `0.1.999.0` |
   | `v0.1.1-beta.1` | `0.1.1001.0` |

   The betas of a version sort below its stable build, which sorts below the
   next patch's betas, so App Installer always sees an upgrade. Patch can go
   up to 64. `LLMTray.Core.ReleaseTag` holds the mapping, and
   `tools/LLMTray.Version` prints it for the workflow.

4. **Self-signed until there is a public certificate.** Open question 2 in
   ADR 0001 (Azure Artifact Signing or an OV certificate) needs Microsoft
   accounts for the company, which don't exist yet. Until then:
   - The packages are signed with a self-signed certificate,
     `CN=IPSupport LLC`. Its key and password live in the repository secrets
     `MSIX_CERT_PFX_BASE64` and `MSIX_CERT_PASSWORD`.
   - Each release carries the public part, `LLMTray-signing.cer`. The
     release notes tell testers to put it in Local Machine > Trusted People
     before installing.
   - The manifest's `Publisher` must equal the certificate's subject. A
     public certificate will have a longer subject (O, L, S, C), and that
     changes the package family name. Testers will then uninstall the
     self-signed builds once and install the new one; App Installer won't
     upgrade across publishers.

   No stable release goes out with the self-signed certificate.

## Why

- A local Windows machine or VM is still missing. Building on the runners
  from the first commit means the app is built the same way for every PR and
  every release, with nothing taken from a developer's machine.
- The Store's last-part-is-zero rule rules out using the fourth number for
  the beta. Putting the beta into the third number keeps one scheme for
  Direct and Store builds.
- A self-signed beta is installable today and costs nothing. The cost is
  one manual certificate step for testers, plus one reinstall when the
  public certificate arrives.

## Not yet

- `.appinstaller` update feeds (ADR 0001, Updates): with the app's first
  real features.
- NOTICE and license texts in the package: with the first bundled
  third-party binary (llama.cpp).
