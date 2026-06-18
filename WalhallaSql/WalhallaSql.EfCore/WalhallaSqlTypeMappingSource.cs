using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlTypeMappingSource : RelationalTypeMappingSource
{
    private readonly StringTypeMapping _string = new WalhallaSqlStringTypeMapping();
    private readonly CharTypeMapping _char = new("TEXT", DbType.String);
    private readonly DateOnlyTypeMapping _dateOnly = new("TEXT", DbType.Date);
    private readonly TimeOnlyTypeMapping _timeOnly = new("TEXT", DbType.Time);
    private readonly TimeSpanTypeMapping _timeSpan = new("TEXT", DbType.Time);
    private readonly ByteTypeMapping _byte = new("INTEGER", DbType.Byte);
    private readonly SByteTypeMapping _sbyte = new("INTEGER", DbType.SByte);
    private readonly ShortTypeMapping _short = new("INTEGER", DbType.Int16);
    private readonly UShortTypeMapping _ushort = new("INTEGER", DbType.UInt16);
    private readonly IntTypeMapping _int = new("INTEGER", DbType.Int32);
    private readonly UIntTypeMapping _uint = new("BIGINT", DbType.UInt32);
    private readonly LongTypeMapping _long = new("INTEGER", DbType.Int64);
    private readonly ULongTypeMapping _ulong = new("DECIMAL", DbType.UInt64);
    private readonly FloatTypeMapping _float = new("REAL", DbType.Single);
    private readonly DoubleTypeMapping _double = new("REAL", DbType.Double);
    private readonly DecimalTypeMapping _decimal = new("DECIMAL", DbType.Decimal);
    private readonly DateTimeTypeMapping _dateTime = new("DATETIME", DbType.DateTime);
    private readonly RelationalTypeMapping _dateTimeOffset = new DateTimeOffsetStringTypeMapping();
    private readonly GuidTypeMapping _guid = new("GUID", DbType.Guid);
    private readonly ByteArrayTypeMapping _bytes = new("BLOB", DbType.Binary);
    private readonly BoolTypeMapping _bool = new("INTEGER", DbType.Boolean);
    private readonly RelationalTypeMapping _jsonElement = new WalhallaSqlStructuralJsonTypeMapping(typeof(JsonElement));
    private readonly RelationalTypeMapping _jsonDocument = new WalhallaSqlStructuralJsonTypeMapping(typeof(JsonDocument));

    public WalhallaSqlTypeMappingSource(
        TypeMappingSourceDependencies dependencies,
        RelationalTypeMappingSourceDependencies relationalDependencies)
        : base(dependencies, relationalDependencies)
    {
    }

    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType;
        var storeType = mappingInfo.StoreTypeNameBase ?? mappingInfo.StoreTypeName;

        if (string.Equals(storeType, "JSON", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storeType, "JSONB", StringComparison.OrdinalIgnoreCase))
        {
            // Der interne JsonTypePlaceholder wird beim Bau des relationalen Modells
            // mit storeTypeName "JSON" abgefragt; wir liefern unseren JSON-Mapping.
            return clrType == typeof(JsonDocument)
                ? _jsonDocument
                : _jsonElement;
        }

        if (clrType == typeof(string))
            return _string;

        if (clrType == typeof(JsonDocument))
            return _jsonDocument;

        if (clrType == typeof(JsonElement))
            return _jsonElement;

        if (clrType == typeof(char))
            return _char;

        if (clrType == typeof(byte))
            return _byte;

        if (clrType == typeof(sbyte))
            return _sbyte;

        if (clrType == typeof(short))
            return _short;

        if (clrType == typeof(ushort))
            return _ushort;

        if (clrType == typeof(int))
            return _int;

        if (clrType == typeof(DateOnly))
            return _dateOnly;

        if (clrType == typeof(TimeOnly))
            return _timeOnly;

        if (clrType == typeof(TimeSpan))
            return _timeSpan;

        if (clrType == typeof(uint))
            return _uint;

        if (clrType == typeof(long))
            return _long;

        if (clrType == typeof(ulong))
            return _ulong;

        if (clrType == typeof(float))
            return _float;

        if (clrType == typeof(double))
            return _double;

        if (clrType == typeof(decimal))
        {
            var precision = mappingInfo.Precision;
            var scale = mappingInfo.Scale;
            if (precision > 28 || scale > 28)
            {
                return new DecimalTypeMapping("DECIMAL", DbType.Decimal,
                    precision.HasValue ? Math.Min(precision.Value, 28) : (int?)null,
                    scale.HasValue ? Math.Min(scale.Value, 28) : (int?)null);
            }
            return _decimal;
        }

        if (clrType == typeof(DateTime))
            return _dateTime;

        if (clrType == typeof(DateTimeOffset))
            return _dateTimeOffset;

        if (clrType == typeof(Guid))
            return _guid;

        if (clrType == typeof(byte[]) && mappingInfo.ElementTypeMapping == null)
            return _bytes;

        if (clrType == typeof(bool))
            return _bool;

        if (string.Equals(storeType, "TEXT", StringComparison.OrdinalIgnoreCase))
        {
            if (clrType == typeof(DateOnly))
                return _dateOnly;

            if (clrType == typeof(TimeOnly))
                return _timeOnly;

            if (clrType == typeof(TimeSpan))
                return _timeSpan;

            if (clrType == typeof(char))
                return _char;

            if (clrType == typeof(DateTimeOffset))
                return _dateTimeOffset;

            return _string;
        }
        if (string.Equals(storeType, "INTEGER", StringComparison.OrdinalIgnoreCase))
        {
            if (clrType == typeof(byte))
                return _byte;

            if (clrType == typeof(sbyte))
                return _sbyte;

            if (clrType == typeof(short))
                return _short;

            if (clrType == typeof(ushort))
                return _ushort;

            if (clrType == typeof(uint))
                return _uint;

            if (clrType == typeof(long))
                return _long;

            return _int;
        }

        if (string.Equals(storeType, "REAL", StringComparison.OrdinalIgnoreCase))
            return clrType == typeof(float)
                ? _float
                : _double;

        if (string.Equals(storeType, "FLOAT", StringComparison.OrdinalIgnoreCase))
            return _float;

        if (string.Equals(storeType, "DOUBLE", StringComparison.OrdinalIgnoreCase))
            return _double;

        if (string.Equals(storeType, "DECIMAL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storeType, "NUMERIC", StringComparison.OrdinalIgnoreCase))
        {
            var precision = mappingInfo.Precision;
            var scale = mappingInfo.Scale;
            if (precision > 28 || scale > 28)
            {
                return new DecimalTypeMapping("DECIMAL", DbType.Decimal,
                    precision.HasValue ? Math.Min(precision.Value, 28) : (int?)null,
                    scale.HasValue ? Math.Min(scale.Value, 28) : (int?)null);
            }
            return clrType == typeof(ulong)
                ? _ulong
                : _decimal;
        }

        if (string.Equals(storeType, "DATETIME", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storeType, "TIMESTAMP", StringComparison.OrdinalIgnoreCase)
            || string.Equals(storeType, "DATE", StringComparison.OrdinalIgnoreCase))
            return _dateTime;

        if (string.Equals(storeType, "BLOB", StringComparison.OrdinalIgnoreCase) && mappingInfo.ElementTypeMapping == null)
            return _bytes;

        return null;
    }
}

internal sealed class WalhallaSqlStructuralJsonTypeMapping : JsonTypeMapping
{
    private static readonly MethodInfo GetStringMethod
        = typeof(DbDataReader).GetRuntimeMethod(nameof(DbDataReader.GetString), new[] { typeof(int) })!;

    private static readonly MethodInfo CreateUtf8StreamMethod
        = typeof(WalhallaSqlStructuralJsonTypeMapping).GetMethod(nameof(CreateUtf8Stream), BindingFlags.Static | BindingFlags.NonPublic)!;

    public WalhallaSqlStructuralJsonTypeMapping(Type clrType)
        : base("JSON", clrType, System.Data.DbType.String)
    {
    }

    private WalhallaSqlStructuralJsonTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new WalhallaSqlStructuralJsonTypeMapping(parameters);

    public override MethodInfo GetDataReaderMethod()
        => GetStringMethod;

    public override Expression CustomizeDataReaderExpression(Expression expression)
        => Expression.Call(CreateUtf8StreamMethod, expression);

    protected override string GenerateNonNullSqlLiteral(object value)
        => $"'{GetJsonText(value).Replace("'", "''", StringComparison.Ordinal)}'";

    private static MemoryStream CreateUtf8Stream(string value)
        => new(Encoding.UTF8.GetBytes(value));

    private static string GetJsonText(object value)
        => value switch
        {
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            string text => text,
            _ => throw new InvalidOperationException($"Unsupported JSON value '{value.GetType().Name}'.")
        };
}

internal sealed class WalhallaSqlStringTypeMapping : StringTypeMapping
{
    private static readonly ValueComparer<string> CaseInsensitiveComparer = new(
        (left, right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase),
        value => value == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value),
        value => value);

    public WalhallaSqlStringTypeMapping()
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(
                    typeof(string),
                    jsonValueReaderWriter: JsonStringReaderWriter.Instance,
                    keyComparer: CaseInsensitiveComparer,
                    providerValueComparer: CaseInsensitiveComparer),
                "TEXT",
                StoreTypePostfix.None,
                System.Data.DbType.String,
                unicode: true))
    {
    }

    private WalhallaSqlStringTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new WalhallaSqlStringTypeMapping(parameters);
}

internal sealed class DateTimeOffsetStringTypeMapping : RelationalTypeMapping
{
    private static readonly ValueConverter<DateTimeOffset, string> DateTimeOffsetConverter = new(
        value => value.ToString("O", CultureInfo.InvariantCulture),
        value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

    public DateTimeOffsetStringTypeMapping()
        : this(
            new RelationalTypeMappingParameters(
                new CoreTypeMappingParameters(
                    typeof(DateTimeOffset),
                    converter: DateTimeOffsetConverter,
                    jsonValueReaderWriter: JsonDateTimeOffsetReaderWriter.Instance),
                "TEXT",
                StoreTypePostfix.None,
                System.Data.DbType.String))
    {
    }

    private DateTimeOffsetStringTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new DateTimeOffsetStringTypeMapping(parameters);

    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var text = value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            string providerValue => providerValue,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };

        return $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
