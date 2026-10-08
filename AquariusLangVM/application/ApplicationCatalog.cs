using System.Collections.Generic;
using System.Linq;
using AquariusLang.runtime;

namespace AquariusLang.Application;

public static class ApplicationCatalog {
    private static readonly IReadOnlyDictionary<string,string> names = new Dictionary<string,string> {
        ["Capabilities"]="功能支援",["Resolve"]="解析資源",["Stat"]="取得資訊",["ReadBytes"]="讀取位元組",["ReadText"]="讀取文字",["SaveBytes"]="儲存位元組",["SaveText"]="儲存文字",
        ["Enumerate"]="列舉",["CreateDirectory"]="建立目錄",["Delete"]="刪除",["Copy"]="複製",["Move"]="移動",["Temporary"]="暫存",["Open"]="開啟",["Read"]="讀取",["Write"]="寫入",["Seek"]="定位",["Flush"]="寫入緩衝",["Close"]="關閉",
        ["Normalize"]="正規化",["Join"]="組合",["Name"]="檔名",["Extension"]="副檔名",["Parent"]="父目錄",["Relative"]="相對路徑",["Formats"]="格式",["Create"]="建立",["Inspect"]="檢查",["Decode"]="解碼",["Load"]="載入",["Encode"]="編碼",["Save"]="儲存",["Pixels"]="像素",
        ["Json"]="JSON序列化",["ParseJson"]="解析JSON",["Binary"]="二進位序列化",["ParseBinary"]="解析二進位",["Compress"]="壓縮",["Decompress"]="解壓縮",["Archive"]="封裝",["ReadArchive"]="讀取封裝",
        ["SettingsDirectory"]="設定目錄",["DataDirectory"]="資料目錄",["GetPreference"]="取得偏好",["SetPreference"]="設定偏好",["Recent"]="最近資源",["Remember"]="記錄最近資源",
        ["ReadImage"]="讀取圖片",["WriteImage"]="寫入圖片",["WriteText"]="寫入文字",["Available"]="可用",["Measure"]="測量",["Selection"]="選取範圍",["State"]="狀態",["Select"]="選取",["Insert"]="插入",["Backspace"]="退格",["Convert"]="轉換位置",["Composition"]="組字",["CancelComposition"]="取消組字",["Commit"]="提交",["Cut"]="剪下",["Paste"]="貼上",["Shortcut"]="快速鍵",
        ["LaunchFiles"]="啟動檔案",["OpenFiles"]="開啟檔案",["SaveFile"]="儲存檔案",["PickDirectory"]="選擇目錄",["Print"]="列印",["AcquireImage"]="取得圖片",["Accessibility"]="可及性",["RegisterFileAssociation"]="註冊檔案關聯",
        ["CreateCancellation"]="建立取消權杖",["UseCancellation"]="使用取消權杖",["Cancel"]="取消",["IsCancelled"]="已取消",["Dispose"]="釋放",
        ["Attach"]="附加",["Current"]="目前視窗",["FromProcessingImage"]="從Processing圖片",["ToProcessingImage"]="轉為Processing圖片",["SetTitle"]="設定標題",["SetCursor"]="設定游標",["SetCustomCursor"]="設定自訂游標",["CapturePointer"]="擷取指標",["ReleasePointer"]="釋放指標",["ResolveClose"]="決定關閉",["Poll"]="輪詢"
    };
    public static string TraditionalChinese(string name) => names[name];
    public static IReadOnlyList<LibraryFunction> Functions { get; } = Build();
    private static LibraryFunction[] Build() {
        var functions = new List<LibraryFunction>();
        string? Returns(string library, string name) => (library,name) switch {
            ("Files","Open") => "Files.Stream", ("Files","Temporary") => "Files.TemporaryResource",
            ("Images","Create" or "Decode" or "Load") => "Images.Image", ("TextEdit","Create") => "TextEdit.Editor",
            ("Tasks","CreateCancellation") => "Tasks.Cancellation", ("Window","Attach" or "Current") => "Window.Integration",
            ("Window","FromProcessingImage") => "Images.Image", ("Window","ToProcessingImage") => "Processing.Image", _ => null
        };
        void Add(string library, params (string Name, int Min, int Max)[] members) { functions.AddRange(members.Select(m => new LibraryFunction(library, m.Name, TraditionalChinese(m.Name), m.Min, m.Max, Returns(library,m.Name)))); }
        Add("Files", ("Capabilities",0,0),("Resolve",1,1),("Stat",1,1),("ReadBytes",1,1),("ReadText",2,2),("SaveBytes",2,3),("SaveText",3,4),("Enumerate",1,1),("CreateDirectory",1,1),("Delete",1,2),("Copy",2,3),("Move",2,3),("Temporary",1,1),("Open",2,2));
        Add("Files.Stream", ("Read",1,1),("Write",1,1),("Seek",2,2),("Flush",0,0),("Close",0,0));
        Add("Files.TemporaryResource", ("Dispose",0,0));
        Add("Paths", ("Normalize",1,1),("Join",1,1),("Name",1,1),("Extension",1,1),("Parent",1,1),("Relative",2,2));
        Add("Images", ("Formats",0,0),("Inspect",1,1),("Create",3,3),("Decode",2,2),("Load",2,2),("Encode",3,3),("Save",4,4));
        Add("Images.Image", ("Pixels",0,0));
        Add("Serialization", ("Json",1,1),("ParseJson",1,1),("Binary",2,2),("ParseBinary",1,1),("Compress",2,3),("Decompress",2,2),("Archive",1,1),("ReadArchive",1,1));
        Add("Storage", ("SettingsDirectory",0,0),("DataDirectory",0,0),("Read",1,1),("Write",2,2),("Delete",1,1),("GetPreference",1,1),("SetPreference",2,2),("Recent",0,0),("Remember",1,1));
        Add("Clipboard", ("Capabilities",0,0),("Formats",0,0),("ReadText",0,0),("ReadImage",0,0),("WriteText",1,1),("WriteImage",1,2));
        Add("Fonts", ("Enumerate",0,0),("Available",1,1),("Measure",3,3),("Selection",5,5));
        Add("TextEdit", ("Create",1,1),("Convert",4,4));
        Add("TextEdit.Editor", ("State",0,0),("Select",2,2),("Insert",1,1),("Backspace",0,0),("Delete",0,0),("Move",3,3),("Convert",3,3),("Composition",2,3),("CancelComposition",0,0),("Commit",1,1),("Copy",0,0),("Cut",0,0),("Paste",0,0),("Shortcut",2,2));
        Add("Application", ("Capabilities",0,0),("LaunchFiles",0,0),("OpenFiles",1,1),("SaveFile",1,1),("PickDirectory",0,0),("Print",1,1),("AcquireImage",0,0),("Accessibility",1,1),("RegisterFileAssociation",1,1));
        Add("Tasks", ("CreateCancellation",0,0),("UseCancellation",1,1));
        Add("Tasks.Cancellation", ("Cancel",0,0),("IsCancelled",0,0),("Dispose",0,0));
        Add("Window", ("Attach",1,1),("Current",0,0),("FromProcessingImage",1,1),("ToProcessingImage",1,1));
        Add("Window.Integration", ("Capabilities",0,0),("SetTitle",1,1),("SetCursor",1,1),("SetCustomCursor",3,3),("CapturePointer",1,1),("ReleasePointer",1,1),("ResolveClose",1,1),("Poll",0,0));
        return functions.ToArray();
    }
}
