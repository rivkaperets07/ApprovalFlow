namespace DecisionEngine.Core.Models;

public class DecisionResult
{
    public string TrackingId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime DecisionTimestamp { get; set; } = DateTime.UtcNow;
}