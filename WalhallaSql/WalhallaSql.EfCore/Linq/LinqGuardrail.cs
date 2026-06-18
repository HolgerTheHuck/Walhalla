using System;

namespace WalhallaSql.EfCore.Linq;

internal static class LinqGuardrail
{
    internal const string Prefix = "WalhallaSql EF LINQ MVP limitation:";

    internal static class Codes
    {
        public const string Generic = "LSQ-EF-LINQ-000";

        public const string PredicateMemberToConstant = "LSQ-EF-LINQ-001";
        public const string PredicateExpressionNode = "LSQ-EF-LINQ-002";
        public const string PredicateContainsSignature = "LSQ-EF-LINQ-003";
        public const string PredicateContainsColumnMembership = "LSQ-EF-LINQ-004";
        public const string PredicateContainsSource = "LSQ-EF-LINQ-005";
        public const string PredicateMethodUnsupported = "LSQ-EF-LINQ-006";
        public const string PredicateOperatorUnsupported = "LSQ-EF-LINQ-007";

        public const string IncludeApiUsage = "LSQ-EF-LINQ-101";
        public const string IncludeShapeLimit = "LSQ-EF-LINQ-102";
        public const string IncludeMappingLimit = "LSQ-EF-LINQ-103";
        public const string IncludeSelectorLimit = "LSQ-EF-LINQ-104";

        public const string QuerySelectorLimit = "LSQ-EF-LINQ-201";
        public const string QueryModelMappingLimit = "LSQ-EF-LINQ-202";
    }

    public static NotSupportedException NotSupported(string message)
    {
        return NotSupported(Codes.Generic, message);
    }

    public static NotSupportedException NotSupported(string code, string message)
    {
        if (string.IsNullOrWhiteSpace(code))
            code = Codes.Generic;

        return new NotSupportedException($"{Prefix} [{code}] {message}");
    }

    public static NotSupportedException NotSupportedWithHint(string code, string message, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return NotSupported(code, message);

        return NotSupported(code, $"{message} Try this instead: {hint}");
    }

    public static NotSupportedException PredicateMemberToConstantOnly()
    {
        return NotSupportedWithHint(
            Codes.PredicateMemberToConstant,
            "Only member-to-constant predicates are supported.",
            "Compare a direct entity property with a constant value, e.g. user => user.Age >= 18.");
    }

    public static NotSupportedException PredicateContainsColumnMembershipOnly()
    {
        return NotSupportedWithHint(
            Codes.PredicateContainsColumnMembership,
            "Contains supports only column membership checks.",
            "Use a value-list membership check like ids.Contains(entity.Id), not entity.Name.Contains(\"x\").");
    }

    public static NotSupportedException PredicateContainsRequiresEnumerableSource()
    {
        return NotSupportedWithHint(
            Codes.PredicateContainsSource,
            "Contains requires an enumerable constant/source value.",
            "Use an array/list variable or literal enumerable as Contains source.");
    }

    public static NotSupportedException PredicateMethodNotSupported(string methodName)
    {
        return NotSupportedWithHint(
            Codes.PredicateMethodUnsupported,
            $"Method '{methodName}' is not supported.",
            "Use supported predicate methods such as StartsWith(...) or Enumerable.Contains(...).");
    }

    public static NotSupportedException IncludeThenIncludeRequiresInclude()
    {
        return NotSupportedWithHint(
            Codes.IncludeApiUsage,
            "ThenInclude requires a preceding Include call. Call Include(...) before ThenInclude(...).",
            "Start with Include(x => x.Navigation) and chain ThenInclude(...) afterwards.");
    }

    public static NotSupportedException QueryOrderBySingleColumnOnly()
    {
        return NotSupportedWithHint(
            Codes.QuerySelectorLimit,
            "OrderBy supports exactly one column selector.",
            "Use one property in OrderBy (and chain additional columns via ThenBy/ThenByDescending).");
    }

    public static NotSupportedException QuerySelectDirectPropertyOnly()
    {
        return NotSupportedWithHint(
            Codes.QuerySelectorLimit,
            "Select supports only direct property selectors.",
            "Project direct properties only, e.g. Select(x => new { x.Id, x.Name }).");
    }
}
