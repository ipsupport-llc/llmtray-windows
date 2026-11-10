# Releasing LLMTray for Windows

Releases are built by `.github/workflows/release.yml` on a `v*` tag
(adr/0002). Tags are admin-only.

## Signing (adr/0017)

Releases are signed with Azure Artifact Signing. The setup lives in Azure
and in the repository's `release` environment:

- **Azure:** an Artifact Signing account and a certificate profile, and an
  Entra app with a federated credential for this repository's `release`
  environment and the *Artifact Signing Certificate Profile Signer* role on
  the account.
- **`release` environment variables:** `AZURE_TENANT_ID`,
  `AZURE_CLIENT_ID`, `AZURE_SUBSCRIPTION_ID`, `SIGNING_ENDPOINT`,
  `SIGNING_ACCOUNT`, `SIGNING_PROFILE`.
- **`release` environment secret:** `MSIX_PUBLISHER`, the profile's
  certificate subject, exactly as Azure shows it. It becomes the MSIX
  `Publisher`.

Changing the profile to another identity changes the MSIX publisher, and
so the package identity (see adr/0017 for what that costs testers).

Before tagging a release that changes the build, run the workflow by hand
from `main` (Actions › Release › Run workflow, with the tag name): it
builds and signs everything and publishes nothing.

## A beta

1. Merge to `main` through PRs (every check read as *pass*).
2. Tag `vX.Y.Z-beta.N` on `main` and push it.
3. `release.yml` tests Core, builds x64 and ARM64 MSIX packages with the
   pinned llama.cpp inside, signs them (adr/0017), builds the x64 and ARM64 portable
   ZIPs and installers (adr/0007), and creates a GitHub pre-release with the
   packages, the ZIPs and the `-setup.exe` files, all signed.

4. The same run points the Scoop bucket
   ([ipsupport-llc/scoop-bucket](https://github.com/ipsupport-llc/scoop-bucket),
   adr/0005) at the new ZIPs: `scripts/bump-scoop.ps1` writes their SHA-256
   into `bucket/llmtray.json` there, pushed with the bucket's deploy key
   (secret `SCOOP_BUCKET_DEPLOY_KEY`). Nothing to do by hand.

Stable tags fail until there is a public certificate.
