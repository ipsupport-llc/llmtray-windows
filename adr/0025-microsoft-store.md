# 0025. Microsoft Store releases from CI

Status: accepted (2026-11-16, the maintainer asked for Store deployment from
CI/CD). Fills in "The Store build" left open in adr/0002 and the Store row
of adr/0001.

## Decision

- **The same packages.** The Store build is not built again. The release's
  unsigned x64 and ARM64 MSIX packages (the `package` job's artifacts) are
  unpacked, given the identity Partner Center assigned to the app, packed
  again, bundled and wrapped as one `.msixupload`
  (`scripts/store-package.ps1`, the Windows SDK's `makeappx`). Only three
  values of the manifest change: `Identity/@Name`, `Identity/@Publisher`
  (`CN=<GUID>`) and `PublisherDisplayName`. The version is the release's
  (adr/0002, `X.Y.(Z×1000+N).0`). Nothing is signed: the Store signs what it
  accepts.
- **The identity is public**, so it lives in repository variables:
  `STORE_PACKAGE_NAME`, `STORE_PUBLISHER`, `STORE_PUBLISHER_DISPLAY_NAME`,
  and `STORE_PRODUCT_ID` (the Store ID). The script checks their shape
  before writing anything. Until any is set, the Store jobs are skipped;
  once one is, a release fails until all four are (a half-done setup is
  never a silent skip).
- **`store-package`** runs for a stable tag and for a dry run
  (workflow_dispatch). Its artifact `store` holds the `.msixupload`, which
  is also what the maintainer uploads by hand for the first submission.
- **`store-submit`** runs only for a pushed stable tag, after the GitHub
  release is published, in the `store` environment:
  - the environment's required reviewer (the maintainer) approves each run
    in GitHub, so nothing reaches the Store without a person's click. The
    job's first step reads the environment's protection rules and stops if
    it has no required reviewer. Who the reviewers are is the environment's
    setting: the maintainer only;
  - its secrets are the Partner Center app's: `PARTNER_CENTER_TENANT_ID`,
    `PARTNER_CENTER_SELLER_ID`, `PARTNER_CENTER_CLIENT_ID`,
    `PARTNER_CENTER_CLIENT_SECRET` (an Entra app added to Partner Center
    with the Manager role);
  - the Microsoft Store Developer CLI (`msstore` v0.4.4, `msstore publish
    <the .msixupload> --appId <Store ID>`) makes a new submission from the
    last published one (listing, pricing and the rest kept), puts this
    package in it and commits it to certification;
  - one run at a time across tags.
- **Betas don't go to the Store.** They stay on GitHub, Scoop and the
  app's beta channel. Package flights can come later.
- **In the app**, a copy installed from the Store (its package's signature
  kind is Store) never updates itself: Settings › Updates says the Store
  keeps it up to date, the updater never starts (adr/0003), and the setup
  wizard doesn't offer update choices. The `store` artifact installed by
  hand for testing isn't Store-signed: it's for Partner Center, not for
  daily use.

## What the maintainer does once

1. Partner Center: reserve the name, and make the first submission by hand
   with the `store` artifact of a dry run or a stable release (the
   submission API needs one submission made in Partner Center), with the
   listing, age rating, privacy policy and the `runFullTrust` reason
   (adr/0001, Store).
2. Copy Product identity (Package/Identity/Name, Publisher,
   PublisherDisplayName) and the Store ID into the repository variables.
3. Entra ID: an app registration with a client secret; Partner Center ›
   Account settings › User management › Microsoft Entra applications: add
   it with the Manager role. Put the tenant, seller (Partner Center's
   Seller ID), client ID and secret into the `store` environment's
   secrets, and make the maintainer its required reviewer.

## Open

- **Major version 0.** adr/0002 asked whether the Store takes a package
  version starting with 0. The first submission answers it. If not, the
  Store stamping step adds an offset to the major number; Direct keeps the
  mapping.
- **Downloaded executables** (adr/0001, Store): the CUDA runtime and the
  runners the app downloads on opt-in. Certification of the first
  submission tells whether policy 10.2.2 allows them with the downloads
  disclosed in the listing.
