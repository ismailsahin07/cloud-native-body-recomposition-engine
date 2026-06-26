namespace BodyRecomp.Api.Models
{
    public class WorkoutSessionLog
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public string Type { get; set; } = nameof(WorkoutSessionLog);
        public int DayNumber { get; set; } 
        public DateTime CompletedAt { get; set; }
        public string Notes { get; set; }
    }

    public class AnalyticsQueueMessage
    {
        public string UserId { get; set; }
        public string TargetDate { get; set; }
    }
}
