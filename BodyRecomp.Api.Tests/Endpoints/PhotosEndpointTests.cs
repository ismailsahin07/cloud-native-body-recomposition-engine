using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using BodyRecomp.Api.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Claims;
using System.Text.Json;

namespace BodyRecomp.Api.Tests.Endpoints
{
    public class PhotosEndpointTests
    {
        private readonly Mock<BlobContainerClient> _mockContainerClient;
        private readonly Mock<ILogger<PhotosEndpoint>> _mockLogger;
        private readonly PhotosEndpoint _sut;
        
        public PhotosEndpointTests()
        {
            _mockContainerClient = new Mock<BlobContainerClient>();
            _mockLogger = new Mock<ILogger<PhotosEndpoint>>();
            _sut = new PhotosEndpoint(_mockContainerClient.Object, _mockLogger.Object);
        }

        private HttpRequest CreateMockRequest(string? userId)
        {
            var context = new DefaultHttpContext();

            if (!string.IsNullOrEmpty(userId))
            {
                var claim = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claim, "TestAuth"));
            }

            return context.Request;
        }

        [Fact]
        public async Task GetUploadToken_WithoutUserIdClaim_ReturnsUnauthorized()
        {
            var request = CreateMockRequest(userId: null);

            var result = await _sut.GetUploadToken(request);

            result.Should().BeOfType<UnauthorizedResult>();
        }

        [Fact]
        public async Task GetUploadToken_WithValidUser_GeneratesSasAndReturnsOk()
        {
            string expectedUserId = "test-user-123";
            var request = CreateMockRequest(expectedUserId);

            var mockBlobClient = new Mock<BlobClient>();
            var expectedUri = new Uri("https://mockstorage.blob.core.windows.net/progress-photos/mock-token");

            _mockContainerClient.SetupGet(c => c.Name).Returns("progress-photos");

            _mockContainerClient.Setup(c => c.GetBlobClient(
                It.IsAny<string>()))
                .Returns(mockBlobClient.Object);

            mockBlobClient.Setup(c => c.GenerateSasUri(
                It.IsAny<BlobSasBuilder>()))
                .Returns(expectedUri);

            var result = await _sut.GetUploadToken(request);

            result.Should().BeOfType<OkObjectResult>();
            var okResult = (OkObjectResult)result;

            string jsonResponse = JsonSerializer.Serialize(okResult.Value);
            using JsonDocument doc = JsonDocument.Parse(jsonResponse);

            string targetBlobName = doc.RootElement.GetProperty("TargetBlobName").GetString()!;
            string uploadUri = doc.RootElement.GetProperty("UploadUri").GetString()!;

            targetBlobName.Should().StartWith(expectedUserId);
            targetBlobName.Should().EndWith("-checkin.jpg");
            uploadUri.Should().Be(expectedUri.ToString());

            _mockContainerClient.Verify(v => v.GetBlobClient(
                It.IsAny<string>()), Times.Once);

            mockBlobClient.Verify(v => v.GenerateSasUri(
                It.IsAny<BlobSasBuilder>()), Times.Once);
        }

        [Fact]
        public async Task GetUploadToken_OnCryptographicFailure_ReturnsInternalServerError()
        {
            string expectedUserId = "test-user-123";
            var request = CreateMockRequest(expectedUserId);

            _mockContainerClient.SetupGet(c => c.Name).Returns("progress-photos");

            _mockContainerClient.Setup(x => x.GetBlobClient(
                It.IsAny<string>()))
                .Throws(new InvalidOperationException("Simulated cryptographic failure."));

            var result = await _sut.GetUploadToken(request);

            result.Should().BeOfType<StatusCodeResult>();
            var statusCodeResult = result as StatusCodeResult;
            statusCodeResult.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        }
    }
}
