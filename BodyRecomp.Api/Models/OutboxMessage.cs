namespace BodyRecomp.Api.Models
{
    public class OutboxMessage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public string Type { get; set; } = nameof(OutboxMessage);
        public string EventType { get; set; }
        public string Payload { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int Ttl { get; set; } = 604800;
    }
}
