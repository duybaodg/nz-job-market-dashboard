namespace Application.Deduplication;

/// <summary>A minimal, pre-computed view of one active listing used by the dedup engine.</summary>
public sealed record DedupListing(
    Guid JobId,
    string Source,
    string NormalizedTitle,
    Guid EmployerCanonicalId,
    bool HasSalary,
    string SalaryKey,
    string LocationKey,
    DateTime WindowStart,
    DateTime WindowEnd,
    DateTime RepresentativeDate,
    IReadOnlySet<string> DescriptionShingles);

public enum DedupPairRelation
{
    NoRelation,
    SalaryWindowMatch,
    DescriptionMatch,
    SalaryConflict,
    SameSource
}

public sealed record DedupPairEvaluation(DedupPairRelation Relation)
{
    public bool IsMatch => Relation is DedupPairRelation.SalaryWindowMatch or DedupPairRelation.DescriptionMatch;

    public string? MatchMethod => Relation switch
    {
        DedupPairRelation.SalaryWindowMatch => DeduplicationMatchMethods.TitleEmployerSalaryWindow,
        DedupPairRelation.DescriptionMatch => DeduplicationMatchMethods.TitleEmployerLocationDescription,
        _ => null
    };
}

public static class DeduplicationMatchMethods
{
    public const string TitleEmployerSalaryWindow = "title-employer-salary-window";
    public const string TitleEmployerLocationDescription = "title-employer-location-description";
    public const string MatchVersion = "v1";
}

public sealed record DedupGroupPlan(IReadOnlyList<Guid> MemberJobIds, string MatchMethod, string Confidence);

public sealed record DedupReviewPair(Guid JobId, Guid CandidateJobId, string Reason);

public sealed record DeduplicationPlan(IReadOnlyList<DedupGroupPlan> Groups, IReadOnlyList<DedupReviewPair> ReviewCandidates);

/// <summary>
/// Conservative v1 grouping rules, in priority order. A pair is only merged on
/// strong evidence; anything weaker is reported for human review. The engine is
/// pure so every boundary can be unit-tested directly.
/// </summary>
public static class DeduplicationEngine
{
    public const int MaxGroupSize = 4;

    public static DedupPairEvaluation Evaluate(DedupListing left, DedupListing right)
    {
        if (string.Equals(left.Source, right.Source, StringComparison.OrdinalIgnoreCase))
        {
            return new DedupPairEvaluation(DedupPairRelation.SameSource);
        }

        if (!string.Equals(left.NormalizedTitle, right.NormalizedTitle, StringComparison.Ordinal)
            || left.EmployerCanonicalId != right.EmployerCanonicalId)
        {
            return new DedupPairEvaluation(DedupPairRelation.NoRelation);
        }

        // Conflicting salary text is a hard stop: two present-but-different
        // salaries must never be treated as the same vacancy, whatever the
        // description similarity says.
        if (left.HasSalary && right.HasSalary && !string.Equals(left.SalaryKey, right.SalaryKey, StringComparison.Ordinal))
        {
            return new DedupPairEvaluation(DedupPairRelation.SalaryConflict);
        }

        // Rule (b): identical title + employer + salary text (both present or
        // both absent) and representative dates within 7 days. When the salary
        // is absent on both sides, the description similarity is the guard
        // against legitimate twin openings (the brief's stated hazard); an
        // identical present salary is strong enough on its own.
        var salaryCompatible = left.HasSalary == right.HasSalary
            && (!left.HasSalary || string.Equals(left.SalaryKey, right.SalaryKey, StringComparison.Ordinal));
        var withinSevenDays = Math.Abs((left.RepresentativeDate - right.RepresentativeDate).TotalDays) <= 7d;
        if (salaryCompatible && withinSevenDays)
        {
            if (left.HasSalary || IsDescriptionMatch(left, right))
            {
                return new DedupPairEvaluation(DedupPairRelation.SalaryWindowMatch);
            }
        }

        // Rule (c): same title + employer + location city, overlapping posting
        // window and description similarity >= 0.8.
        if (left.LocationKey.Length > 0
            && string.Equals(left.LocationKey, right.LocationKey, StringComparison.Ordinal)
            && WindowsOverlap(left, right)
            && IsDescriptionMatch(left, right))
        {
            return new DedupPairEvaluation(DedupPairRelation.DescriptionMatch);
        }

        return new DedupPairEvaluation(DedupPairRelation.NoRelation);
    }

