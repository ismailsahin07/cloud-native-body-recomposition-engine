using Azure.Messaging.EventGrid;
using Azure.Storage.Blobs;
using BodyRecomp.Api.Configuration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Text.Json;

namespace BodyRecomp.Api.Triggers;

public class ImageResizeTrigger
{
    private readonly BlobContainerClient _rawPhotosContainer;
    private readonly BlobContainerClient _thumbnailsContainer;
    private readonly ILogger<ImageResizeTrigger> _logger;

    public ImageResizeTrigger(
        [FromKeyedServices(StorageContainerKey.ProgressPhotos)] BlobContainerClient rawPhotosContainer,
        [FromKeyedServices(StorageContainerKey.Thumbnails)] BlobContainerClient thumbnailsContainer,
        ILogger<ImageResizeTrigger> logger)
    {
        _rawPhotosContainer = rawPhotosContainer;
        _thumbnailsContainer = thumbnailsContainer;
        _logger = logger;
    }

    [Function(nameof(ImageResizeTrigger))]
    public async Task Run([EventGridTrigger] EventGridEvent egEvent)
    {
        _logger.LogInformation($"Event Grid trigger intercepted event type: {egEvent.EventType}");

        if(egEvent.EventType != "Microsoft.Storage.BlobCreated")
        {
            _logger.LogError("Unsupported event type received. Skipping execution.");
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(egEvent.Data.ToString());
        if(!doc.RootElement.TryGetProperty("url", out JsonElement urlElement))
        {
            _logger.LogError("Event payload missing 'url' property metadata.");
            return;
        }

        string blobUrl = urlElement.ToString();
        _logger.LogInformation($"New blob detected at target URL: {blobUrl}");

        string blobName = GetBlobNameFromUrl(blobUrl, _rawPhotosContainer.Name);
        if (string.IsNullOrEmpty(blobName))
        {
            _logger.LogError($"Failed to parse relative blob name from URL path structure.");
            return;
        }

        try
        {
            BlobClient sourceBlobClient = _rawPhotosContainer.GetBlobClient(blobName);

            _logger.LogInformation("Downloading blob binaries into worker memory space...");
            var downloadResponse = await sourceBlobClient.DownloadContentAsync();
            byte[] rawImageBytes = downloadResponse.Value.Content.ToArray();

            byte[] thumbnailBytes;
            using (Image image = Image.Load(rawImageBytes))
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(300, 300),
                    Mode = ResizeMode.Max
                }));

                using MemoryStream memoryStream = new();
                await image.SaveAsJpegAsync(memoryStream);
                thumbnailBytes = memoryStream.ToArray();
            }

            BlobClient targetBlobClient = _thumbnailsContainer.GetBlobClient(blobName);

            using (MemoryStream uploadStream = new(thumbnailBytes))
            {
                await targetBlobClient.UploadAsync(uploadStream, overwrite: true);
            }

            _logger.LogInformation($"Successfully generated and saved thumbnail asset for: {blobName}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Critical failure inside image processing execution pipeline: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Helper method to extract the relative path of blob out of its fully qualified URL
    /// </summary>
    private string GetBlobNameFromUrl(string url, string containerName)
    {
        string lookFor = $"/{containerName}/";
        int index = url.IndexOf(lookFor, StringComparison.OrdinalIgnoreCase);
        if (index == -1) return null;

        return url.Substring(index + lookFor.Length);
    }
}