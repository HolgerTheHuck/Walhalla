// Copyright (c) 2026 HolgerTheHuck
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.VectorData;

namespace Walhalla.VectorStore.Microsoft.Extensions.VectorData;

/// <summary>
/// Mappt zwischen TRecord und Walhalla.VectorStore-Typen (Vector, VectorMetadata, ulong-Key).
/// </summary>
internal sealed class WalhallaRecordMapper<TRecord>
{
    private readonly VectorStoreCollectionDefinition? _definition;
    private readonly PropertyInfo? _keyProperty;
    private readonly PropertyInfo? _vectorProperty;
    private readonly PropertyInfo[] _dataProperties;
    private readonly int _dimensions;

    public WalhallaRecordMapper(VectorStoreCollectionDefinition? definition)
    {
        _definition = definition;
        var recordType = typeof(TRecord);

        if (definition is not null)
        {
            var keyPropDef = definition.Properties.OfType<VectorStoreKeyProperty>().FirstOrDefault();
            var vecPropDef = definition.Properties.OfType<VectorStoreVectorProperty>().FirstOrDefault();
            var dataPropDefs = definition.Properties.OfType<VectorStoreDataProperty>().ToList();

            _keyProperty = keyPropDef is not null
                ? recordType.GetProperty(keyPropDef.Name)
                : null;

            _vectorProperty = vecPropDef is not null
                ? recordType.GetProperty(vecPropDef.Name)
                : null;

            _dataProperties = dataPropDefs
                .Select(dp => recordType.GetProperty(dp.Name))
                .Where(p => p is not null)
                .ToArray()!;

            _dimensions = vecPropDef?.Dimensions ?? 0;
        }
        else
        {
            var props = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            _keyProperty = props.FirstOrDefault(p =>
                p.GetCustomAttribute<VectorStoreKeyAttribute>() is not null);

            _vectorProperty = props.FirstOrDefault(p =>
                p.GetCustomAttribute<VectorStoreVectorAttribute>() is not null);

            _dataProperties = props
                .Where(p => p.GetCustomAttribute<VectorStoreDataAttribute>() is not null)
                .ToArray();

            _dimensions = _vectorProperty?.GetCustomAttribute<VectorStoreVectorAttribute>()?.Dimensions ?? 0;
        }
    }

    /// <summary>
    /// Extrahiert Key, Vector und Metadata aus dem Record.
    /// </summary>
    public (ulong Id, Vector Vector, VectorMetadata? Metadata) ToWalhalla(TRecord record)
    {
        var keyValue = _keyProperty?.GetValue(record);
        ulong id = ConvertKeyToULong(keyValue);

        var vectorValue = _vectorProperty?.GetValue(record);
        var vector = ConvertToVector(vectorValue);

        if (_dataProperties.Length == 0)
            return (id, vector, null);

        var payload = new Dictionary<string, object>();
        foreach (var prop in _dataProperties)
        {
            if (prop is null) continue;
            payload[prop.Name] = prop.GetValue(record)!;
        }

        var metadata = new VectorMetadata
        {
            Id = id,
            Collection = string.Empty,
            Payload = payload
        };

        return (id, vector, metadata);
    }

    /// <summary>
    /// Baut einen TRecord aus einem VectorEntry zurück.
    /// </summary>
    public TRecord FromEntry(VectorEntry entry, bool includeVector)
    {
        var record = Activator.CreateInstance<TRecord>()
                     ?? throw new InvalidOperationException($"Cannot create instance of {typeof(TRecord)}. Ensure a parameterless constructor exists.");

        if (_keyProperty is not null)
        {
            var keyValue = ConvertULongToKey(_keyProperty.PropertyType, entry.Id);
            _keyProperty.SetValue(record, keyValue);
        }

        if (includeVector && _vectorProperty is not null)
        {
            var vecValue = ConvertVectorToPropertyType(_vectorProperty.PropertyType, entry.Vector);
            _vectorProperty.SetValue(record, vecValue);
        }

        if (entry.Metadata?.Payload is not null)
        {
            foreach (var prop in _dataProperties)
            {
                if (prop is null) continue;
                if (entry.Metadata.Payload.TryGetValue(prop.Name, out var rawValue))
                {
                    var converted = ConvertJsonValue(rawValue, prop.PropertyType);
                    if (converted is not null)
                        prop.SetValue(record, converted);
                }
            }
        }

        return record;
    }

