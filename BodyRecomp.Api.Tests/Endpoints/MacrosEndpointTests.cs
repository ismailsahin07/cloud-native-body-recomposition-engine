using BodyRecomp.Api.Endpoints;
using BodyRecomp.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BodyRecomp.Api.Tests.Endpoints
{
    public class MacrosEndpointTests
    {
        private readonly Mock<Container> _mockContainer;
        private readonly Mock<ILogger<MacrosEndpoint>> _mockLogger;
        private readonly MacrosEndpoint _sut;

        public MacrosEndpointTests()
        {
            _mockContainer = new Mock<Container>();
            _mockLogger = new Mock<ILogger<MacrosEndpoint>>();
            _sut = new MacrosEndpoint(_mockContainer.Object, _mockLogger.Object);
        }

        private HttpRequest CreateMockRequest(string? userId, object? bodyPayload)
        {
            var context = new DefaultHttpContext();

            if (!string.IsNullOrEmpty(userId))
            {
                var claims = new List<Claim> { new Claim(ClaimTypes.NameIdentifier, userId) };
                var identity = new ClaimsIdentity(claims, "TestAuth");
                context.User = new ClaimsPrincipal(identity);
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

        private Mock<FeedIterator<T>> SetupMockFeedIterator<T>(List<T> dataToReturn)
        {
            var mockFeedResponse = new Mock<FeedResponse<T>>();

            mockFeedResponse.As<IEnumerable<T>>()
                .Setup(x => x.GetEnumerator())
                .Returns(() => dataToReturn.GetEnumerator());

            var mockIterator = new Mock<FeedIterator<T>>();

            mockIterator.SetupSequence(x => x.HasMoreResults)
                .Returns(true)
                .Returns(false);

            mockIterator.Setup(x => x.ReadNextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(mockFeedResponse.Object);

            return mockIterator;
        }

        [Fact]
        public async Task LogMacros_WithoutUserIdClaim_ReturnsUnauthorized()
        {
            var request = CreateMockRequest(userId: null, bodyPayload: new DailyMacroLog());

            var result = await _sut.LogMacros(request);

            result.Should().BeOfType<UnauthorizedResult>();
        }

        [Fact]
        public async Task LogMacros_WithMalformedJson_ReturnsBadRequest()
        {
            var request = CreateMockRequest(userId: "test-user-123", bodyPayload: null);

            var badJsonStream = new MemoryStream(Encoding.UTF8.GetBytes("{ bad_json: true }"));
            request.Body = badJsonStream;
           
            var result = await _sut.LogMacros(request);

            result.Should().BeOfType<BadRequestObjectResult>();
            var badRequest = (BadRequestObjectResult)result;
            badRequest.Value.Should().Be("Malformed JSON payload.");
        }

        [Fact]
        public async Task LogMacros_WithValidPayload_SavesToCosmosAndReturnsOk()
        {
            string expectedUserId = "test-user-123";
            var payload = new DailyMacroLog
            {
                ProteinGrams = 150,
                FatGrams = 50,
                CarbGrams = 200
            };

            var request = CreateMockRequest(userId: expectedUserId, bodyPayload: payload);

            _mockContainer.Setup(c => c.CreateItemAsync(
                It.IsAny<DailyMacroLog>(),
                It.Is<PartitionKey>(pk => pk == new PartitionKey(expectedUserId)),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync((DailyMacroLog item, PartitionKey pk, ItemRequestOptions opt, CancellationToken ct) =>
                {
                    var mockItemResponse = new Mock<ItemResponse<DailyMacroLog>>();
                    mockItemResponse.Setup(x => x.Resource).Returns(item); 
                    mockItemResponse.Setup(x => x.RequestCharge).Returns(1.5);
                    return mockItemResponse.Object;
                });

            var result = await _sut.LogMacros(request);

            result.Should().BeOfType<OkObjectResult>();
            var okResult = (OkObjectResult)result;
            var returnedData = okResult.Value as DailyMacroLog;

            returnedData.Should().NotBeNull();
            returnedData!.UserId.Should().Be(expectedUserId);
            returnedData.TotalCalories.Should().Be(1850);

            _mockContainer.Verify(c => c.CreateItemAsync(
                It.IsAny<DailyMacroLog>(),
                It.IsAny<PartitionKey>(),
                It.IsAny<ItemRequestOptions>(),
                It.IsAny<CancellationToken>()
                ), Times.Once);
        }

        [Fact]
        public async Task GetMacroHistory_WithoutUserIdClaim_ReturnsUnauthorized()
        {
            var request = CreateMockRequest(userId: null, bodyPayload: null);

            var result = await _sut.GetMacroHistory(request);

            result.Should().BeOfType<UnauthorizedResult>();
        }

        [Fact]
        public async Task GetMacroHistory_WithNoHistory_ReturnsEmptyList()
        {
            string expectedUser = "test-user-123";
            var request = CreateMockRequest(userId: expectedUser, bodyPayload: null);
            var emptyDataList = new List<DailyMacroLog>();

            var mockIterator = SetupMockFeedIterator(emptyDataList);

            _mockContainer.Setup(c => c.GetItemQueryIterator<DailyMacroLog>(
                It.IsAny<QueryDefinition>(),
                It.IsAny<string>(),
                It.IsAny<QueryRequestOptions>()))
                .Returns(mockIterator.Object);

            var result = await _sut.GetMacroHistory(request);

            result.Should().BeOfType<OkObjectResult>();
            var okResult = (OkObjectResult)result;

            var returnedList = okResult.Value as List<DailyMacroLog>;
            returnedList.Should().NotBeNull();
            returnedList.Should().BeEmpty();
        }

        [Fact]
        public async Task GetMacroHistory_WithExistingHistory_ReturnsPopulatedList()
        {
            string expectedUser = "test-user-123";
            var request = CreateMockRequest(userId: expectedUser, bodyPayload: null);

            var historycalData = new List<DailyMacroLog>()
            {
                new DailyMacroLog { ProteinGrams = 150, FatGrams = 50, CarbGrams = 200 },
                new DailyMacroLog { ProteinGrams = 160, FatGrams = 55, CarbGrams = 190 }
            };

            var mockIterator = SetupMockFeedIterator(historycalData);

            _mockContainer.Setup(c => c.GetItemQueryIterator<DailyMacroLog>(
                It.IsAny<QueryDefinition>(),
                It.IsAny<string>(),
                It.IsAny<QueryRequestOptions>()))
                .Returns(mockIterator.Object);

            var result = await _sut.GetMacroHistory(request);

            result.Should().BeOfType<OkObjectResult>();
            var okResult = (OkObjectResult)result;

            var returnedList = okResult.Value as List<DailyMacroLog>;
            returnedList.Should().NotBeNull();
            returnedList.Should().HaveCount(2);

            returnedList[0]!.ProteinGrams.Should().Be(150);
            returnedList[1].ProteinGrams.Should().Be(160);

            _mockContainer.Verify(c => c.GetItemQueryIterator<DailyMacroLog>(
                It.IsAny<QueryDefinition>(),
                It.IsAny<string>(),
                It.IsAny<QueryRequestOptions>()), Times.Once);
        }
    }
}
