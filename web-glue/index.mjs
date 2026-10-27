// The unified web glue: every Web API domain the host core and libraries may import, and the application starter.
// Each domain registers itself with core/glue.mjs when imported here; one line per domain folder.
import './core/runtime.mjs';
import './window/window.mjs';
import './audio/audio.mjs';
import './camera/camera.mjs';
import './webgpu/webgpu.mjs';
import './webgl/webgl.mjs';
import './network/network.mjs';
import './webrtc/webrtc.mjs';
import './storage/storage.mjs';
import './files/files.mjs';
import './clipboard/clipboard.mjs';
import './print/print.mjs';
import './accessibility/accessibility.mjs';
import './canvas/canvas.mjs';
import './thread/thread.mjs';

export { startApplication } from './core/runtime.mjs';
