namespace DecisionEngine.Core.Models;

public class AiAnalysisResult
{
    public string SuggestedCategory { get; set; } = "Unknown";
    public string? LinkedTripId { get; set; } 
    public double ConfidenceScore { get; set; } 
    public int MealAttendeesCount { get; set; }
    public string Reasoning { get; set; } = string.Empty; 
}