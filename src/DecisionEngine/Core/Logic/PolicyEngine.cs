namespace DecisionEngine.Core.Logic;

public class PolicyEngine
{
    private readonly IConfiguration _config;

    public PolicyEngine(IConfiguration config)
    {
        _config = config;
    }

    public RouterDecision Evaluate(InvoicePayload invoice, AiAnalysisResult aiResult)
    {
        // Dynamic lookup based on AI category
        var policy = _config.GetSection($"ExpensePolicies:{aiResult.SuggestedCategory}").Get<PolicyConfig>();

        if (policy == null)
            return new RouterDecision { IsApproved = false, Reason = "Category not supported" };

        // Metadata validation
        foreach (var key in policy.RequiredMetadata)
        {
            if (!aiResult.Metadata.ContainsKey(key))
                return new RouterDecision { IsApproved = false, Reason = $"Missing: {key}" };
        }

        // Amount validation
        if (invoice.TotalAmount > policy.MaxAmount)
            return new RouterDecision { IsApproved = false, Reason = "Exceeds limit" };

        return new RouterDecision { IsApproved = true };
    }
}

public class PolicyConfig 
{
    public List<string> RequiredMetadata { get; set; } = new();
    public decimal MaxAmount { get; set; }
}