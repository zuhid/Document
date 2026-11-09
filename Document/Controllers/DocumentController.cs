using System.Text.RegularExpressions;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Zuhid.Document.Models;

namespace Zuhid.Document.Controllers;

// Route precedence: the catch-all "{**folder}" routes have the LOWEST precedence in ASP.NET
// routing, so every other route wins automatically whenever it can match:
// - GET/POST have no empty-template routes: the catch-all also matches an empty remainder
//   (folder = null), so one action per verb handles both the bare project URL (list all /
//   flat upload) and folder paths.
// - GET "/{project}/{id:guid}" (single guid-constrained segment) beats the catch-all for a
//   single-guid path — which is also why a folder's FIRST segment must not be a guid: a
//   one-segment guid folder would be unreachable behind the view-by-id route.
// - GET "/{project}/{id:guid}/download" (guid segment + literal) beats the catch-all for
//   "/{guid}/download"; a non-guid first segment such as "/x/download" falls through to the
//   catch-all as folder "x/download", which is a valid folder list.
// - PUT/DELETE "/{project}/{id:guid}" have no catch-all counterparts, so nothing to shadow.
[ApiController]
[Route("{project}")]
public partial class DocumentController(BlobServiceClient service, DocumentSetting setting) : ControllerBase
{
    private const int MaxFolderSegments = 10;
    private const int MaxFolderLength = 256;

    private static readonly FileExtensionContentTypeProvider s_contentTypeProvider = new();

    /// <summary>List all documents for a project, or only those under a folder path of any depth.</summary>
    [HttpGet("{**folder}")]
    public async Task<ActionResult<List<DocumentModel>>> Get(string project, string? folder)
    {
        var container = GetContainer(project);
        if (container == null)
        {
            return NotFound();
        }

        // An absent or empty catch-all means list everything; anything else must normalize.
        var normalized = string.IsNullOrEmpty(folder) ? null : NormalizeFolder(folder);
        if (!string.IsNullOrEmpty(folder) && normalized == null)
        {
            return BadRequest(new { file = new[] { "Invalid folder path." } });
        }

        return await ListBlobs(container, normalized == null ? null : $"{normalized}/");
    }

    /// <summary>View a document inline — browsers render PDFs, images, text directly.</summary>
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Get(string project, Guid id) => StreamBlob(project, id, asAttachment: false);

    /// <summary>Download a document as an attachment with its original file name.</summary>
    [HttpGet("{id:guid}/download")]
    public Task<IActionResult> Download(string project, Guid id) => StreamBlob(project, id, asAttachment: true);

    /// <summary>Upload a document, flat or under a folder path of any depth (multipart/form-data, field name "file").</summary>
    [HttpPost("{**folder}")]
    public async Task<ActionResult<DocumentModel>> Post(string project, string? folder, IFormFile file)
    {
        var container = GetContainer(project);
        if (container == null)
        {
            return NotFound();
        }

        // An absent or empty catch-all means a flat upload; anything else must normalize.
        var normalized = string.IsNullOrEmpty(folder) ? null : NormalizeFolder(folder);
        if (!string.IsNullOrEmpty(folder) && normalized == null)
        {
            return BadRequest(new { file = new[] { "Invalid folder path." } });
        }

        return await Upload(container, file, normalized);
    }

    /// <summary>Replace an existing document's file (multipart/form-data, field name "file").</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<DocumentModel>> Put(string project, Guid id, IFormFile file)
    {
        var container = GetContainer(project);
        if (container == null)
        {
            return NotFound();
        }

        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded.");
        }

        var fileName = SanitizeFileName(file.FileName);
        var typeError = ValidateFileType(fileName);
        if (typeError != null)
        {
            return typeError;
        }

        var existing = await FindBlobNames(container, id);
        if (existing.Count == 0)
        {
            return NotFound();
        }

