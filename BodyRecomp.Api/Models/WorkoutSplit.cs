namespace BodyRecomp.Api.Models
{
    public class WorkoutSplit
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public string Type { get; set; } = nameof(WorkoutSplit);
        public string SplitName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsActive { get; set; }
        public List<TrainingDay> Days { get; set; } = new();
    }

    public class TrainingDay
    {
        public int DayNumber { get; set; }
        public string FocusName { get; set; }
        public List<ExerciseEntry> Exercises { get; set; } = new();

    }

    public class ExerciseEntry
    {
        public string Name { get; set; }
        public int TargetSets { get; set; }
        public string TargetRepsRange { get; set; }
        public int RestTimeSeconds { get; set; }
    }
}
