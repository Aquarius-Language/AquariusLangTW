export function registerAndroidInput(host, bridge) {
  const module = host.library('AndroidInput');
  host.modules.set('Android輸入', module);
  host.bind(module, 'AndroidInput', 'IsSupported', () => true);
  host.bind(module, 'AndroidInput', 'Poll', () => {
    host.androidInputObserved = true;
    if (!host.processing.running) host.input = JSON.parse(bridge.poll());
    const input = host.input;
    return host.record({
      orientation: input.orientation, keyboardVisible: input.keyboardVisible,
      insets: host.record(input.insets),
      touches: input.events.filter(event => event.type === 'touch').map(event => host.record({
        ...event, history: event.history.map(sample => host.record(sample)),
      })),
      backRequested: input.events.some(event => event.type === 'back'),
    });
  });
  host.bind(module, 'AndroidInput', 'ShowKeyboard', visible => {
    if (typeof visible !== 'boolean') throw new Error('Expected boolean');
    bridge.keyboard(visible);
  });
  host.bind(module, 'AndroidInput', 'Vibrate', duration => {
    if (!Number.isInteger(duration) || duration < 1 || duration > 10000) throw new Error('Vibration must be 1..10000 milliseconds');
    bridge.vibrate(duration);
  });
  host.bind(module, 'AndroidInput', 'ResolveBack', decision => {
    if (!['Accept', 'Cancel', 'Defer'].includes(decision)) throw new Error('Expected Accept, Cancel or Defer');
    bridge.resolveBack(decision);
  });
}
