using System.Numerics;

namespace PureEngine.Editor;

internal static class InspectorVectorHelpers
{
    public static float GetAxis(this Vector2 vector, string axis) => axis switch
    {
        "X" => vector.X,
        "Y" => vector.Y,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public static float GetAxis(this Vector3 vector, string axis) => axis switch
    {
        "X" => vector.X,
        "Y" => vector.Y,
        "Z" => vector.Z,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public static float GetAxis(this Vector4 vector, string axis) => axis switch
    {
        "X" => vector.X,
        "Y" => vector.Y,
        "Z" => vector.Z,
        "W" => vector.W,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public static float GetAxis(this Quaternion quaternion, string axis) => axis switch
    {
        "X" => quaternion.X,
        "Y" => quaternion.Y,
        "Z" => quaternion.Z,
        "W" => quaternion.W,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    public static float GetAxis(this object? value, string axis) => value switch
    {
        Vector2 vector2 => vector2.GetAxis(axis),
        Vector3 vector3 => vector3.GetAxis(axis),
        Vector4 vector4 => vector4.GetAxis(axis),
        Quaternion quaternion => quaternion.GetAxis(axis),
        _ => 0f,
    };
}
