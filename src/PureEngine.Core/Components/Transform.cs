using System.Numerics;

namespace PureEngine.Core;

public sealed class Transform
{
    public Vector3 LocalPosition { get; set; } = Vector3.Zero;
    public Quaternion LocalRotation { get; set; } = Quaternion.Identity;
    public Vector3 LocalScale { get; set; } = Vector3.One;

    public Matrix4x4 LocalMatrix => Matrix4x4.CreateScale(LocalScale)
                                    * Matrix4x4.CreateFromQuaternion(LocalRotation)
                                    * Matrix4x4.CreateTranslation(LocalPosition);
}
