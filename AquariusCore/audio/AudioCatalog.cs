using AquariusLang.runtime;
using System.Collections.Generic;

namespace AquariusLang.Audio;

public static class AudioCatalog {
    public static IReadOnlyList<LibraryFunction> Functions { get; } = new LibraryFunction[] {
        new("Audio", "CreateDevice", "建立裝置", 0, 0, "Audio.Device"),
        new("Audio", "CreateOfflineDevice", "建立離線裝置", 0, 0, "Audio.Device"),
        new("Audio.Device", "Start", "啟動", 0, 0), new("Audio.Device", "Suspend", "暫停輸出", 0, 0),
        new("Audio.Device", "CreateTone", "建立音調", 2, 2, "Audio.Sound"),
        new("Audio.Device", "CreateBuffer", "建立樣本", 1, 1, "Audio.Sound"),
        new("Audio.Device", "Load", "載入", 1, 1, "Audio.Sound"),
        new("Audio.Device", "Render", "產生樣本", 1, 1), new("Audio.Device", "Dispose", "釋放", 0, 0),
        new("Audio.Sound", "Play", "播放", 0, 0), new("Audio.Sound", "Pause", "暫停", 0, 0),
        new("Audio.Sound", "Stop", "停止", 0, 0), new("Audio.Sound", "SetVolume", "設定音量", 1, 1),
        new("Audio.Sound", "SetPan", "設定聲像", 1, 1), new("Audio.Sound", "SetPitch", "設定音高", 1, 1),
        new("Audio.Sound", "SetLooping", "設定循環", 1, 1), new("Audio.Sound", "Seek", "跳至", 1, 1),
        new("Audio.Sound", "IsPlaying", "是否播放中", 0, 0), new("Audio.Sound", "GetDuration", "取得時長", 0, 0),
        new("Audio.Sound", "GetPosition", "取得位置", 0, 0), new("Audio.Sound", "Dispose", "釋放", 0, 0)
    };
}