    public int Dimensions => _dimensions;

    private static ulong ConvertKeyToULong(object? keyValue)
    {
        return keyValue switch
        {
            ulong ul => ul,
            string s when ulong.TryParse(s, out var parsed) => parsed,
            Guid g => BitConverter.ToUInt64(g.ToByteArray(), 0),
            int i => (ulong)i,
            long l => (ulong)l,
            uint ui => ui,
            _ => throw new NotSupportedException(
                $"Key value of type {keyValue?.GetType()?.Name ?? "null"} cannot be mapped to ulong. Supported: ulong, string (parseable), Guid, int, long, uint.")
        };
    }

    private static object? ConvertULongToKey(Type targetType, ulong id)
    {
        if (targetType == typeof(ulong)) return id;
        if (targetType == typeof(string)) return id.ToString();
        if (targetType == typeof(Guid))
        {
            var bytes = new byte[16];
            BitConverter.GetBytes(id).CopyTo(bytes, 0);
            return new Guid(bytes);
        }
        if (targetType == typeof(int)) return (int)id;
        if (targetType == typeof(long)) return (long)id;
        if (targetType == typeof(uint)) return (uint)id;

        throw new NotSupportedException($"Cannot convert ulong key to {targetType}. Supported: ulong, string, Guid, int, long, uint.");
    }

    private Vector ConvertToVector(object? vectorValue)
    {
        float[] data = vectorValue switch
        {
            float[] arr => arr,
            ReadOnlyMemory<float> rom => rom.Span.ToArray(),
            Memory<float> mem => mem.Span.ToArray(),
            _ => throw new ArgumentException(
                $"Vector property must be float[], Memory<float> or ReadOnlyMemory<float>. Got {vectorValue?.GetType()?.Name ?? "null"}.")
        };

        if (_dimensions > 0 && data.Length != _dimensions)
            throw new ArgumentException($"Expected {_dimensions} dimensions, got {data.Length}.");

        return new Vector(data);
    }

    private static object? ConvertVectorToPropertyType(Type propertyType, Vector vector)
    {
        var data = vector.Span.ToArray();
        if (propertyType == typeof(float[])) return data;
        if (propertyType == typeof(ReadOnlyMemory<float>)) return new ReadOnlyMemory<float>(data);
        if (propertyType == typeof(Memory<float>)) return new Memory<float>(data);

        throw new NotSupportedException($"Vector property type {propertyType} is not supported. Use float[], Memory<float> or ReadOnlyMemory<float>.");
    }

    private static object? ConvertJsonValue(object? rawValue, Type targetType)
    {
        if (rawValue is null) return null;
        if (targetType == rawValue.GetType()) return rawValue;

        // JSON-Deserialisierung liefert oft JsonElement oder long statt int
        if (rawValue is System.Text.Json.JsonElement jsonElement)
        {
            return JsonElementToObject(jsonElement, targetType);
        }

        try
        {
            return Convert.ChangeType(rawValue, targetType);
        }
        catch
        {
            return rawValue;
        }
    }

    private static object? JsonElementToObject(System.Text.Json.JsonElement element, Type targetType)
    {
        if (targetType == typeof(string)) return element.GetString();
        if (targetType == typeof(int)) return element.GetInt32();
        if (targetType == typeof(long)) return element.GetInt64();
        if (targetType == typeof(double)) return element.GetDouble();
        if (targetType == typeof(bool)) return element.GetBoolean();
        if (targetType == typeof(DateTime)) return element.GetDateTime();
        if (targetType == typeof(DateTimeOffset)) return element.GetDateTimeOffset();
        if (Nullable.GetUnderlyingType(targetType) is Type underlying)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Null) return null;
            return JsonElementToObject(element, underlying);
        }
        if (targetType.IsEnum && element.ValueKind == System.Text.Json.JsonValueKind.String)
            return Enum.Parse(targetType, element.GetString()!);
        if (targetType.IsEnum && element.ValueKind == System.Text.Json.JsonValueKind.Number)
            return Enum.ToObject(targetType, element.GetInt32());

        return element.ToString();
    }
}
