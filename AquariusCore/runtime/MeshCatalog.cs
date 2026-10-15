namespace AquariusLang.runtime;
public static partial class LibraryCatalog {
    private static readonly LibraryFunction[] meshFunctions = {
        new("Processing", "loadMesh", "載入網格", 1, 1, "Processing.Shape"),
        new("Processing.Canvas", "loadMesh", "載入網格", 1, 1, "Processing.Shape"),
    };
}
