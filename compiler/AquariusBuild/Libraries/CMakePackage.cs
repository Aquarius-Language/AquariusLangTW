using System.Text;
using AquariusLang.Build.Bindings;
using AquariusLang.Compiler;

namespace AquariusLang.Build.Libraries;

/// <summary>
/// <c>lib/cmake/&lt;name&gt;/&lt;name&gt;-config.cmake</c> of a native library output: imported targets for the kinds that
/// were built (<c>&lt;name&gt;::shared</c>, <c>&lt;name&gt;::static</c>, <c>&lt;name&gt;::object</c>), so a CMake program uses
/// one with <c>find_package(&lt;name&gt; CONFIG)</c> and links it. Static and object targets carry the system libraries
/// they need and the header's <c>&lt;NAME&gt;_STATIC</c>.
/// </summary>
public static class CMakePackage
{
    public static string Config(BindingContext context, RuntimeTarget target, NativeLibraryOutput.LibraryFiles files)
    {
        string name = context.Settings.Name, macro = CBinding.Macro(context);
        string system = string.Join(";", files.SystemLibraries ?? []);
        var text = new StringBuilder();
        text.Append($"# {name}-config.cmake: the native library outputs of {name} for {target.Name}, written by aqua {AquariusVersion.Current}.\n");
        text.Append($"# find_package({name} CONFIG) gives the targets of the kinds built below; link one of them.\n");
        text.Append("get_filename_component(_aqua_root \"${CMAKE_CURRENT_LIST_DIR}/../../..\" ABSOLUTE)\n");
        void Target(string kind, string type, string properties) {
            text.Append($"if(NOT TARGET {name}::{kind})\n    add_library({name}::{kind} {type} IMPORTED)\n");
            text.Append($"    set_target_properties({name}::{kind} PROPERTIES INTERFACE_INCLUDE_DIRECTORIES \"${{_aqua_root}}/include\"\n        {properties})\nendif()\n");
        }
        if (files.Shared.Length > 0) {
            string implib = files.ImportLibrary.Length > 0 ? $" IMPORTED_IMPLIB \"${{_aqua_root}}/{files.ImportLibrary}\"" : "";
            Target("shared", "SHARED", $"IMPORTED_LOCATION \"${{_aqua_root}}/{files.Shared}\"{implib}");
        }
        string staticProperties = $"INTERFACE_COMPILE_DEFINITIONS {macro}_STATIC INTERFACE_LINK_LIBRARIES \"{system}\"";
        if (files.Static.Length > 0) Target("static", "STATIC", $"IMPORTED_LOCATION \"${{_aqua_root}}/{files.Static}\" {staticProperties}");
        if (files.Object.Length > 0) Target("object", "OBJECT", $"IMPORTED_OBJECTS \"${{_aqua_root}}/{files.Object}\" {staticProperties}");
        text.Append("unset(_aqua_root)\n");
        return text.ToString();
    }
}
