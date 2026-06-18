using System;

namespace WalhallaSql.EfCore;

internal static class EfSaveChangesGuardrail
{
    internal const string Prefix = "WalhallaSql EF SaveChanges MVP limitation:";

    internal static class Codes
    {
        public const string Generic = "LSQ-EF-SAVE-000";
        public const string ComplexGraph = "LSQ-EF-SAVE-001";
        public const string OwnedTypes = "LSQ-EF-SAVE-002";
        public const string SingleColumnPrimaryKey = "LSQ-EF-SAVE-003";
        public const string NonNullPrimaryKey = "LSQ-EF-SAVE-004";
        public const string ShadowProperty = "LSQ-EF-SAVE-005";
        public const string NoMappedScalarProperties = "LSQ-EF-SAVE-006";
        public const string ConcurrencyNoRowsAffected = "LSQ-EF-SAVE-007";
        public const string UnsupportedKeyGeneration = "LSQ-EF-SAVE-008";
        public const string NoOpModifiedEntry = "LSQ-EF-SAVE-009";
        public const string ExternalEfTransaction = "LSQ-EF-SAVE-010";
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
}
