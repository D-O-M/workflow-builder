namespace Dcc.Common;

public interface IDccXmlBlobClient
{
    string ContainerName { get; }

    Task UploadTextAsync(string blobName, string content, string contentType = "application/xml");

    Task<string> DownloadTextAsync(string blobName);
}
