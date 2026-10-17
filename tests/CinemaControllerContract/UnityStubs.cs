using N = System.Numerics;

// These substitutes provide camera and raw-input state only. No shader/render claims.
namespace UnityEngine
{
    internal struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float sqrMagnitude => x * x + y * y;
    }
    internal struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 up => new(0, 1, 0);
        public float sqrMagnitude => x * x + y * y + z * z;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static float Distance(Vector3 a, Vector3 b) => MathF.Sqrt((a - b).sqrMagnitude);
    }
    internal struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public static Quaternion identity => new(0, 0, 0, 1);
        public static Quaternion Euler(float x, float y, float z)
        {
            var q = N.Quaternion.CreateFromYawPitchRoll(y * MathF.PI / 180, x * MathF.PI / 180, z * MathF.PI / 180);
            return new(q.X, q.Y, q.Z, q.W);
        }
        public static Quaternion LookRotation(Vector3 direction, Vector3 up)
        {
            float yaw = MathF.Atan2(direction.x, direction.z) * 180 / MathF.PI;
            float pitch = -MathF.Atan2(direction.y, MathF.Sqrt(direction.x * direction.x + direction.z * direction.z)) * 180 / MathF.PI;
            return Euler(pitch, yaw, 0);
        }
    }
    internal sealed class Transform
    {
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 eulerAngles
        {
            get
            {
                var q = rotation;
                float pitch = MathF.Asin(Math.Clamp(2 * (q.w * q.x - q.y * q.z), -1, 1));
                float yaw = MathF.Atan2(2 * (q.w * q.y + q.x * q.z), 1 - 2 * (q.x * q.x + q.y * q.y));
                float roll = MathF.Atan2(2 * (q.w * q.z + q.x * q.y), 1 - 2 * (q.x * q.x + q.z * q.z));
                return new(pitch * 180 / MathF.PI, yaw * 180 / MathF.PI, roll * 180 / MathF.PI);
            }
        }
        public void SetPositionAndRotation(Vector3 position, Quaternion rotation) { this.position = position; this.rotation = rotation; }
    }
    internal sealed class Camera
    {
        public readonly Transform transform = new();
        public float fieldOfView = 60;
        public bool Alive = true;
        public static implicit operator bool(Camera? value) => value?.Alive == true;
    }
    internal static class Mathf { public static float Max(float a, float b) => Math.Max(a, b); }
}

namespace UnityEngine.InputSystem
{
    internal sealed class ButtonState { public bool isPressed, wasPressedThisFrame; }
    internal sealed class Keyboard
    {
        public static Keyboard? current;
        public readonly ButtonState ctrlKey = new(), zKey = new(), yKey = new(), hKey = new(), cKey = new(), spaceKey = new(), tabKey = new(),
            wKey = new(), aKey = new(), sKey = new(), dKey = new(), qKey = new(), eKey = new();
    }
    internal sealed class MouseDelta
    {
        public UnityEngine.Vector2 Value;
        public UnityEngine.Vector2 ReadValue() => Value;
    }
    internal sealed class Mouse { public static Mouse? current; public readonly MouseDelta delta = new(); }
}
