using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace WalhallaSql.EfCore;

internal static class WalhallaSqlStoreObjectNameSanitizer
{
    public static string ResolveDefaultCollectionName(IEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        var tableName = entityType.GetTableName();
        if (!string.IsNullOrWhiteSpace(tableName))
            return Sanitize(tableName);

        return Sanitize(entityType.ClrType?.Name ?? entityType.Name);
    }

    public static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Entity";

        if (IsValidIdentifier(name))
            return name;

        var builder = new StringBuilder(name.Length);
        var previousWasUnderscore = false;

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character) || character == '_' || (character == '$' && builder.Length == 0))
            {
                builder.Append(character);
                previousWasUnderscore = false;
                continue;
            }

            if (character == '-' && builder.Length > 0)
            {
                builder.Append(character);
                previousWasUnderscore = false;
                continue;
            }

            if (previousWasUnderscore)
                continue;

            builder.Append('_');
            previousWasUnderscore = true;
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "Entity" : sanitized;
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        if (!char.IsLetter(name[0]) && name[0] != '_')
            return false;

        for (var i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                return false;
        }

        return true;
    }

    public static string MakeUnique(string baseName, ISet<string> usedNames)
    {
        if (usedNames.Add(baseName))
            return baseName;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName}_{suffix}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }
}
