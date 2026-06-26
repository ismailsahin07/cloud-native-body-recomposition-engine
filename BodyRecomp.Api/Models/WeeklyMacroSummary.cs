namespace BodyRecomp.Api.Models
{
    public class WeeklyMacroSummary
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public string Type { get; set; } = nameof(WeeklyMacroSummary);
        public DateTime WeekEnding { get; set; }
        public double AverageProtein { get; set; }
        public double AverageFat { get; set; }
        public double AverageCarbs { get; set; }
        public double AverageCalories { get; set; }
        public int DaysLogged { get; set; }
    }
}
