using Azure.Storage.Blobs;

namespace Zuhid.Document;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers();
        builder.Services.AddSingleton(new BlobServiceClient(
            builder.Configuration["Storage:ConnectionString"]));
        builder.Services.AddSingleton(new DocumentSetting(
            (builder.Configuration["AllowedExtensions"] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.StartsWith('.') ? n : $".{n}")
            .ToArray(),
            (builder.Configuration["Projects"] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.ToLowerInvariant())
            .ToArray()));
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins((builder.Configuration["CorsOrigin"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            .AllowAnyHeader()
            .AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();
        app.MapControllers();
        app.Run();
    }
}
