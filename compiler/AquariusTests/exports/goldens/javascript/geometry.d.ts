// geometry.d.ts: the types of the geometry library on the web, written by aqua <version>
// from main.aqua (docs/native-libraries.md). Do not edit; rebuild the library instead. Load it with the glue:
//   import { loadLibrary } from './glue/index.mjs';
//   const geometry = (await loadLibrary({ library: 'geometry.wasm', base: 'runtime/' })) as Geometry;

/**
 * A point in the plane.
 */
export interface Point {
    x: number;
    y: number;
}

/**
 * A person with a name.
 */
export interface Person {
    name: string;
    age: number;
}

/** The geometry library: a failed call throws an Error with the library's reason. */
export interface Geometry {
    /** Starts the library (runs its top-level code once). Optional: the first call starts it. */
    initialize(): void;
    /** Stops the library and resolves once a fresh instance is linked; the next call starts it again. */
    shutdown(): Promise<void>;
    /** The C ABI version the library implements. */
    readonly abiVersion: number;
    /**
     * Area of a rectangle.
     */
    area(arg0: number, arg1: number): number;
    /**
     * Distance between two points.
     */
    distance(a: Point, b: Point): number;
    /**
     * The point halfway between two points.
     * Both are copied in and the result is copied out.
     */
    midpoint(a: Point, b: Point): Point;
    /**
     * Every value multiplied by a factor.
     */
    scale(values: ArrayLike<number>, factor: number): number[];
    sum(values: ArrayLike<number>): number;
    halve(x: number): number;
    isEven(n: number): boolean;
    evens(n: number): boolean[];
    squares(n: number): bigint[];
    widths(values: ArrayLike<number>): number;
    total(values: ArrayLike<bigint>): bigint;
    countTrue(values: ArrayLike<boolean>): number;
    /**
     * Greets someone by name.
     */
    greet(arg0: string): string;
    big(n: bigint): bigint;
    makePerson(name: string, age: number): Person;
    describe(p: Person): string;
    log(message: string): void;
    divide(a: number, b: number): number;
    next(): number;
}
