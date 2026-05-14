using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Backend.Plugins.Configuration;
using Microsoft.Extensions.Configuration;

namespace Backend.Plugins.Harness;

internal static class PluginConfigurationBinder
{
    private static readonly NullabilityInfoContext NullabilityContext = new();

    public static TOptions Bind<TOptions>(IConfiguration configuration)
        where TOptions : class
    {
        var optionsType = typeof(TOptions);
        var sectionPath = PluginConfigurationKey.GetSectionPath(optionsType);
        var section = configuration.GetSection(sectionPath);

        return (TOptions)BindObject(optionsType, section, sectionPath)!;
    }

    private static object? BindObject(Type objectType, IConfigurationSection section, string path)
    {
        var instance = Activator.CreateInstance(objectType)
            ?? throw new InvalidOperationException(
                $"Could not create plugin configuration type '{objectType.FullName}'.");

        foreach (var property in GetBindableProperties(objectType))
        {
            var childSection = section.GetSection(property.Name);
            var propertyPath = $"{path}:{property.Name}";

            if (!HasValue(childSection))
            {
                if (IsRequired(property))
                {
                    throw new InvalidOperationException(
                        $"Required plugin configuration value '{propertyPath}' was not supplied.");
                }

                continue;
            }

            var value = BindValue(property.PropertyType, childSection, propertyPath);
            property.SetValue(instance, value);
        }

        return instance;
    }

    private static object? BindValue(Type targetType, IConfigurationSection section, string path)
    {
        var unwrappedTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (TryGetSecretValueType(unwrappedTargetType, out var secretValueType))
        {
            if (section.Value is null)
            {
                throw new InvalidOperationException(
                    $"Secret plugin configuration value '{path}' must be a scalar value.");
            }

            var secretValue = ConvertScalar(secretValueType, section.Value, path);
            return Activator.CreateInstance(unwrappedTargetType, secretValue);
        }

        if (unwrappedTargetType != typeof(string) &&
            typeof(IEnumerable).IsAssignableFrom(unwrappedTargetType) &&
            TryGetCollectionElementType(unwrappedTargetType, out var elementType))
        {
            return BindCollection(unwrappedTargetType, elementType, section, path);
        }

        if (IsLeafType(unwrappedTargetType))
        {
            if (section.Value is null)
            {
                throw new InvalidOperationException(
                    $"Plugin configuration value '{path}' must be a scalar value.");
            }

            return ConvertScalar(unwrappedTargetType, section.Value, path);
        }

        return BindObject(unwrappedTargetType, section, path);
    }

    private static object BindCollection(Type targetType, Type elementType, IConfigurationSection section, string path)
    {
        var values = section
            .GetChildren()
            .Select((child, index) =>
            {
                var childPath = $"{path}:{index}";
                if (child.Value is null)
                {
                    throw new InvalidOperationException(
                        $"Collection plugin configuration value '{childPath}' must be a scalar value.");
                }

                return ConvertScalar(elementType, child.Value, childPath);
            })
            .ToArray();

        if (targetType.IsArray)
        {
            var array = Array.CreateInstance(elementType, values.Length);
            for (var index = 0; index < values.Length; index++)
            {
                array.SetValue(values[index], index);
            }

            return array;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var value in values)
        {
            list.Add(value);
        }

        return list;
    }

    private static object? ConvertScalar(Type targetType, string value, string path)
    {
        try
        {
            if (targetType == typeof(string))
            {
                return value;
            }

            if (targetType == typeof(Uri))
            {
                return new Uri(value, UriKind.RelativeOrAbsolute);
            }

            if (targetType == typeof(TimeSpan))
            {
                return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(DateTimeOffset))
            {
                return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(DateTime))
            {
                return DateTime.Parse(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(DateOnly))
            {
                return DateOnly.Parse(value, CultureInfo.InvariantCulture);
            }

            if (targetType == typeof(Guid))
            {
                return Guid.Parse(value);
            }

            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, value, ignoreCase: true);
            }

            var converter = TypeDescriptor.GetConverter(targetType);
            if (converter.CanConvertFrom(typeof(string)))
            {
                return converter.ConvertFromInvariantString(value);
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or NotSupportedException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Plugin configuration value '{path}' could not be converted to '{targetType.Name}'.",
                ex);
        }

        throw new InvalidOperationException(
            $"Plugin configuration value '{path}' uses unsupported type '{targetType.FullName}'.");
    }

    private static IReadOnlyList<PropertyInfo> GetBindableProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0 &&
                               property.SetMethod is not null &&
                               property.SetMethod.IsPublic)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();

    private static bool HasValue(IConfigurationSection section) =>
        section.Value is not null || section.GetChildren().Any();

    private static bool IsRequired(PropertyInfo property)
    {
        if (property.GetCustomAttribute<RequiredMemberAttribute>() is not null)
        {
            return true;
        }

        var propertyType = property.PropertyType;
        if (propertyType.IsValueType)
        {
            return Nullable.GetUnderlyingType(propertyType) is null;
        }

        return NullabilityContext.Create(property).WriteState == NullabilityState.NotNull;
    }

    private static bool IsLeafType(Type type) =>
        type.IsEnum ||
        type == typeof(string) ||
        type == typeof(bool) ||
        type == typeof(byte) ||
        type == typeof(short) ||
        type == typeof(int) ||
        type == typeof(long) ||
        type == typeof(float) ||
        type == typeof(double) ||
        type == typeof(decimal) ||
        type == typeof(Guid) ||
        type == typeof(Uri) ||
        type == typeof(DateOnly) ||
        type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan);

    private static bool TryGetSecretValueType(Type type, out Type valueType)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Secret<>))
        {
            valueType = type.GetGenericArguments()[0];
            return true;
        }

        valueType = type;
        return false;
    }

    private static bool TryGetCollectionElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        var enumerableType = type
            .GetInterfaces()
            .Concat([type])
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (enumerableType is not null && type != typeof(string))
        {
            elementType = enumerableType.GetGenericArguments()[0];
            return true;
        }

        elementType = type;
        return false;
    }
}