    private static bool IsDescriptionMatch(DedupListing left, DedupListing right) =>
        DescriptionSimilarity.Jaccard(left.DescriptionShingles, right.DescriptionShingles) >= DescriptionSimilarity.MergeThreshold;

    public static DeduplicationPlan Plan(IReadOnlyList<DedupListing> listings, int maxGroupSize = MaxGroupSize)
    {
        var groups = new List<DedupGroupPlan>();
        var reviewPairs = new HashSet<(Guid, Guid)>();
        var review = new List<DedupReviewPair>();

        foreach (var bucket in listings.GroupBy(listing => (listing.NormalizedTitle, listing.EmployerCanonicalId)))
        {
            var items = bucket.ToList();
            if (items.Count < 2)
            {
                continue;
            }

            var parent = Enumerable.Range(0, items.Count).ToArray();
            var evaluations = new Dictionary<(int, int), DedupPairEvaluation>();

            for (var i = 0; i < items.Count; i++)
            {
                for (var j = i + 1; j < items.Count; j++)
                {
                    var evaluation = Evaluate(items[i], items[j]);
                    evaluations[(i, j)] = evaluation;
                    if (evaluation.IsMatch)
                    {
                        Union(parent, i, j);
                    }
                    else if (evaluation.Relation is DedupPairRelation.NoRelation or DedupPairRelation.SalaryConflict)
                    {
                        AddReview(review, reviewPairs, items[i].JobId, items[j].JobId,
                            evaluation.Relation == DedupPairRelation.SalaryConflict ? "salary-conflict" : "gates-failed");
                    }
                }
            }

            foreach (var component in Enumerable.Range(0, items.Count).GroupBy(index => Find(parent, index)))
            {
                var indices = component.ToList();
                if (indices.Count < 2)
                {
                    continue;
                }

                if (indices.Count > maxGroupSize)
                {
                    // The whole larger set is handed to a human rather than
                    // arbitrarily merging four of them.
                    var anchor = indices.OrderBy(index => items[index].RepresentativeDate).First();
                    foreach (var index in indices.Where(index => index != anchor))
                    {
                        AddReview(review, reviewPairs, items[anchor].JobId, items[index].JobId, "group-cap-exceeded");
                    }

                    continue;
                }

                var method = DeduplicationMatchMethods.TitleEmployerLocationDescription;
                for (var i = 0; i < items.Count; i++)
                {
                    for (var j = i + 1; j < items.Count; j++)
                    {
                        if (indices.Contains(i) && indices.Contains(j)
                            && evaluations.TryGetValue((i, j), out var evaluation)
                            && evaluation.Relation == DedupPairRelation.SalaryWindowMatch)
                        {
                            method = DeduplicationMatchMethods.TitleEmployerSalaryWindow;
                        }
                    }
                }

                groups.Add(new DedupGroupPlan(indices.Select(index => items[index].JobId).ToList(), method, "high"));
            }
        }

        return new DeduplicationPlan(groups, review);
    }

    private static void AddReview(
        List<DedupReviewPair> review,
        HashSet<(Guid, Guid)> seen,
        Guid left,
        Guid right,
        string reason)
    {
        var key = left.CompareTo(right) <= 0 ? (left, right) : (right, left);
        if (seen.Add(key))
        {
            review.Add(new DedupReviewPair(key.Item1, key.Item2, reason));
        }
    }

    private static bool WindowsOverlap(DedupListing left, DedupListing right) =>
        left.WindowStart <= right.WindowEnd && right.WindowStart <= left.WindowEnd;

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int left, int right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot != rightRoot)
        {
            parent[leftRoot] = rightRoot;
        }
    }
}
