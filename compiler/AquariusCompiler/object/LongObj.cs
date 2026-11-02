namespace AquariusLang.Object {

    /// <summary>A 長整數 / long constant: the guest stores its two's complement bits in Value.number (tag LONG, 17).</summary>
    public sealed class LongObj(long value) : IObject {
        public const string LONG_OBJ = "LONG";
        public long Value => value;
        public string Type() => LONG_OBJ;
        public string Inspect() => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
