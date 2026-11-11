// The JavaScript consumer of the geometry test library (tests/exports/geometry) on the web: the same calls as
// consumer.c through loadLibrary() of the web glue, printing the lines of geometry.expected but the c-abi ones (no
// JavaScript value becomes invalid UTF-8 or a NULL array). scripts/web-library-run.mjs serves the library's web output
// with this file beside it and runs main() in Chromium.
import { loadLibrary } from './glue/index.mjs';

const hundredths = (x) => Math.trunc(x * 100);

export async function main(print) {
    const geometry = await loadLibrary({
        library: new URL('geometry.wasm', import.meta.url), base: new URL('runtime/', import.meta.url), output: (stream, line) => print(line),
    });
    const errorLine = (what, call) => {
        try { call(); print(`native error ${what}: (none)`); } catch (error) { print(`native error ${what}: ${error.message}`); }
    };
    print(`native abi ${geometry.abiVersion}`);
    print(`area ${hundredths(geometry.area(3, 4.5))}`);
    const a = { x: 0, y: 0 }, b = { x: 3, y: 4 };
    print(`distance ${hundredths(geometry.distance(a, b))}`);
    const m = geometry.midpoint(a, b);
    print(`midpoint ${hundredths(m.x)} ${hundredths(m.y)}`);
    print(`scale ${geometry.scale([1, 2.5, -4], 2).map(hundredths).join(' ')}`);
    print(`sum ${geometry.sum([1, 2, 3, 4])}`);
    print(`halve ${hundredths(geometry.halve(5))}`);
    print(`isEven ${Number(geometry.isEven(4))} ${Number(geometry.isEven(7))}`);
    print(`evens ${geometry.evens(5).map(Number).join(' ')}`);
    print(`squares ${geometry.squares(3).join(' ')}`);
    print(`widths ${hundredths(geometry.widths([0.5, 1.25]))}`);
    print(`total ${geometry.total([5000000000n, 1n])}`);
    print(`countTrue ${geometry.countTrue([true, false, true])}`);
    print(`greet ${geometry.greet('世界')}`);
    print(`big ${geometry.big(3000000000n)}`);
    const person = geometry.makePerson('Ada', 36);
    print(`person ${person.name} ${person.age}`);
    print(`describe ${geometry.describe(person)}`);
    geometry.log('hello');
    print(`next ${geometry.next()}`);
    print(`next ${geometry.next()}`);
    errorLine('divide', () => geometry.divide(1, 0));
    errorLine('null string', () => geometry.greet(null));
    geometry.area(1, 1);
    print('native error cleared'); // a call that returns has cleared the error: a failing one throws
    await geometry.shutdown(); // resolves once a fresh instance is ready
    print(`native next after shutdown ${geometry.next()}`);
    await geometry.shutdown();
}
