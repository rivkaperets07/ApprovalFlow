namespace DecisionEngine.Core.Models;

public class RouterDecision
{
    public bool IsApproved { get; set; }
    public string Reason { get; set; } = string.Empty;

    // Factory methods 
    public static RouterDecision Approved(string reason) => 
        new() { IsApproved = true, Reason = reason };

    public static RouterDecision Escalated(string reason) => 
        new() { IsApproved = false, Reason = reason };
}