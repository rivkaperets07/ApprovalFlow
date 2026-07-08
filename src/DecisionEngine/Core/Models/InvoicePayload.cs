namespace DecisionEngine.Core.Models;

public class InvoicePayload
{
    // The category suggested by the AI (e.g., "Meals", "Travel")
    public string SuggestedCategory { get; set; } = "General";
    
    // Total amount of the invoice
    public decimal TotalAmount { get; set; }
    
    // Confidence score from the AI model
    public double ConfidenceScore { get; set; }
    
    // Explanation from the AI for the decision
    public string Reasoning { get; set; } = string.Empty;

    // Flexible storage for category-specific data (e.g., "MealAttendeesCount", "TripId")
    // This allows the system to remain agnostic to specific business rules
    public Dictionary<string, object> Metadata { get; set; } = new();
}