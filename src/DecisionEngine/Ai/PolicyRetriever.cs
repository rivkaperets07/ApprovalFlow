using System.Text.RegularExpressions;

namespace DecisionEngine.Ai;

/// <summary>One row of docs/policy.md's rule tables: a stable rule_id (the doc's own words:
/// "emit these in the agent's policy_violations[].rule_id"), the section it lives under,
/// and the rule's text.</summary>
public record PolicyClause(string RuleId, string Section, string Text);

/// <summary>
/// RAG over docs/policy.md: parses the policy document into per-rule_id clauses once
/// at startup, then retrieves only the ones relevant to a given invoice via TF-IDF cosine
/// similarity — instead of stuffing the whole document into every AI prompt, which is
/// literally what the doc's own preamble asks for ("the Nice-to-Have RAG work is to
/// retrieve only the relevant rule(s) instead of putting the whole policy in the prompt").
///
/// Classical (pre-neural) retrieval, not an embedding model: the corpus is a few dozen
/// short rules, well within what TF-IDF handles well, and it keeps this fully local and
/// deterministic — no network call, no model to swap alongside AiProvider, and the Stub
/// provider (CI, tests) gets the exact same retrieval behavior as Groq, unlike an
/// embeddings API that would only be reachable when a key is configured.
///
/// Scoped to policy.md's per-category rule sections (§1-5) only — never §6's autonomy
/// thresholds or §7's budgets. Those are enforced by PolicyEngine in code and never shown
/// to the AI at all: retrieval augments the AI's qualitative coherence judgment with
/// rule *text* it can reason about and cite, never the numeric ceilings that actually
/// decide approve/escalate.
/// </summary>
public class PolicyRetriever
{
    private static readonly Regex SectionHeaderPattern = new(@"^##\s+\d+\.\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex RuleRowPattern = new(@"^\|\s*`([A-Z0-9\-]+)`\s*\|\s*(.+?)\s*\|\s*$", RegexOptions.Compiled);
    private static readonly Regex TokenPattern = new(@"[a-z0-9]+", RegexOptions.Compiled);

    // The only sections whose rules are candidates for retrieval — §6 (autonomy
    // thresholds) and §7 (budgets) are deliberately excluded (see class summary).
    private static readonly HashSet<string> IndexableSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Meals & Entertainment", "Travel", "Software / SaaS", "Hardware", "Global rules"
    };

    private readonly List<PolicyClause> _clauses;
    private readonly List<Dictionary<string, double>> _clauseVectors;
    private readonly Dictionary<string, double> _idf;

    public PolicyRetriever(IEnumerable<PolicyClause> clauses)
    {
        _clauses = clauses.ToList();
        // Section folded into what gets indexed (not just Text): BuildQuery leads with the
        // invoice's category (e.g. "SaaS"), and that word lives in the section header
        // ("Software / SaaS"), not necessarily in any individual rule's own text — without
        // this, category alignment contributes nothing to the score, only incidental
        // wording overlap with the rule text itself.
        var indexedText = _clauses.Select(IndexedText).ToList();
        _idf = ComputeIdf(indexedText);
        _clauseVectors = indexedText.Select(Vectorize).ToList();
    }

    private static string IndexedText(PolicyClause clause) => $"{clause.Section} {clause.Text}";

    public static PolicyRetriever LoadFromFile(string path) => new(ParsePolicyMarkdown(File.ReadAllText(path)));

    /// <summary>The text a caller should retrieve against for one invoice: the category
    /// (so a clause about that category's own section scores highest) plus whatever
    /// free-text signal the submitter actually provided.</summary>
    public static string BuildQuery(InvoicePayload invoice, string category)
    {
        var lineItemText = invoice.LineItems is { Count: > 0 }
            ? string.Join(" ", invoice.LineItems.Select(item => item.Description))
            : string.Empty;
        return string.Join(" ", new[] { category, invoice.Notes, lineItemText }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    /// <summary>Top-<paramref name="topK"/> clauses by cosine similarity to <paramref name="query"/>,
    /// excluding anything that shares no vocabulary with it at all (score 0) — an empty or
    /// entirely off-topic query legitimately retrieves nothing rather than K arbitrary rows.</summary>
    public IReadOnlyList<PolicyClause> Retrieve(string query, int topK = 3)
    {
        var queryVector = Vectorize(query);
        if (queryVector.Count == 0)
            return [];

        return _clauses
            .Select((clause, i) => (clause, score: CosineSimilarity(queryVector, _clauseVectors[i])))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.clause)
            .ToList();
    }

    public static List<PolicyClause> ParsePolicyMarkdown(string markdown)
    {
        var clauses = new List<PolicyClause>();
        string? currentSection = null;

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            var headerMatch = SectionHeaderPattern.Match(line);
            if (headerMatch.Success)
            {
                currentSection = headerMatch.Groups[1].Value;
                continue;
            }

            if (currentSection is null || !IndexableSections.Contains(currentSection))
                continue;

            var ruleMatch = RuleRowPattern.Match(line);
            if (ruleMatch.Success)
            {
                clauses.Add(new PolicyClause(ruleMatch.Groups[1].Value, currentSection, ruleMatch.Groups[2].Value));
            }
        }

        return clauses;
    }

    private static Dictionary<string, double> ComputeIdf(List<string> indexedTexts)
    {
        var documentFrequency = new Dictionary<string, int>();
        foreach (var text in indexedTexts)
        {
            foreach (var term in Tokenize(text).Distinct())
            {
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
            }
        }

        // +1 smoothing so a term appearing in every clause still gets a small positive
        // weight instead of ln(1) = 0, which would erase it from every vector.
        return documentFrequency.ToDictionary(kv => kv.Key, kv => Math.Log((double)indexedTexts.Count / kv.Value) + 1);
    }

    private Dictionary<string, double> Vectorize(string text)
    {
        var termFrequency = new Dictionary<string, int>();
        foreach (var term in Tokenize(text))
        {
            termFrequency[term] = termFrequency.GetValueOrDefault(term) + 1;
        }

        var vector = new Dictionary<string, double>();
        foreach (var (term, frequency) in termFrequency)
        {
            // A term the indexed corpus never saw contributes nothing — there's no IDF
            // weight to score it against, and treating it as maximally rare would let one
            // shared rare word dominate the whole similarity score.
            if (_idf.TryGetValue(term, out var idf))
            {
                vector[term] = frequency * idf;
            }
        }
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text) =>
        TokenPattern.Matches(text.ToLowerInvariant()).Select(m => Stem(m.Value));

    // Deliberately crude (suffix-stripping, not a real Porter stemmer): just enough to
    // stop "subscription" (a submitter's Notes) and "Subscriptions" (the rule's own text)
    // from scoring as unrelated words purely because of pluralization. A full stemmer
    // would be overkill for a few dozen short rules, and precision matters less than
    // recall here — PolicyEngine's code, not this ranking, is what enforces safety.
    private static string Stem(string token)
    {
        if (token.Length <= 3)
            return token;
        if (token.EndsWith("ies", StringComparison.Ordinal))
            return string.Concat(token.AsSpan(0, token.Length - 3), "y");
        if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 4)
            return token[..^2];
        if (token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
            return token[..^1];
        return token;
    }

    private static double CosineSimilarity(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        double dot = 0, normA = 0, normB = 0;
        foreach (var (term, weight) in a)
        {
            normA += weight * weight;
            if (b.TryGetValue(term, out var otherWeight))
                dot += weight * otherWeight;
        }
        foreach (var weight in b.Values)
        {
            normB += weight * weight;
        }

        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
