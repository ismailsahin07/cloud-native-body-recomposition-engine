using BodyRecomp.Api.Endpoints;
using BodyRecomp.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BodyRecomp.Api.Tests.Endpoints
{
    public class WorkoutEndpointTests
    {
        private readonly Mock<Container> _mockContainer;
        private readonly Mock<ILogger<WorkoutEndpoint>> _mockLogger;
        private readonly WorkoutEndpoint _sut;

        public WorkoutEndpointTests()
        {
            _mockContainer = new Mock<Container>();
            _mockLogger = new Mock<ILogger<WorkoutEndpoint>>();
            _sut = new WorkoutEndpoint(_mockContainer.Object, _mockLogger.Object);
        }

        private HttpRequest CreateMockRequest(string? userId, object? bodyPayload)
        {
            var context = new DefaultHttpContext();

            if (!string.IsNullOrEmpty(userId))
            {
                var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            }

            if (bodyPayload is not null)
            {
                var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                string json = JsonSerializer.Serialize(bodyPayload, options);
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                context.Request.Body = stream;
                context.Request.ContentLength = stream.Length;
            }

            return context.Request;
        }

        [Fact]
        public async Task GetActiveWorkoutSplit_WhenMissing_ReturnsNotFound()
        {
            string expectedUserId = "test-user-123";
            var request = CreateMockRequest(userId: expectedUserId, bodyPayload: null);
            string expectedDocumentId = $"split-{expectedUserId}";

            var notFoundException = new CosmosException("Not Found", HttpStatusCode.NotFound, 0, "mockActivityId", 0);

            _mockContainer.Setup(x => x.ReadItemAsync<WorkoutSplit>(
                expectedDocumentId,
                new PartitionKey(expectedUserId),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()))
                .ThrowsAsync(notFoundException);

            var result = await _sut.GetActiveWorkoutSplit(request);

            result.Should().BeOfType<NotFoundObjectResult>();
            var notFoundResult = (NotFoundObjectResult)result;
            notFoundResult.Value.Should().Be("No active training split template discovered. Please create one.");
        }

        [Fact]
        public async Task LogWorkoutSession_OnStandardDay_BatchesSingleItem()
        {
            string expectedUserId = "test-user-123";
            var payload = new WorkoutSessionLog { DayNumber = 3 };
            var request = CreateMockRequest(expectedUserId, payload);

            var mockBatch = new Mock<TransactionalBatch>();
            var mockResponse = new Mock<TransactionalBatchResponse>();

            mockResponse.Setup(r => r.IsSuccessStatusCode).Returns(true);
            mockResponse.Setup(r => r.StatusCode).Returns(HttpStatusCode.OK);

            mockBatch.Setup(x => x.CreateItem(
                It.IsAny<WorkoutSessionLog>(),
                It.IsAny<TransactionalBatchItemRequestOptions>()))
                .Returns(mockBatch.Object);

            mockBatch.Setup(x => x.ExecuteAsync(
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockResponse.Object);

            _mockContainer.Setup(x => x.CreateTransactionalBatch(
                new PartitionKey(expectedUserId)))
                .Returns(mockBatch.Object);

            var result = await _sut.LogWorkoutSession(request);

            result.Should().BeOfType<OkObjectResult>();

            mockBatch.Verify(v => v.CreateItem(
                It.IsAny<WorkoutSessionLog>(),
                It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);

            mockBatch.Verify(v => v.CreateItem(
                It.IsAny<OutboxMessage>(),
                It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Never);

            mockBatch.Verify(v => v.ExecuteAsync(
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task LogWorkoutSession_OnDayFive_BatchesSessionAndOutboxMessage()
        {
            string expectedUserId = "test-user-123";
            var payload = new WorkoutSessionLog { DayNumber = 5 };
            var request = CreateMockRequest(userId: expectedUserId, bodyPayload: payload);

            var mockBatch = new Mock<TransactionalBatch>();
            var mockResponse = new Mock<TransactionalBatchResponse>();

            mockResponse.Setup(r => r.IsSuccessStatusCode).Returns(true);

            mockBatch.Setup(x => x.CreateItem(
                It.IsAny<WorkoutSessionLog>(),
                It.IsAny<TransactionalBatchItemRequestOptions>()))
                .Returns(mockBatch.Object);

            mockBatch.Setup(x => x.CreateItem(
                It.IsAny<OutboxMessage>(),
                It.IsAny<TransactionalBatchItemRequestOptions>()))
                .Returns(mockBatch.Object);

            mockBatch.Setup(x => x.ExecuteAsync(
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockResponse.Object);

            _mockContainer.Setup(c => c.CreateTransactionalBatch(
                new PartitionKey(expectedUserId)))
                .Returns(mockBatch.Object);

            var result = await _sut.LogWorkoutSession(request);

            result.Should().BeOfType<OkObjectResult>();

            mockBatch.Verify(v => v.CreateItem(
                It.IsAny<WorkoutSessionLog>(),
                It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);

            mockBatch.Verify(v => v.CreateItem(
                It.IsAny<OutboxMessage>(),
                It.IsAny<TransactionalBatchItemRequestOptions>()), Times.Once);

            mockBatch.Verify(v => v.ExecuteAsync(
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
