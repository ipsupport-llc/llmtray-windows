# Releasing LLMTray for Windows

Releases are built by `.github/workflows/release.yml` on a `v*` tag
(adr/0002). Tags are admin-only.

## One-time setup: the beta signing certificate

Until the company has a public code-signing certificate, betas are signed
with a self-signed one (`CN=IPSupport LLC`). Make it once and keep the key
only in the repository secrets:

```bash
umask 077
openssl req -x509 -newkey rsa:3072 -sha256 -days 730 -nodes \
  -keyout key.pem -out cert.pem -subj "/CN=IPSupport LLC" \
  -addext "basicConstraints=critical,CA:FALSE" \
  -addext "keyUsage=critical,digitalSignature" \
  -addext "extendedKeyUsage=codeSigning"
PASSWORD=$(openssl rand -base64 24)
openssl pkcs12 -export -inkey key.pem -in cert.pem -out sign.pfx -passout pass:"$PASSWORD"
base64 -i sign.pfx | gh secret set MSIX_CERT_PFX_BASE64 -R ipsupport-llc/llmtray-windows
printf %s "$PASSWORD" | gh secret set MSIX_CERT_PASSWORD -R ipsupport-llc/llmtray-windows
rm -P key.pem sign.pfx   # macOS; use shred on Linux
```

The subject must stay `CN=IPSupport LLC`: it has to equal the manifest's
`Publisher`, and the release workflow checks that. A new certificate
(another key) means testers trust the new `.cer` once.

## A beta

1. Merge to `main` through PRs (every check read as *pass*).
2. Tag `vX.Y.Z-beta.N` on `main` and push it.
3. `release.yml` tests Core, builds x64 and ARM64 MSIX packages with the
   pinned llama.cpp inside, signs them, builds the x64 and ARM64 portable
   ZIPs, and creates a GitHub pre-release with the packages, the ZIPs and
   `LLMTray-signing.cer`.

4. Bump the Scoop manifest (adr/0005) in a PR. Scoop's `checkver` reads the
   new tag and fills in each ZIP's URL and the SHA-256 GitHub lists for it
   (it downloads the ZIP only if GitHub lists none):

   ```powershell
   $env:SCOOP_GH_TOKEN = gh auth token   # GitHub's API allows 60 calls an hour without it
   & "$(scoop prefix scoop)\bin\checkver.ps1" -App llmtray -Dir bucket -Update
   ```

Stable tags fail until there is a public certificate.
