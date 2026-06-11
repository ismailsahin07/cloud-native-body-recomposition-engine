namespace BodyRecomp.Api.Models
{
    public class DailyMacroLog
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string UserId { get; set; }
        public string Type { get; set; } = nameof(DailyMacroLog);
        public DateTime LogTime { get; set; } 
        public double ProteinGrams { get; set; }
        public double FatGrams { get; set; }
        public double CarbGrams { get; set; }
        public double TotalCalories => (ProteinGrams * 4) + (CarbGrams * 4) + (FatGrams * 9);
    }
}
