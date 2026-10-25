// The unified web glue: every Web API domain the host core and libraries may import, and the application starter.
// Each domain registers itself with core/glue.mjs when imported here.
import './core/runtime.mjs';

export { startApplication } from './core/runtime.mjs';
