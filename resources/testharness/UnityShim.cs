// Minimal managed reimplementation of the UnityEngine math types used by the
// pure-logic Speed scripts, so their EditMode tests can run outside Unity.
using System;

namespace UnityEngine
{
    public static class Mathf
    {
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public const float Rad2Deg = (float)(180.0 / Math.PI);

        public static float Abs(float v) => Math.Abs(v);
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Min(float a, float b) => Math.Min(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static int Min(int a, int b) => Math.Min(a, b);
        public static int Abs(int v) => Math.Abs(v);
        public static float Sign(float v) => v >= 0f ? 1f : -1f;
        public static float Sin(float v) => (float)Math.Sin(v);
        public static float Cos(float v) => (float)Math.Cos(v);
        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static float Exp(float v) => (float)Math.Exp(v);
        public static float Log(float v) => (float)Math.Log(v);
        public static float Log(float v, float b) => (float)Math.Log(v, b);
        public static float Pow(float a, float b) => (float)Math.Pow(a, b);
        public static float Tan(float v) => (float)Math.Tan(v);
        public static float Acos(float v) => (float)Math.Acos(v);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static int RoundToInt(float v) => (int)Math.Round(v, MidpointRounding.ToEven);
        public static int CeilToInt(float v) => (int)Math.Ceiling(v);
        public static int FloorToInt(float v) => (int)Math.Floor(v);

        public static float Clamp(float v, float min, float max)
            => v < min ? min : (v > max ? max : v);

        public static int Clamp(int v, int min, int max)
            => v < min ? min : (v > max ? max : v);

        public static float Clamp01(float v) => Clamp(v, 0f, 1f);

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);

        public static float InverseLerp(float a, float b, float value)
            => a != b ? Clamp01((value - a) / (b - a)) : 0f;

        public static float Repeat(float t, float length)
            => Clamp(t - (float)Math.Floor(t / length) * length, 0f, length);

        public static float DeltaAngle(float current, float target)
        {
            float delta = Repeat(target - current, 360f);
            if (delta > 180f)
            {
                delta -= 360f;
            }

            return delta;
        }

        public static bool Approximately(float a, float b)
            => Math.Abs(b - a) < Math.Max(1e-06f * Math.Max(Math.Abs(a), Math.Abs(b)), 1.175494e-38f * 8f);
    }

    public struct Vector3 : IEquatable<Vector3>
    {
        public float x;
        public float y;
        public float z;

        public Vector3(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }

        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 one => new Vector3(1f, 1f, 1f);
        public static Vector3 right => new Vector3(1f, 0f, 0f);
        public static Vector3 left => new Vector3(-1f, 0f, 0f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public static Vector3 back => new Vector3(0f, 0f, -1f);

        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);

        public Vector3 normalized
        {
            get
            {
                float m = magnitude;
                return m > 1e-05f ? this / m : zero;
            }
        }

        public static Vector3 operator +(Vector3 a, Vector3 b)
            => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);

