using System.Collections;
using System.Globalization;

namespace PureEngine.Core;

/// <summary>Shared zero-based array storage and row-major coordinate handling.</summary>
public static class InspectorArrayShape
{
    /// <summary>Captures vectors as sequences and rectangular arrays as dimensions plus flat items.</summary>
    public static object Capture(Array array, Func<object?, object?> capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return Capture(array, "Array", (value, _) => capture(value));
    }

    public static object Capture(Array array, string path, Func<object?, string, object?> capture)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentNullException.ThrowIfNull(capture);
        ValidateArray(array, path);
        List<object?> items = [];
        foreach (var indices in Indices(array))
            items.Add(capture(array.GetValue(indices), ItemPath(path, indices)));
        if (array.Rank == 1)
            return items;
        return new Dictionary<string, object?>
        {
            ["dimensions"] = Enumerable.Range(0, array.Rank).Select(array.GetLength).ToList(),
            ["items"] = items,
        };
    }

    public static Array Restore(object raw, Type arrayType, string path, Func<object?, string, object?> restore)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(arrayType);
        ArgumentNullException.ThrowIfNull(restore);
        if (!arrayType.IsArray || (arrayType.GetArrayRank() == 1 && !arrayType.IsSZArray))
            throw new InvalidDataException($"{path}: a zero-based array type is required.");
        var rank = arrayType.GetArrayRank();
        int[] dimensions;
        IList items;
        if (raw is Array typed)
        {
            ValidateArray(typed, path);
            if (typed.Rank != rank)
                throw new InvalidDataException($"{path}: array rank does not match.");
            dimensions = [.. Enumerable.Range(0, rank).Select(typed.GetLength)];
            items = typed.Cast<object?>().ToList();
        }
        else if (rank == 1)
        {
            items = Sequence(raw, path);
            dimensions = [items.Count];
        }
        else
        {
            if (raw is not IDictionary mapping || mapping.Count != 2
                || !mapping.Contains("dimensions") || !mapping.Contains("items"))
                throw new InvalidDataException($"{path}: rectangular arrays require dimensions and items.");
            var lengths = Sequence(mapping["dimensions"], $"{path}.dimensions");
            if (lengths.Count != rank)
                throw new InvalidDataException($"{path}: array rank does not match.");
            dimensions = new int[rank];
            for (var dimension = 0; dimension < rank; dimension++)
                dimensions[dimension] = ReadLength(lengths[dimension], path);
            items = Sequence(mapping["items"], $"{path}.items");
        }
        var count = ValidateDimensions(dimensions, path);
        if (count != items.Count)
            throw new InvalidDataException($"{path}: array dimensions do not match the item count.");
        var array = Create(arrayType.GetElementType()!, dimensions, path);
        var flatIndex = 0;
        foreach (var indices in Indices(array))
            array.SetValue(restore(items[flatIndex++], ItemPath(path, indices)), indices);
        return array;
    }

    /// <summary>Creates a zero-based array after checking CLR dimension and total-length limits.</summary>
    public static Array Create(Type elementType, int[] dimensions, string path)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        ValidateDimensions(dimensions, path);
        try
        {
            return Array.CreateInstance(elementType, dimensions);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or OutOfMemoryException or OverflowException)
        {
            throw new InvalidDataException($"{path}: array shape cannot be allocated.", error);
        }
    }

    /// <summary>Validates rank, dimensions and product without allocating the array.</summary>
    public static int ValidateDimensions(int[] dimensions, string path)
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        if (dimensions.Length is < 1 or > 32 || dimensions.Any(length => length < 0 || length > Array.MaxLength))
            throw new InvalidDataException($"{path}: invalid array dimensions.");
        if (dimensions.Contains(0))
            return 0;
        long count = 1;
        foreach (var length in dimensions)
        {
            count *= length;
            if (count > Array.MaxLength)
                throw new InvalidDataException($"{path}: array dimensions exceed the supported element count.");
        }
        return (int)count;
    }

    public static int[] GetIndices(Array array, int flatIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (flatIndex < 0 || flatIndex >= array.Length)
            throw new ArgumentOutOfRangeException(nameof(flatIndex));
        var indices = new int[array.Rank];
        for (var dimension = array.Rank - 1; dimension >= 0; dimension--)
        {
            indices[dimension] = flatIndex % array.GetLength(dimension);
            flatIndex /= array.GetLength(dimension);
        }
        return indices;
    }

    public static IEnumerable<int[]> Indices(Array array)
    {
        ArgumentNullException.ThrowIfNull(array);
        ValidateArray(array, "Array");
        for (var flatIndex = 0; flatIndex < array.Length; flatIndex++)
            yield return GetIndices(array, flatIndex);
    }

    private static string ItemPath(string path, int[] indices) => $"{path}[{string.Join(",", indices)}]";

    private static void ValidateArray(Array array, string path)
    {
        for (var dimension = 0; dimension < array.Rank; dimension++)
            if (array.GetLowerBound(dimension) != 0)
                throw new InvalidDataException($"{path}: array lower bounds must be zero.");
        ValidateDimensions([.. Enumerable.Range(0, array.Rank).Select(array.GetLength)], path);
    }

    private static IList Sequence(object? raw, string path)
    {
        if (raw is IList list && (raw is not Array array || array.Rank == 1 && array.GetLowerBound(0) == 0))
            return list;
        throw new InvalidDataException($"{path}: an array sequence is required.");
    }

    private static int ReadLength(object? raw, string path)
    {
        if (raw is int length)
            return length;
        if (raw is long wide && wide is >= 0 and <= int.MaxValue)
            return (int)wide;
        if (raw is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
            return length;
        throw new InvalidDataException($"{path}: invalid array dimension.");
    }
}