        // The replacement stays in the same folder (at any depth) as the existing blob.
        var folder = GetFolder(existing[0]);
        var blob = container.GetBlobClient(BuildBlobName(folder, id, fileName));
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = GetContentType(fileName, file.ContentType) },
        };
        await using (var stream = file.OpenReadStream())
        {
            await blob.UploadAsync(stream, options);
        }

        foreach (var name in existing.Where(n => n != blob.Name))
        {
            await container.DeleteBlobIfExistsAsync(name);
        }

        return Ok(new DocumentModel(id, fileName, file.Length, DateTime.UtcNow, folder));
    }

    /// <summary>Delete a document.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(string project, Guid id)
    {
        var container = GetContainer(project);
        if (container == null)
        {
            return NotFound();
        }

        var existing = await FindBlobNames(container, id);
        if (existing.Count == 0)
        {
            return NotFound();
        }

        foreach (var name in existing)
        {
            await container.DeleteBlobIfExistsAsync(name);
        }

        return Ok(new { id });
    }

    private async Task<List<DocumentModel>> ListBlobs(BlobContainerClient container, string? prefix)
    {
        var list = new List<DocumentModel>();
        if (!await container.ExistsAsync())
        {
            return list;
        }

        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, HttpContext.RequestAborted))
        {
            var model = ToModel(blob);
            if (model != null)
            {
                list.Add(model);
            }
        }

        return list.OrderBy(n => n.FileName).ToList();
    }

    private async Task<ActionResult<DocumentModel>> Upload(BlobContainerClient container, IFormFile file, string? folder)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file uploaded.");
        }

        var fileName = SanitizeFileName(file.FileName);
        var typeError = ValidateFileType(fileName);
        if (typeError != null)
        {
            return typeError;
        }

        var id = Guid.NewGuid();
        await container.CreateIfNotExistsAsync();

        var blob = container.GetBlobClient(BuildBlobName(folder, id, fileName));
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = GetContentType(fileName, file.ContentType) },
        };
        await using var stream = file.OpenReadStream();
        await blob.UploadAsync(stream, options);

        return Ok(new DocumentModel(id, fileName, file.Length, DateTime.UtcNow, folder));
    }

    // Empty AllowedExtensions in appsettings means every file type is accepted.
    // The error shape { file: [message] } matches what the web app's apiClient expects for 400s.
    private BadRequestObjectResult? ValidateFileType(string fileName)
    {
        if (setting.AllowedExtensions.Length == 0)
        {
            return null;
        }

        var extension = Path.GetExtension(fileName);
        if (setting.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        return BadRequest(new { file = new[] { $"File type \"{extension}\" is not allowed. Allowed types: {string.Join(", ", setting.AllowedExtensions)}" } });
    }

    // Each project gets its own blob container. Only names listed in the Projects setting are
    // allowed — anything else is rejected, so callers cannot create arbitrary containers.
    private BlobContainerClient? GetContainer(string project) =>
        setting.Projects.Contains(project, StringComparer.OrdinalIgnoreCase)
            ? service.GetBlobContainerClient(project.ToLowerInvariant())
            : null;

    // Document ids are unique guids per container, so a full enumeration finds a document at any
    // depth — flat ("{id}_...") or foldered ("{any/depth}/{id}_..."). Containers are small.
    private async Task<List<string>> FindBlobNames(BlobContainerClient container, Guid id)
    {
        var names = new List<string>();
        if (!await container.ExistsAsync())
        {
            return names;
        }

        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, null, HttpContext.RequestAborted))
        {
            if (GetLastSegment(item.Name).StartsWith($"{id}_"))
            {
                names.Add(item.Name);
            }
        }

        return names;
    }

    private async Task<IActionResult> StreamBlob(string project, Guid id, bool asAttachment)
    {
        var container = GetContainer(project);
        if (container == null)
        {
            return NotFound();
        }

        if (!await container.ExistsAsync())
        {
            return NotFound();
        }

        await foreach (var item in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, null, HttpContext.RequestAborted))
        {
            if (!GetLastSegment(item.Name).StartsWith($"{id}_"))
            {
                continue;
            }

            var fileName = GetFileName(item.Name);
            var contentType = GetContentType(fileName, item.Properties.ContentType);
            var stream = await container.GetBlobClient(item.Name).OpenReadAsync();
            return asAttachment
                ? File(stream, contentType, fileName, enableRangeProcessing: true)
                : File(stream, contentType, enableRangeProcessing: true);
        }

        return NotFound();
    }

    // Blobs are named "{guid}_{original file name}", optionally under a folder path of any
    // depth, so no database is needed. Anything whose last segment doesn't parse is skipped.
    private static DocumentModel? ToModel(BlobItem blob)
    {
        var slash = blob.Name.LastIndexOf('/');
        var parts = blob.Name[(slash + 1)..].Split('_', 2);
        if (parts.Length != 2 || parts[1].Length == 0 || !Guid.TryParse(parts[0], out var id))
        {
            return null;
        }

        var folder = slash < 0 ? null : blob.Name[..slash];
        return new DocumentModel(id, parts[1], blob.Properties.ContentLength ?? 0, blob.Properties.LastModified?.UtcDateTime ?? default, folder);
    }

    // Splits on '/', drops empty segments, and lowercases. Every segment must match the segment
    // regex; the FIRST segment must not parse as a guid (see the route-precedence comment on the
    // class) — later segments may be guids, which is how record ids appear in paths. Depth is
    // capped at 10 segments and 256 chars total. Returns the canonical "a/b/c", or null if invalid.
    private static string? NormalizeFolder(string folder)
    {
        var segments = folder.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.ToLowerInvariant())
            .ToList();
        if (segments.Count == 0 || segments.Count > MaxFolderSegments)
        {
            return null;
        }

        if (Guid.TryParse(segments[0], out _) || segments.Any(n => !FolderSegmentRegex().IsMatch(n)))
        {
            return null;
        }

        var normalized = string.Join('/', segments);
        return normalized.Length <= MaxFolderLength ? normalized : null;
    }

    private static string BuildBlobName(string? folder, Guid id, string fileName) =>
        folder == null ? $"{id}_{fileName}" : $"{folder}/{id}_{fileName}";

    // "{folder path}/{guid}_{fileName}" → "{folder path}"; a flat blob name → null.
    private static string? GetFolder(string blobName)
    {
        var slash = blobName.LastIndexOf('/');
        return slash < 0 ? null : blobName[..slash];
    }

    private static string GetLastSegment(string blobName) => blobName[(blobName.LastIndexOf('/') + 1)..];

    private static string GetFileName(string blobName) => GetLastSegment(blobName).Split('_', 2)[^1];

    private static string GetContentType(string fileName, string? stored) =>
        !string.IsNullOrWhiteSpace(stored) && stored != "application/octet-stream"
            ? stored
            : s_contentTypeProvider.TryGetContentType(fileName, out var contentType) ? contentType : "application/octet-stream";

    // Fixed set rather than Path.GetInvalidFileNameChars(), which is OS-dependent (on Linux it is
    // only '\0' and '/'). Users download these files on any OS, so use the strictest (Windows) rules.
    private static readonly char[] s_invalidFileNameChars =
        [.. "\"<>|:*?\\/", .. Enumerable.Range(0, 32).Select(i => (char)i)];

    private static string SanitizeFileName(string fileName)
    {
        // Strip any client-supplied path, whether it uses '/' or '\' separators.
        var name = fileName[(fileName.LastIndexOfAny(['/', '\\']) + 1)..];
        foreach (var invalid in s_invalidFileNameChars)
        {
            name = name.Replace(invalid, '-');
        }

        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*$")]
    private static partial Regex FolderSegmentRegex();
}
