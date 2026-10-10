using System.Numerics;
using AquariusLang.Object;

namespace AquariusLang.Desktop.Graphics;

internal sealed partial class GraphicsRuntime {
    private void RegisterMath() {
        var env = Module("GLM");
        env.Create("PI", new DoubleObj(Math.PI));
        Bind(env, "Radians", 1, a => new DoubleObj(Number(a[0]) * Math.PI / 180));
        Bind(env, "Sin", 1, a => new DoubleObj(Math.Sin(Number(a[0]))));
        Bind(env, "Cos", 1, a => new DoubleObj(Math.Cos(Number(a[0]))));
        Bind(env, "Sqrt", 1, a => { double n = Number(a[0]); if (n < 0) throw new ArgumentException("Sqrt requires a nonnegative value."); return new DoubleObj(Math.Sqrt(n)); });
        Bind(env, "Normalize", 1, a => Vector(Normalized(Vec(a[0]))));
        Bind(env, "Dot", 2, a => new DoubleObj(Vector3.Dot(Vec(a[0]), Vec(a[1]))));
        Bind(env, "Cross", 2, a => Vector(Vector3.Cross(Vec(a[0]), Vec(a[1]))));
        Bind(env, "Identity", 0, _ => Matrix(Matrix4x4.Identity));
        Bind(env, "Translate", 1, a => Matrix(Matrix4x4.CreateTranslation(Vec(a[0]))));
        Bind(env, "Scale", 1, a => Matrix(Matrix4x4.CreateScale(Vec(a[0]))));
        Bind(env, "Rotate", 2, a => Matrix(Matrix4x4.CreateFromAxisAngle(Normalized(Vec(a[1])), (float)Number(a[0]))));
        // System.Numerics uses row vectors. Reverse composition and serialize rows
        // as GL columns, yielding the expected GLSL column-vector convention.
        Bind(env, "Multiply", 2, a => Matrix(Mat(a[1]) * Mat(a[0])));
        Bind(env, "LookAt", 3, a => {
            var eye = Vec(a[0]); var target = Vec(a[1]); var up = Normalized(Vec(a[2]));
            var forward = Normalized(target - eye);
            if (Vector3.Cross(forward, up).LengthSquared() < 1e-12f) throw new ArgumentException("LookAt up vector is parallel to view direction.");
            return Matrix(Matrix4x4.CreateLookAt(eye, target, up));
        });
        Bind(env, "Perspective", 4, a => {
            float fov = (float)Number(a[0]), aspect = (float)Number(a[1]), near = (float)Number(a[2]), far = (float)Number(a[3]);
            if (fov <= 0 || fov >= Math.PI || aspect <= 0 || near <= 0 || far <= near)
                throw new ArgumentException("Perspective requires 0 < fov < PI, aspect > 0 and 0 < near < far.");
            float f = 1 / MathF.Tan(fov / 2);
            // OpenGL NDC depth is [-1,1], unlike System.Numerics' default [0,1].
            return Matrix(new Matrix4x4(f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0,
                (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0));
        });
        Bind(env, "Transpose", 1, a => Matrix(Matrix4x4.Transpose(Mat(a[0]))));
        Bind(env, "Inverse", 1, a => {
            if (!Matrix4x4.Invert(Mat(a[0]), out var inverse)) throw new ArgumentException("Matrix is singular.");
            return Matrix(inverse);
        });
        Bind(env, "TransformPoint", 2, a => Vector(Vector3.Transform(Vec(a[1]), Mat(a[0]))));
    }
    private static Vector3 Vec(IObject value) {
        var a = Array(value).Elements;
        if (a.Length != 3) throw new ArgumentException("Expected a 3-element vector.");
        return new Vector3((float)Number(a[0]), (float)Number(a[1]), (float)Number(a[2]));
    }
    private static Vector3 Normalized(Vector3 vector) {
        if (vector.LengthSquared() < 1e-12f) throw new ArgumentException("Cannot normalize a zero vector.");
        return Vector3.Normalize(vector);
    }
    private static IObject Vector(Vector3 v) => Numbers(v.X, v.Y, v.Z);
    private static ArrayObj Matrix(Matrix4x4 m) => Numbers(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
    private static Matrix4x4 Mat(IObject value) {
        var a = Array(value).Elements;
        if (a.Length != 16) throw new ArgumentException("Expected a 16-element column-major matrix.");
        float[] f = a.Select(x => (float)Number(x)).ToArray();
        return new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
    }
}
