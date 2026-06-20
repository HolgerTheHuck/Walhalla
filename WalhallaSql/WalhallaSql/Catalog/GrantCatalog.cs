using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WalhallaSql.Catalog;

/// <summary>
/// Typ des Objekts, auf das ein Grant angewendet wird.
/// </summary>
public enum GrantObjectType
{
    Table,
    View,
    Procedure
}

/// <summary>
/// Unterstuetzte Privilegien fuer Grants.
/// </summary>
public enum GrantPrivilege
{
    Select,
    Insert,
    Update,
    Delete,
    Execute
}

/// <summary>
/// Ein einzelner Grant-Eintrag.
/// </summary>
public sealed record GrantEntry(
    string Grantee,
    GrantObjectType ObjectType,
    string ObjectName,
    GrantPrivilege Privilege);

/// <summary>
/// In-memory Katalog fuer Rechte (Grants), persistiert als <c>grants.json</c>.
/// </summary>
public sealed class GrantCatalog
{
    private readonly string? _persistPath;
    private readonly HashSet<GrantEntry> _entries;
    private readonly object _sync = new();

    public GrantCatalog(string? rootPath)
    {
        if (!string.IsNullOrEmpty(rootPath))
        {
            _persistPath = Path.Combine(rootPath, "grants.json");
            _entries = Load();
        }
        else
        {
            _entries = new HashSet<GrantEntry>();
        }
    }

    /// <summary>
    /// Gibt eine Rolle ein Privileg auf einem Objekt.
    /// </summary>
    public void Grant(GrantEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Grantee))
            throw new ArgumentException("Grantee must not be empty.", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.ObjectName))
            throw new ArgumentException("Object name must not be empty.", nameof(entry));

        lock (_sync)
        {
            _entries.Add(entry);
            SaveLocked();
        }
    }

    /// <summary>
    /// Entzieht einer Rolle ein Privileg auf einem Objekt.
    /// </summary>
    public bool Revoke(GrantEntry entry)
    {
        lock (_sync)
        {
            if (!_entries.Remove(entry))
                return false;

            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Prueft, ob die angegebene Rolle ein Privileg auf einem Objekt besitzt.
    /// </summary>
    public bool HasPrivilege(string grantee, GrantObjectType objectType, string objectName, GrantPrivilege privilege)
    {
        lock (_sync)
        {
            return _entries.Contains(new GrantEntry(grantee, objectType, objectName, privilege));
        }
    }

    /// <summary>
    /// Liefert alle Privilegien einer Rolle auf einem bestimmten Objekt.
    /// </summary>
    public IReadOnlyList<GrantPrivilege> GetPrivileges(string grantee, GrantObjectType objectType, string objectName)
    {
        lock (_sync)
        {
            return _entries
                .Where(e =>
                    string.Equals(e.Grantee, grantee, StringComparison.OrdinalIgnoreCase) &&
                    e.ObjectType == objectType &&
                    string.Equals(e.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Privilege)
                .ToList();
        }
    }

    /// <summary>
    /// Entfernt alle Grants fuer ein geloeschtes Objekt.
    /// </summary>
    public void RevokeAllForObject(GrantObjectType objectType, string objectName)
    {
        lock (_sync)
        {
            var toRemove = _entries
                .Where(e => e.ObjectType == objectType &&
                           string.Equals(e.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var entry in toRemove)
                _entries.Remove(entry);

            if (toRemove.Count > 0)
                SaveLocked();
        }
    }

    /// <summary>
    /// Entfernt alle Grants einer geloeschten Rolle.
    /// </summary>
    public void RevokeAllForGrantee(string grantee)
    {
        lock (_sync)
        {
            var toRemove = _entries
                .Where(e => string.Equals(e.Grantee, grantee, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var entry in toRemove)
                _entries.Remove(entry);

            if (toRemove.Count > 0)
                SaveLocked();
        }
    }

    private HashSet<GrantEntry> Load()
    {
        if (_persistPath == null || !File.Exists(_persistPath))
            return new HashSet<GrantEntry>();

        try
        {
            var json = File.ReadAllText(_persistPath);
            var dtos = JsonSerializer.Deserialize<List<GrantEntryDto>>(json);
            if (dtos == null)
                return new HashSet<GrantEntry>();

            var entries = new HashSet<GrantEntry>();
            foreach (var dto in dtos)
            {
                if (string.IsNullOrWhiteSpace(dto.Grantee) || string.IsNullOrWhiteSpace(dto.ObjectName))
                    continue;

                if (!Enum.TryParse<GrantObjectType>(dto.ObjectType, out var objectType))
                    continue;
                if (!Enum.TryParse<GrantPrivilege>(dto.Privilege, out var privilege))
                    continue;

                entries.Add(new GrantEntry(dto.Grantee, objectType, dto.ObjectName, privilege));
            }

            return entries;
        }
        catch
        {
            return new HashSet<GrantEntry>();
        }
    }

    private void SaveLocked()
    {
        if (_persistPath == null) return;

        var dtos = _entries
            .Select(e => new GrantEntryDto(
                e.Grantee,
                e.ObjectType.ToString(),
                e.ObjectName,
                e.Privilege.ToString()))
            .ToList();

        var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var dir = Path.GetDirectoryName(_persistPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_persistPath, json);
    }

    private sealed record GrantEntryDto(string Grantee, string ObjectType, string ObjectName, string Privilege);
}
