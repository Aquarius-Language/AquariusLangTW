export function frameDelay(fps, start, now) {
  return Math.max(0, 1000 / fps - Math.max(0, now - start));
}
