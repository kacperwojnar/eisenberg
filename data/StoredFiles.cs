using Azure.Storage.Blobs;

namespace Eisenberg.Data;

public static class StoredFiles
{
    public static async Task<string> SaveAsync(string? connectionString, string root, Guid id, Stream content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, id.ToString());
            await using var file = File.Create(path);
            await content.CopyToAsync(file, cancellationToken);
            return path;
        }

        var container = new BlobContainerClient(connectionString, "files");
        await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        await container.GetBlobClient(id.ToString()).UploadAsync(content, overwrite: true, cancellationToken: cancellationToken);
        return "blob:" + id;
    }

    public static async Task<Stream> OpenReadAsync(string? connectionString, string location, CancellationToken cancellationToken = default)
    {
        if (!location.StartsWith("blob:", StringComparison.Ordinal))
            return File.OpenRead(location);

        var container = new BlobContainerClient(connectionString, "files");
        var memory = new MemoryStream();
        await container.GetBlobClient(location["blob:".Length..]).DownloadToAsync(memory, cancellationToken);
        memory.Position = 0;
        return memory;
    }

    public static async Task DeleteAsync(string? connectionString, string location, CancellationToken cancellationToken = default)
    {
        if (!location.StartsWith("blob:", StringComparison.Ordinal))
        {
            if (File.Exists(location)) File.Delete(location);
            return;
        }

        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var container = new BlobContainerClient(connectionString, "files");
        await container.GetBlobClient(location["blob:".Length..]).DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }
}
