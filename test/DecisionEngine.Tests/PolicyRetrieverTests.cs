using DecisionEngine.Ai;

public class PolicyRetrieverTests
{
    private const string SampleMarkdown = """
        # Sample Policy

        ## 1. Meals & Entertainment

        | rule_id | Rule |
        |---|---|
        | `MEAL-01` | Personal meals are reimbursable up to $75 per submission. |
        | `MEAL-02` | Client entertainment over $500 requires a business justification and a client name. |
        | `MEAL-03` | Alcohol-only receipts are not reimbursable. |

        ## 2. Travel

        | rule_id | Rule |
        |---|---|
        | `TRAVEL-01` | Economy flights and standard hotels are policy-eligible. |
        | `TRAVEL-03` | First/business-class travel always requires approval. |

        ## 3. Software / SaaS

        | rule_id | Rule |
        |---|---|
        | `SAAS-01` | Subscriptions are policy-eligible up to $200 per month. |

        ## 5. Global rules

        | rule_id | Rule |
        |---|---|
        | `GLOBAL-RECEIPT` | A receipt is required for any expense over $25. |

        ## 6. Autonomy thresholds (the dilemma — default posture; tune & justify)

        | key | default | meaning |
        |---|---|---|
        | `AUTONOMY-CEILING` | $250 | The agent may auto-approve only when the USD amount is <= $250. |
        """;

    [Fact]
    public void ParsePolicyMarkdown_ExtractsRuleIdAndTextFromIndexableSections()
    {
        var clauses = PolicyRetriever.ParsePolicyMarkdown(SampleMarkdown);

        var meal01 = Assert.Single(clauses, c => c.RuleId == "MEAL-01");
        Assert.Equal("Meals & Entertainment", meal01.Section);
        Assert.Contains("$75 per submission", meal01.Text);
    }

    [Fact]
    public void ParsePolicyMarkdown_SkipsTheHeaderAndSeparatorRows()
    {
        var clauses = PolicyRetriever.ParsePolicyMarkdown(SampleMarkdown);

        Assert.DoesNotContain(clauses, c => c.RuleId == "rule_id");
        Assert.DoesNotContain(clauses, c => c.RuleId.Contains("---"));
    }

    [Fact]
    public void ParsePolicyMarkdown_ExcludesAutonomyThresholdsSection()
    {
        // M12: the numeric ceilings PolicyEngine enforces in code must never reach the AI
        // via retrieval — §6 is deliberately out of the indexable set (see PolicyRetriever's
        // class summary), so this must never come back no matter what's queried for.
        var clauses = PolicyRetriever.ParsePolicyMarkdown(SampleMarkdown);

        Assert.DoesNotContain(clauses, c => c.RuleId == "AUTONOMY-CEILING");
    }

    [Fact]
    public void Retrieve_ReturnsTheClauseMatchingTheQueryTopic()
    {
        var retriever = new PolicyRetriever(PolicyRetriever.ParsePolicyMarkdown(SampleMarkdown));

        var results = retriever.Retrieve("Meals alcohol beer wine only receipt");

        Assert.Contains(results, c => c.RuleId == "MEAL-03");
    }

    [Fact]
    public void Retrieve_RanksTheMostSimilarClauseFirst()
    {
        var retriever = new PolicyRetriever(PolicyRetriever.ParsePolicyMarkdown(SampleMarkdown));

        var results = retriever.Retrieve("client entertainment business justification client name");

        Assert.Equal("MEAL-02", results[0].RuleId);
    }

    [Fact]
    public void Retrieve_RespectsTopK()
    {
        var retriever = new PolicyRetriever(PolicyRetriever.ParsePolicyMarkdown(SampleMarkdown));

        var results = retriever.Retrieve("policy expense reimbursement", topK: 2);

        Assert.True(results.Count <= 2);
    }

    [Fact]
    public void Retrieve_QueryWithNoVocabularyOverlap_ReturnsEmpty()
    {
        var retriever = new PolicyRetriever(PolicyRetriever.ParsePolicyMarkdown(SampleMarkdown));

        // None of these words appear anywhere in SampleMarkdown's clause text.
        var results = retriever.Retrieve("xyzzy quux zzyzx wobble");

        Assert.Empty(results);
    }

    [Fact]
    public void Retrieve_EmptyQuery_ReturnsEmpty()
    {
        var retriever = new PolicyRetriever(PolicyRetriever.ParsePolicyMarkdown(SampleMarkdown));

        var results = retriever.Retrieve("");

        Assert.Empty(results);
    }

    [Fact]
    public void BuildQuery_CombinesCategoryNotesAndLineItemDescriptions()
    {
        var invoice = new InvoicePayload
        {
            Vendor = "The Corner Bistro",
            Notes = "team dinner",
            LineItems = [new LineItem("Cocktails", 40m), new LineItem("Wine", 30m)]
        };

        var query = PolicyRetriever.BuildQuery(invoice, "Meals");

        Assert.Contains("Meals", query);
        Assert.Contains("team dinner", query);
        Assert.Contains("Cocktails", query);
        Assert.Contains("Wine", query);
    }
}