        public static Vector3 operator -(Vector3 a, Vector3 b)
            => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);

        public static Vector3 operator -(Vector3 a)
            => new Vector3(-a.x, -a.y, -a.z);

        public static Vector3 operator *(Vector3 a, float d)
            => new Vector3(a.x * d, a.y * d, a.z * d);

        public static Vector3 operator *(float d, Vector3 a) => a * d;

        public static Vector3 operator /(Vector3 a, float d)
            => new Vector3(a.x / d, a.y / d, a.z / d);

        public static float Dot(Vector3 a, Vector3 b)
            => a.x * b.x + a.y * b.y + a.z * b.z;

        public static Vector3 Cross(Vector3 a, Vector3 b)
            => new Vector3(
                a.y * b.z - a.z * b.y,
                a.z * b.x - a.x * b.z,
                a.x * b.y - a.y * b.x);

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Vector3(
                a.x + (b.x - a.x) * t,
                a.y + (b.y - a.y) * t,
                a.z + (b.z - a.z) * t);
        }

        public static Vector3 Slerp(Vector3 a, Vector3 b, float t)
        {
            t = Mathf.Clamp01(t);
            float magnitudeA = a.magnitude;
            float magnitudeB = b.magnitude;
            if (magnitudeA < 1e-06f || magnitudeB < 1e-06f)
            {
                return Lerp(a, b, t);
            }

            Vector3 directionA = a / magnitudeA;
            Vector3 directionB = b / magnitudeB;
            float dot = Mathf.Clamp(Dot(directionA, directionB), -1f, 1f);
            float angle = Mathf.Acos(dot);
            float targetMagnitude = Mathf.Lerp(magnitudeA, magnitudeB, t);
            if (angle < 1e-06f)
            {
                return Lerp(directionA, directionB, t).normalized * targetMagnitude;
            }

            if (angle > (float)Math.PI - 1e-06f)
            {
                // Opposite directions: pick any perpendicular rotation plane.
                Vector3 axis = Cross(directionA, up);
                if (axis.sqrMagnitude < 1e-06f)
                {
                    axis = Cross(directionA, right);
                }

                axis = axis.normalized;
                Quaternion rotation = Quaternion.AngleAxis(180f * t, axis);
                return rotation * directionA * targetMagnitude;
            }

            float sinAngle = Mathf.Sin(angle);
            float weightA = Mathf.Sin((1f - t) * angle) / sinAngle;
            float weightB = Mathf.Sin(t * angle) / sinAngle;
            Vector3 direction = directionA * weightA + directionB * weightB;
            return direction * targetMagnitude;
        }

        public static Vector3 ClampMagnitude(Vector3 v, float maxLength)
        {
            float sq = v.sqrMagnitude;
            if (sq > maxLength * maxLength)
            {
                float m = (float)Math.Sqrt(sq);
                return v / m * maxLength;
            }

            return v;
        }

        public bool Equals(Vector3 other)
            => x == other.x && y == other.y && z == other.z;

        public override bool Equals(object obj)
            => obj is Vector3 other && Equals(other);

        public override int GetHashCode()
            => x.GetHashCode() ^ (y.GetHashCode() << 2) ^ (z.GetHashCode() >> 2);

        public override string ToString() => $"({x:F2}, {y:F2}, {z:F2})";
    }

    public struct Quaternion
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public Quaternion(float x, float y, float z, float w)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            this.w = w;
        }

        public static Quaternion identity => new Quaternion(0f, 0f, 0f, 1f);

        public Quaternion normalized
        {
            get
            {
                float m = (float)Math.Sqrt(x * x + y * y + z * z + w * w);
                return m > 1e-06f
                    ? new Quaternion(x / m, y / m, z / m, w / m)
                    : identity;
            }
        }

        public static Quaternion operator *(Quaternion a, Quaternion b)
            => new Quaternion(
                a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
                a.w * b.y + a.y * b.w + a.z * b.x - a.x * b.z,
                a.w * b.z + a.z * b.w + a.x * b.y - a.y * b.x,
                a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        public static Vector3 operator *(Quaternion rotation, Vector3 point)
        {
            float x2 = rotation.x * 2f;
            float y2 = rotation.y * 2f;
            float z2 = rotation.z * 2f;
            float xx = rotation.x * x2;
            float yy = rotation.y * y2;
            float zz = rotation.z * z2;
            float xy = rotation.x * y2;
            float xz = rotation.x * z2;
            float yz = rotation.y * z2;
            float wx = rotation.w * x2;
            float wy = rotation.w * y2;
            float wz = rotation.w * z2;
            return new Vector3(
                (1f - (yy + zz)) * point.x + (xy - wz) * point.y + (xz + wy) * point.z,
                (xy + wz) * point.x + (1f - (xx + zz)) * point.y + (yz - wx) * point.z,
                (xz - wy) * point.x + (yz + wx) * point.y + (1f - (xx + yy)) * point.z);
        }

        public static Quaternion Inverse(Quaternion q)
        {
            float normSquared = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (normSquared < 1e-12f)
            {
                return identity;
            }

            float inverse = 1f / normSquared;
            return new Quaternion(
                -q.x * inverse,
                -q.y * inverse,
                -q.z * inverse,
                q.w * inverse);
        }

        public static Quaternion AngleAxis(float angleDegrees, Vector3 axis)
        {
            Vector3 normalizedAxis = axis.normalized;
            float halfAngle = angleDegrees * Mathf.Deg2Rad * 0.5f;
            float sin = Mathf.Sin(halfAngle);
            return new Quaternion(
                normalizedAxis.x * sin,
                normalizedAxis.y * sin,
                normalizedAxis.z * sin,
                Mathf.Cos(halfAngle));
        }

        // Unity applies intrinsic rotations in Z, then X, then Y order.
        public static Quaternion Euler(float xDegrees, float yDegrees, float zDegrees)
        {
            Quaternion qx = AngleAxis(xDegrees, Vector3.right);
            Quaternion qy = AngleAxis(yDegrees, Vector3.up);
            Quaternion qz = AngleAxis(zDegrees, Vector3.forward);
            return qy * qx * qz;
        }

        public override string ToString() => $"({x:F3}, {y:F3}, {z:F3}, {w:F3})";
    }
}
