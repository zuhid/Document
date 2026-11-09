using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Zuhid.Document.Tests;

// Hosts the service in-memory against the local Azurite emulator (which must be running, see
// README.md). Each factory instance gets its own uniquely named project/container so test runs
// never see each other's blobs; the container is deleted when the fixture is disposed.
public class DocumentApiFactory : WebApplicationFactory<Program>
{
    public string Project { get; } = $"test-{Guid.NewGuid():N}";

    // UseSetting (not ConfigureAppConfiguration): Program reads these values during the
    // builder phase, before ConfigureAppConfiguration overrides would be applied.
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:ConnectionString", "UseDevelopmentStorage=true");
        builder.UseSetting("Projects", Project);
        builder.UseSetting("AllowedExtensions", ".pdf;.txt");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            new BlobServiceClient("UseDevelopmentStorage=true").GetBlobContainerClient(Project).DeleteIfExists();
        }

        base.Dispose(disposing);
    }
}
