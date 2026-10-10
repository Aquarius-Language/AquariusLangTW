using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using AquariusLang.Object;
using static AquariusLang.Application.ApplicationValues;

namespace AquariusLang.Application;

public sealed partial class ApplicationRuntime {
    private ImageEncodingOptions EncodingOptions(IObject value) {
        var json = DocumentSerialization.Json(JsonValue(value));
        return JsonSerializer.Deserialize<ImageEncodingOptions>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
    private void RegisterData() {
        var images = Module("Images");
        Bind(images, "Formats", 0, 0, _ => new ArrayObj(host.Images.Formats.Select(f => (IObject)new StringObj(f.ToString())).ToArray()));
        Bind(images, "Inspect", 1, 1, a => Record(Await(host.Images.Inspect(Bytes(a[0]), limits, cancellation))));
        Bind(images, "Create", 3, 3, a => Image(new PixelImage(Integer(a[0]), Integer(a[1]), Bytes(a[2]))));
        Bind(images, "Decode", 2, 2, a => Image(Await(host.Images.Decode(Bytes(a[0]), Integer(a[1]), limits, cancellation))));
        Bind(images, "Load", 2, 2, a => Image(Await(host.Images.Decode(Await(files.Read(Owned<FileResource>(a[0]), cancellation)), Integer(a[1]), limits, cancellation))));
        Bind(images, "Encode", 3, 3, a => Binary(Await(host.Images.Encode(GetImage(a[0]), Enum.Parse<ImageFormat>(Text(a[1]), true), EncodingOptions(a[2]), limits, cancellation))));
        Bind(images, "Save", 4, 4, a => new BooleanObj(Await(files.Save(Owned<FileResource>(a[0]), Await(host.Images.Encode(GetImage(a[1]), Enum.Parse<ImageFormat>(Text(a[2]), true), EncodingOptions(a[3]), limits, cancellation)), cancellation: cancellation))));
        var data = Module("Serialization");
        Bind(data, "Json", 1, 1, a => new StringObj(Encoding.UTF8.GetString(DocumentSerialization.Json(JsonValue(a[0])))));
        Bind(data, "ParseJson", 1, 1, a => FromJson(DocumentSerialization.Parse(TextEncoding.Get("utf-8").GetBytes(Text(a[0])))));
        Bind(data, "Binary", 2, 2, a => Binary(DocumentSerialization.Binary(JsonValue(a[0]), checked((uint)WholeNumber(a[1])))));
        Bind(data, "ParseBinary", 1, 1, a => { var (schema, value) = DocumentSerialization.ReadBinary(Bytes(a[0])); var result = Record(new { schema }); result._Environment.Create("value", FromJson(value)); return result; });
        Bind(data, "Compress", 2, 3, a => Binary(DocumentCompression.Compress(Bytes(a[0]), Enum.Parse<CompressionFormat>(Text(a[1]), true), a.Length == 3 ? Enum.Parse<CompressionLevel>(Text(a[2]), true) : CompressionLevel.Optimal)));
        Bind(data, "Decompress", 2, 2, a => Binary(Await(DocumentCompression.Decompress(Bytes(a[0]), Enum.Parse<CompressionFormat>(Text(a[1]), true), cancellation: cancellation))));
        Bind(data, "Archive", 1, 1, a => { if (a[0] is not HashObj hash) throw new ArgumentException("Expected resource hash."); return Binary(DocumentArchive.Create(hash.Pairs.Values.ToDictionary(p => Text(p.Key), p => Bytes(p.Value)))); });
        Bind(data, "ReadArchive", 1, 1, a => new HashObj(Await(DocumentArchive.Read(Bytes(a[0]), cancellation: cancellation)).ToDictionary(p => new StringObj(p.Key).HashKey(), p => new HashPair(new StringObj(p.Key), Binary(p.Value)))));
        var storage = Module("Storage"); var preferences = new Preferences(host.Storage);
        Bind(storage, "SettingsDirectory", 0, 0, _ => Resource(host.Storage.SettingsDirectory)); Bind(storage, "DataDirectory", 0, 0, _ => Resource(host.Storage.DataDirectory));
        Bind(storage, "Read", 1, 1, a => Await(host.Storage.Read(Text(a[0]), cancellation)) is byte[] bytes ? Binary(bytes) : AquariusLang.runtime.RepeatedPrimitives.NULL);
        Action(storage, "Write", 2, 2, a => Await(host.Storage.Write(Text(a[0]), Bytes(a[1]), cancellation))); Action(storage, "Delete", 1, 1, a => Await(host.Storage.Delete(Text(a[0]), cancellation)));
        Bind(storage, "GetPreference", 1, 1, a => Await(preferences.Load(Text(a[0]), cancellation)) is JsonElement value ? FromJson(value) : AquariusLang.runtime.RepeatedPrimitives.NULL);
        Action(storage, "SetPreference", 2, 2, a => Await(preferences.Save(Text(a[0]), JsonValue(a[1]), cancellation)));
        Bind(storage, "Recent", 0, 0, _ => Await(preferences.Load("recent", cancellation)) is JsonElement value ? FromJson(value) : new ArrayObj(Array.Empty<IObject>()));
        Action(storage, "Remember", 1, 1, a => {
            var recent = Await(preferences.Load("recent", cancellation)); var entries = recent is JsonElement value ? JsonSerializer.Deserialize<FileResource[]>(value.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? Array.Empty<FileResource>() : Array.Empty<FileResource>();
            var resources = Preferences.Remember(entries, Owned<FileResource>(a[0])).Select(r => new { provider = r.Provider, id = r.Id, name = r.Name, kind = (int)r.Kind, persistentIdentity = r.PersistentIdentity }).ToArray();
            Await(preferences.Save("recent", resources, cancellation));
        });
    }
}
