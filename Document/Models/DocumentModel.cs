namespace Zuhid.Document.Models;

public record DocumentModel(Guid Id, string FileName, long Length, DateTime Modified, string? Folder = null);
