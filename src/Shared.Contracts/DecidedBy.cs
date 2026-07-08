namespace ApprovalFlow.Contracts;

/// <summary>Who (or what) produced a decision, for the audit trail and dashboard split
/// between autonomous and human approvals.</summary>
public static class DecidedBy
{
    public const string Ai = "AI";
    public const string Human = "Human";
    /// <summary>A hard-coded guardrail fired without consulting the AI (e.g. a global
    /// guardrail or GLOBAL-FRAUD), or a provider error forced escalation for safety.</summary>
    public const string System = "System";
}
