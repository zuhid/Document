# DocumentController

REST API for storing, listing, serving, replacing, and deleting documents in Azure Blob Storage.
No database is involved — the blob name itself carries all metadata:

- **Flat (unfoldered):** `{guid}_{original file name}`
- **Foldered (any depth):** `{any/depth/of/folders}/{guid}_{original file name}`

The `{guid}` is the document id used in the API routes. Document-id routes find a document at any depth, so a foldered document is viewed, downloaded, replaced, and deleted with the same id routes as a flat one.

Configuration (`Projects`, `AllowedExtensions`, connection string) and the storage layout are documented in the root [README.md](../../README.md).

## Features

- **Per-project containers** — every route includes a project name (e.g. `crm`, `demo`). Each project gets its own blob container, and all operations (list, view, download, replace, delete) are scoped to that container. One project can never see or touch another project's files, even with a known document id.
- **Dynamic folders** — a document can be uploaded under an arbitrary folder path (`POST /{project}/{folder...}`, e.g. `control/string/{recordId}`) and listed scoped to that folder. Replacing a foldered document keeps it in its folder. See [Folder rules](#folder-rules).
- **Controlled container creation** — only project names listed in the `Projects` setting are accepted. Any other name returns `404`, so callers cannot create arbitrary containers.
- **File type restriction** — uploads and replacements are validated against the `AllowedExtensions` setting.
- **Inline viewing and downloads** — documents can be streamed inline (browsers render PDFs/images/text directly) or downloaded with their original file name. Range requests are supported for both.
- **Safe file names** — uploaded file names are sanitized (path stripped, invalid characters replaced).

## Endpoints

All routes follow `/{project}`, where `{project}` must be one of the configured `Projects` (case-insensitive).

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/{project}` | List all documents in the project's container. |
| `GET` | `/{project}/{folder...}` | List only the documents under that folder path (any depth). |
| `GET` | `/{project}/{id}` | View a document inline. |
| `GET` | `/{project}/{id}/download` | Download a document with its original file name. |
| `POST` | `/{project}` | Upload a flat document (`multipart/form-data`, field name `file`). |
| `POST` | `/{project}/{folder...}` | Upload a document under a folder path (`multipart/form-data`, field name `file`). |
| `PUT` | `/{project}/{id}` | Replace an existing document's file; a foldered document stays in its folder. |
| `DELETE` | `/{project}/{id}` | Delete a document. |

`{folder...}` is a catch-all — every remaining path segment forms the folder path. It also matches an empty remainder, so the bare `/{project}` rows and the `/{project}/{folder...}` rows are served by the same action per verb: no folder means list all (GET) or flat upload (POST). Catch-all routes have the lowest precedence, so the document-id routes (`/{id}`, `/{id}/download`) always win when they match.

### Folder rules

A folder path is normalized and validated the same way on both folder routes:

- Split on `/`, empty segments dropped, each segment lowercased; segments rejoined with `/` form the canonical folder.
- Every segment must match `^[a-z0-9][a-z0-9_-]*$`.
- The first segment must not parse as a guid (a single-segment guid folder would be unreachable behind the view-by-id route). Later segments may be guids — that is how record ids appear in paths, e.g. `control/string/9b2f0a3c-...`.
- At most 10 segments and 256 characters total (after normalization).
- An empty result (e.g. the path was all slashes) or any violation returns `400 { "file": ["Invalid folder path."] }`.

### Examples (curl)

```sh
# upload from CRM
curl -F "file=@somefile.pdf" http://localhost:5103/crm

# upload from Demo
curl -F "file=@somefile.pdf" http://localhost:5103/demo

# upload under a folder path (e.g. the "control/string" page and one record id)
curl -F "file=@somefile.pdf" http://localhost:5103/crm/control/string/9b2f0a3c-8b86-d011-b42d-00cf4fc964ff

# list CRM documents
curl http://localhost:5103/crm

# list only the documents under that folder
curl http://localhost:5103/crm/control/string/9b2f0a3c-8b86-d011-b42d-00cf4fc964ff

# view inline (browsers render PDFs/images directly)
curl http://localhost:5103/crm/{id}

# download with original file name
curl -OJ http://localhost:5103/crm/{id}/download

# replace the file for an existing document
curl -X PUT -F "file=@newfile.pdf" http://localhost:5103/crm/{id}

# delete
curl -X DELETE http://localhost:5103/crm/{id}
```

### Responses

**Upload / replace / list item** (`DocumentModel`):

```json
{
  "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
  "fileName": "somefile.pdf",
  "length": 12345,
  "modified": "2026-07-24T12:00:00Z",
  "folder": "control/string/9b2f0a3c-8b86-d011-b42d-00cf4fc964ff"
}
```

`folder` is the document's full folder path, or `null` for flat documents.

**Errors:**

| Status | When |
| --- | --- |
| `400 { "file": ["..."] }` | File type not in `AllowedExtensions` (message lists the allowed types). |
| `400 { "file": ["Invalid folder path."] }` | Folder route with a path that fails the [folder rules](#folder-rules). |
| `400` | No file, or empty file, in the request. |
| `404` | Unknown project name, or no document with that id in the project's container. |
