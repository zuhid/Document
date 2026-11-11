# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A document storage service: an ASP.NET Core (.NET 10) REST API (`Document/`, assembly/namespace `Zuhid.Document`) backed by Azure Blob Storage, an xUnit integration test project (`Document.Tests/`), and a Vue 3 + Vite frontend (`Web/`). Solution file is `Document.slnx`.

## Commands

```sh
# Local blob storage emulator (required for running the API and the tests)
docker run --name azurite --detach -p 10000:10000 -p 10001:10001 -p 10002:10002 \
  mcr.microsoft.com/azure-storage/azurite:latest \
  azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0 --skipApiVersionCheck

dotnet tool restore                  # restores reportgenerator (dotnet-tools.json)
dotnet build Document.slnx
dotnet run --project Document        # API on http://localhost:5104

dotnet test Document.Tests                                             # all tests
dotnet test Document.Tests --filter "FullyQualifiedName~Put_Replaces"  # single test

# Web (from Web/)
npm install
npm run dev      # http://localhost:5202 (strictPort — must match the API's CorsOrigin)
npm run build
```

`scripts.sh` bundles these as shell functions (`docker_install_azurite`, `solution_initilize`, `update_dotnet_packages`, `run_test`); uncomment the one to run in its "execute" section. `run_test Document.Tests` runs tests with coverlet coverage and generates an HTML report in `Document.Tests/TestResults/CoverageReport/`.

## Architecture

**No database — the blob name is the metadata.** Each document is stored as `{guid}_{originalFileName}`, optionally under a folder path: `{a/b/c}/{guid}_{originalFileName}`. `DocumentController.ToModel` parses names back into `DocumentModel`; blobs whose last segment doesn't parse are silently skipped. Lookups by id enumerate the whole container and match the `{id}_` prefix on the last segment, so id routes find a document at any folder depth.

**Per-project containers.** Every route starts with `/{project}`. A project maps to a blob container of the same (lowercased) name, but only names in the `Projects` setting are accepted — anything else returns 404 on every verb, which prevents callers from creating arbitrary containers.

**Configuration** (`Document/appsettings.json`), parsed in `Program.cs` into the `DocumentSetting` singleton:
- `Storage:ConnectionString` — defaults to `UseDevelopmentStorage=true` (Azurite).
- `Projects` — `;`-separated, lowercased.
- `AllowedExtensions` — `;`-separated, leading dot added if missing; empty means all types allowed.
- `CorsOrigin` — `;`-separated; must include the Vite dev origin `http://localhost:5202`.

**Routing subtleties** (documented in detail at the top of `DocumentController.cs`): GET/POST use catch-all `{**folder}` routes, which also match an empty remainder, so one action handles both "whole project" and "folder" cases. Catch-alls have the lowest precedence, so `{id:guid}` and `{id:guid}/download` win when they match — which is why a folder's *first* segment may not be a guid (later segments may, e.g. record ids). Folder paths are normalized (lowercased, empty segments dropped, regex `^[a-z0-9][a-z0-9_-]*$`, max 10 segments / 256 chars) by `NormalizeFolder`. `PUT` replaces a file in place, keeping the id and folder and deleting the old blob.

**Error contract.** Validation 400s return `{ "file": ["message"] }`; `Web/src/api/documents.js` relies on this shape to surface messages. Keep it when adding validation.

`Document/Controllers/DocumentController.md` is the full API reference (endpoints, folder rules, responses) — update it alongside controller changes. Note its curl examples use port 5103 while the launch profile uses 5104, and `Document.http` is a stale template.

## nginx proxy

`nginx/default.conf` runs nginx on port 5105 (`docker_install_nginx` in `scripts.sh`, `--network host` because Kestrel binds to localhost only) and exposes **only the `demo` project**: `/demo` and `/demo/...` go to the API on 5104, and anything else gets a 404 from nginx. The `proxy_pass` targets include the URI on purpose, so the upstream always receives the normalized `/demo...` path and tricks like `/demo/../crm` resolve to `/crm` and get blocked. `client_max_body_size` is set to match Kestrel's ~28.6 MB body limit.

## Tests

Tests are end-to-end through the real HTTP pipeline via `WebApplicationFactory<Program>` (`DocumentApiFactory`) and **require Azurite to be running**. Each fixture creates a unique `test-{guid}` project/container (deleted on dispose) with `AllowedExtensions` limited to `.pdf;.txt`, so tests should only assert on folders/ids they created. Settings are injected with `UseSetting` rather than `ConfigureAppConfiguration` because `Program` reads config during the builder phase.

## Frontend

`Web/` is a single-page Vue 3 app (`App.vue` → `pages/DocumentsPage.vue`) using `src/api/documents.js` as the API client. The API base URL defaults to `http://localhost:5104` and can be overridden with `VITE_API_URL`. `@` aliases `Web/src`.

## Git workflow

Work happens on `dev`; `commit.sh` stages everything, commits with a timestamp message, and pushes. `main` receives squash merges from `dev` (`git checkout main && git merge --squash dev`) with a descriptive message.
