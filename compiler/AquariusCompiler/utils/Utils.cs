using System;
using System.IO;

namespace AquariusLang.utils {

    public class Utils {
        public static T[] PushToArray<T>(T[] target, T item) {
            if (target == null) {
                //TODO: Return null or throw ArgumentNullException;
            }

            T[] result = new T[target.Length + 1];
            target.CopyTo(result, 0);
            result[target.Length] = item;
            return result;
        }

        /// <summary>A float literal's value (<c>1.5f</c>, <c>2.5e3f</c>, <c>2f</c>, <c>1_000.5f</c>), rounded once from its
        /// decimal digits, or null when it is not one or lies outside the float range.</summary>
        public static float? StringToFloat(string literal) {
            string digits = literal.Replace("_", "").TrimEnd('f');
            return float.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value)
                && float.IsFinite(value) ? value : null;
        }

        /// <summary>A double literal's value (<c>1.5</c>, <c>1.5d</c>, <c>1e-9</c>, <c>2d</c>), or null when it is not one or
        /// lies outside the double range.</summary>
        public static double? StringToDouble(string literal) {
            string digits = literal.Replace("_", "").TrimEnd('d');
            return double.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                && double.IsFinite(value) ? value : null;
        }

        /// <summary>A 長整數 / long literal's value (digits and the suffix L): decimal up to 9223372036854775807, or 0x / 0b up
        /// to 64 bits read as the two's complement pattern (<c>0xFFFF_FFFF_FFFF_FFFFL</c> is -1); null when it does not fit.</summary>
        public static long? StringToLong(string literal) {
            string digits = literal.Replace("_", "").TrimEnd('L');
            if (digits.Length > 2 && digits[0] == '0' && digits[1] is 'x' or 'X' or 'b' or 'B') {
                int radix = digits[1] is 'x' or 'X' ? 16 : 2;
                ulong bits = 0;
                foreach (char c in digits.AsSpan(2)) {
                    uint digit = (uint)Convert.ToInt32(c.ToString(), 16);
                    if (bits > (ulong.MaxValue - digit) / (ulong)radix) return null;
                    bits = bits * (ulong)radix + digit;
                }
                return unchecked((long)bits);
            }
            return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value) ? value : null;
        }

        /// <summary>An integer literal's value: decimal up to 2147483647, or <c>0x</c> hexadecimal / <c>0b</c> binary up to 32
        /// bits, read as the two's complement pattern (<c>0xFFFFFFFF</c> is -1); null when it does not fit.</summary>
        public static int? StringToInteger(string literal) {
            string digits = literal.Replace("_", "");
            if (digits.Length > 2 && digits[0] == '0' && digits[1] is 'x' or 'X' or 'b' or 'B') {
                int radix = digits[1] is 'x' or 'X' ? 16 : 2;
                uint bits = 0;
                foreach (char c in digits.AsSpan(2)) {
                    int digit = Convert.ToInt32(c.ToString(), 16);
                    if (bits > (uint.MaxValue - (uint)digit) / (uint)radix) return null;
                    bits = bits * (uint)radix + (uint)digit;
                }
                return unchecked((int)bits);
            }
            return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : null;
        }

        public static bool TryCast<T>(object obj, out T result) {
            if (obj is T) {
                result = (T)obj;
                return true;
            }

            result = default(T);
            return false;
        }

        public static bool IsFullPath(string path) {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) != -1 || !Path.IsPathRooted(path))
                return false;

            string pathRoot = Path.GetPathRoot(path);
            if (pathRoot.Length <= 2 &&
                pathRoot != "/") // Accepts X:\ and \\UNC\PATH, rejects empty string, \ and X:, but accepts / to support Linux
                return false;

            if (pathRoot[0] != '\\' || pathRoot[1] != '\\')
                return true; // Rooted and not a UNC path

            return
                pathRoot.Trim('\\').IndexOf('\\') !=
                -1; // A UNC server name without a share name (e.g "\\NAME" or "\\NAME\") is invalid
        }
    }
}
