using Azure;
using Azure.Messaging.EventGrid;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using BodyRecomp.Api.Triggers;
using Microsoft.Extensions.Logging;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BodyRecomp.Api.Tests.TriggerTests
{
    public class ImageResizeTriggerTests
    {
        private readonly Mock<BlobContainerClient> _mockRawContainer;
        private readonly Mock<BlobContainerClient> _mockThumbnailContainer;
        private readonly Mock<ILogger<ImageResizeTrigger>> _logger;
        private readonly ImageResizeTrigger _sut;

        public ImageResizeTriggerTests()
        {
            _mockRawContainer = new Mock<BlobContainerClient>();
            _mockThumbnailContainer = new Mock<BlobContainerClient>();
            _logger = new Mock<ILogger<ImageResizeTrigger>>();

            _mockRawContainer.SetupGet(c => c.Name).Returns("progress-photos");

            _sut = new ImageResizeTrigger(_mockRawContainer.Object, _mockThumbnailContainer.Object, _logger.Object);
        }

        private byte[] GenerateValidDummyImage()
        {
            using var image = new Image<Rgba32>(1, 1);
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms);
            return ms.ToArray();
        }

        [Fact]
        public async Task Run_WithWrongEventType_AbortsExecution()
        {
            var egEvent = new EventGridEvent(
                subject: "test-subject",
                eventType: "Microsoft.Storage.BlobDeleted",
                dataVersion: "1.0",
                data: BinaryData.FromObjectAsJson(new { url = "https://mock/progress-photos/test.jpg" }));

            await _sut.Run(egEvent);

            _mockRawContainer.Verify(v => v.GetBlobClient(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Run_WithMissingUrlProperty_AbortsExecution()
        {
            var egEvent = new EventGridEvent(
                subject: "test-subject",
                eventType: "Microsoft.Storage.BlobCreated",
                dataVersion: "1.0",
                data: BinaryData.FromObjectAsJson(new { irrelevantProperty = true }));

            await _sut.Run(egEvent);

            _mockRawContainer.Verify(v => v.GetBlobClient(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Run_WithValidEvent_DownloadsResizesAndUploads()
        {
            string blobName = "user-123/photo.jpg";
            string fullUrl = $"https://mock.blob.core.windows.net/progress-photos/{blobName}";

            var egEvent = new EventGridEvent(
                subject: "test-subject-123",
                eventType: "Microsoft.Storage.BlobCreated",
                dataVersion: "1.0",
                data: BinaryData.FromObjectAsJson(new { url = fullUrl }));

            var mockSourceBlob = new Mock<BlobClient>();
            var mockTargetBlob = new Mock<BlobClient>();

            byte[] validImageBytes = GenerateValidDummyImage();

            BlobDownloadResult mockDownloadResult = BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromBytes(validImageBytes));
            var mockResponse = Response.FromValue(mockDownloadResult, Mock.Of<Response>());

            mockSourceBlob.Setup(c => c.DownloadContentAsync()).ReturnsAsync(mockResponse);
            _mockRawContainer.Setup(c => c.GetBlobClient(blobName)).Returns(mockSourceBlob.Object);
            _mockThumbnailContainer.Setup(c => c.GetBlobClient(blobName)).Returns(mockTargetBlob.Object);

            await _sut.Run(egEvent);

            _mockRawContainer.Verify(v => v.GetBlobClient(blobName), Times.Once);
            _mockThumbnailContainer.Verify(v => v.GetBlobClient(blobName), Times.Once);

            mockSourceBlob.Verify(b => b.DownloadContentAsync(), Times.Once);

            mockTargetBlob.Verify(b => b.UploadAsync(
                It.IsAny<Stream>(),
                It.Is<bool>(overwrite => overwrite == true),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
