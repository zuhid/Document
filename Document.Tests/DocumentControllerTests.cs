using System.Net;
using System.Net.Http.Json;
using System.Text;
using Zuhid.Document.Models;

namespace Zuhid.Document.Tests;

// End-to-end tests through the real HTTP pipeline (routing, model binding, validation) against
// the local Azurite emulator. The fixture's project/container is unique per run, so tests only
// assert on the folders and ids they created themselves.
public class DocumentControllerTests(DocumentApiFactory factory) : IClassFixture<DocumentApiFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    private string Project => factory.Project;

    [Fact]
    public async Task Post_Flat_ReturnsModelWithNoFolder()
    {
        var model = await Upload("contract.pdf", content: "12345");

        Assert.NotEqual(Guid.Empty, model.Id);
        Assert.Equal("contract.pdf", model.FileName);
        Assert.Equal(5, model.Length);
        Assert.Null(model.Folder);
    }

    [Fact]
    public async Task Post_Foldered_NormalizesFolderToLowercase()
    {
        var model = await Upload("a.txt", "Control/String");

        Assert.Equal("control/string", model.Folder);
    }

    [Fact]
    public async Task Post_FolderWithRecordIdSegment_IsAccepted()
    {
        var recordId = Guid.NewGuid();
        var model = await Upload("a.txt", $"records/{recordId}");

        Assert.Equal($"records/{recordId}", model.Folder);
    }

    [Fact]
    public async Task Post_SanitizesFileName()
    {
        var model = await Upload("we:ird.txt");

        Assert.Equal("we-ird.txt", model.FileName);
    }

    [Fact]
    public async Task Post_AllowedExtensionIsCaseInsensitive()
    {
        var model = await Upload("Report.PDF");

        Assert.Equal("Report.PDF", model.FileName);
    }

    [Theory]
    [InlineData("bad!folder")]
    [InlineData("6f9619ff-8b86-d011-b42d-00cf4fc964ff")]
    [InlineData("a/a/a/a/a/a/a/a/a/a/a")]
    public async Task Post_InvalidFolder_Returns400WithFileError(string folder)
    {
        var response = await client.PostAsync($"/{Project}/{folder}", File("a.txt"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid folder path.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_DisallowedExtension_Returns400WithFileError()
    {
        var response = await client.PostAsync($"/{Project}", File("run.exe"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not allowed", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Post_EmptyFile_Returns400()
    {
        var response = await client.PostAsync($"/{Project}", File("a.txt", content: ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_ListAll_ContainsUploadedDocument()
    {
        var model = await Upload("list-all.txt");

        var list = await client.GetFromJsonAsync<List<DocumentModel>>($"/{Project}");

        Assert.NotNull(list);
        Assert.Contains(list, n => n.Id == model.Id && n.FileName == "list-all.txt");
    }

    [Fact]
    public async Task Get_ListFolder_ReturnsOnlyThatFolderSortedByFileName()
    {
        await Upload("b.txt", "ordering/inner");
        await Upload("a.txt", "ordering/inner");
        await Upload("other.txt", "elsewhere");

        var list = await client.GetFromJsonAsync<List<DocumentModel>>($"/{Project}/ordering/inner");

        Assert.NotNull(list);
        Assert.Equal(["a.txt", "b.txt"], list.Select(n => n.FileName));
        Assert.All(list, n => Assert.Equal("ordering/inner", n.Folder));
    }

    [Theory]
    [InlineData("bad!folder")]
    [InlineData("6f9619ff-8b86-d011-b42d-00cf4fc964ff/sub")]
    public async Task Get_InvalidFolder_Returns400WithFileError(string folder)
    {
        var response = await client.GetAsync($"/{Project}/{folder}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid folder path.", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_ById_StreamsContentInline()
    {
        var model = await Upload("view.txt", content: "hello inline");

        var response = await client.GetAsync($"/{Project}/{model.Id}");

        response.EnsureSuccessStatusCode();
        Assert.Equal("hello inline", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_ById_FindsFolderedDocumentAtAnyDepth()
    {
        var model = await Upload("deep.txt", $"depth/{Guid.NewGuid()}/leaf", content: "deep content");

        var response = await client.GetAsync($"/{Project}/{model.Id}");

        response.EnsureSuccessStatusCode();
        Assert.Equal("deep content", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_Download_UsesOriginalFileName()
    {
        var model = await Upload("original.pdf", content: "pdf bytes");

        var response = await client.GetAsync($"/{Project}/{model.Id}/download");

        response.EnsureSuccessStatusCode();
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("original.pdf", response.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("pdf bytes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var response = await client.GetAsync($"/{Project}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_ReplacesFileKeepsIdAndFolderDeletesOldBlob()
    {
        var original = await Upload("old.txt", "replace", content: "v1");

        var response = await client.PutAsync($"/{Project}/{original.Id}", File("new.pdf", content: "v2!"));

        response.EnsureSuccessStatusCode();
        var replaced = await response.Content.ReadFromJsonAsync<DocumentModel>();
        Assert.NotNull(replaced);
        Assert.Equal(original.Id, replaced.Id);
        Assert.Equal("new.pdf", replaced.FileName);
        Assert.Equal("replace", replaced.Folder);

        var list = await client.GetFromJsonAsync<List<DocumentModel>>($"/{Project}/replace");
        Assert.NotNull(list);
        var item = Assert.Single(list);
        Assert.Equal("new.pdf", item.FileName);
        Assert.Equal(3, item.Length);
    }

    [Fact]
    public async Task Put_UnknownId_Returns404()
    {
        var response = await client.PutAsync($"/{Project}/{Guid.NewGuid()}", File("a.txt"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesDocument()
    {
        var model = await Upload("doomed.txt");

        var delete = await client.DeleteAsync($"/{Project}/{model.Id}");
        delete.EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/{Project}/{model.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/{Project}/{model.Id}")).StatusCode);
    }

    [Fact]
    public async Task UnknownProject_Returns404OnEveryVerb()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/nope", File("a.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/nope/{Guid.NewGuid()}", File("a.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/nope/{Guid.NewGuid()}")).StatusCode);
    }

    private static MultipartFormDataContent File(string fileName, string content = "test content") =>
        new() { { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", fileName } };

    private async Task<DocumentModel> Upload(string fileName, string? folder = null, string content = "test content")
    {
        var url = folder == null ? $"/{Project}" : $"/{Project}/{folder}";
        var response = await client.PostAsync(url, File(fileName, content));
        response.EnsureSuccessStatusCode();
        var model = await response.Content.ReadFromJsonAsync<DocumentModel>();
        Assert.NotNull(model);
        return model;
    }
}
