// Geometry.swift: the Swift binding of the geometry native library, written by aqua <version>
// from main.aqua (docs/native-libraries.md). Do not edit; rebuild the library instead.
import Foundation
import Geometry

/// The geometry library. Calls are serialized by the library; the first one runs its top-level code.
/// A call that fails throws ``Geometry/Error`` with the library's reason.
public enum Geometry {
    /// A call the library could not complete; `message` is its reason.
    public struct Error: Swift.Error, Equatable, CustomStringConvertible {
        public let message: String
        public init(message: String) { self.message = message }
        public var description: String { message }
    }

    /// A point in the plane.
    public struct Point: Equatable {
        public var x: Double
        public var y: Double

        public init(x: Double, y: Double) {
            self.x = x
            self.y = y
        }

        fileprivate func toC(_ copies: inout CopiedArguments) throws -> geo_point {
            geo_point(x: x, y: y)
        }

        fileprivate init(taking value: geo_point) {
            self.init(x: value.x, y: value.y)
        }
    }

    /// A person with a name.
    public struct Person: Equatable {
        public var name: String
        public var age: Int32

        public init(name: String, age: Int32) {
            self.name = name
            self.age = age
        }

        fileprivate func toC(_ copies: inout CopiedArguments) throws -> geo_person {
            geo_person(name: try copies.string(name), age: age)
        }

        fileprivate init(taking value: geo_person) {
            self.init(name: takeString(value.name), age: value.age)
        }
    }

    /// The C ABI version the library implements.
    public static var abiVersion: UInt32 { geo_abi_version() }

    /// Starts the library (runs its top-level code once). Optional: the first call starts it.
    public static func initialize() throws {
        if !geo_initialize() {
            try check()
            throw Error(message: "The library did not start.")
        }
    }

    /// Stops the library; a later call starts it again.
    public static func shutdown() { geo_shutdown() }

    /// Area of a rectangle.
    public static func area(_ arg0: Double, _ arg1: Double) throws -> Double {
        let result = geo_area(arg0, arg1)
        try check()
        return result
    }

    /// Distance between two points.
    public static func distance(a: Point, b: Point) throws -> Double {
        var copies = CopiedArguments()
        defer { copies.release() }
        let result = try geo_distance(a.toC(&copies), b.toC(&copies))
        try check()
        return result
    }

    /// The point halfway between two points.
    /// Both are copied in and the result is copied out.
    public static func midpoint(a: Point, b: Point) throws -> Point {
        var copies = CopiedArguments()
        defer { copies.release() }
        let result = try geo_midpoint(a.toC(&copies), b.toC(&copies))
        try check()
        return Point(taking: result)
    }

    /// Every value multiplied by a factor.
    public static func scale(values: [Double], factor: Double) throws -> [Double] {
        let result = geo_scale(values, values.count, factor)
        try check()
        return takeArray(result.data, result.count)
    }

    public static func sum(values: [Int32]) throws -> Int32 {
        let result = geo_sum(values, values.count)
        try check()
        return result
    }

    public static func halve(x: Float) throws -> Float {
        let result = geo_halve(x)
        try check()
        return result
    }

    public static func isEven(n: Int32) throws -> Bool {
        let result = geo_is_even(n)
        try check()
        return result
    }

    public static func evens(n: Int32) throws -> [Bool] {
        let result = geo_evens(n)
        try check()
        return takeArray(result.data, result.count)
    }

    public static func squares(n: Int32) throws -> [Int64] {
        let result = geo_squares(n)
        try check()
        return takeArray(result.data, result.count)
    }

    public static func widths(values: [Float]) throws -> Float {
        let result = geo_widths(values, values.count)
        try check()
        return result
    }

    public static func total(values: [Int64]) throws -> Int64 {
        let result = geo_total(values, values.count)
        try check()
        return result
    }

    public static func countTrue(values: [Bool]) throws -> Int32 {
        let result = geo_count_true(values, values.count)
        try check()
        return result
    }

    /// Greets someone by name.
    public static func greet(_ arg0: String) throws -> String {
        var copies = CopiedArguments()
        defer { copies.release() }
        let result = try geo_greet(copies.string(arg0))
        try check()
        return takeString(result)
    }

    public static func big(n: Int64) throws -> Int64 {
        let result = geo_big(n)
        try check()
        return result
    }

    public static func makePerson(name: String, age: Int32) throws -> Person {
        var copies = CopiedArguments()
        defer { copies.release() }
        let result = try geo_make_person(copies.string(name), age)
        try check()
        return Person(taking: result)
    }

    public static func describe(p: Person) throws -> String {
        var copies = CopiedArguments()
        defer { copies.release() }
        let result = try geo_describe(p.toC(&copies))
        try check()
        return takeString(result)
    }

    public static func log(message: String) throws {
        var copies = CopiedArguments()
        defer { copies.release() }
        try geo_log(copies.string(message))
        try check()
    }

    public static func divide(a: Int32, b: Int32) throws -> Int32 {
        let result = geo_divide(a, b)
        try check()
        return result
    }

    public static func next() throws -> Int32 {
        let result = geo_next()
        try check()
        return result
    }

    private static func check() throws {
        if let error = geo_last_error() { throw Error(message: String(cString: error)) }
    }

    /// C copies of string arguments, alive until the call is over (the caller releases them).
    fileprivate struct CopiedArguments {
        private var memory: [UnsafeMutablePointer<CChar>] = []

        mutating func string(_ text: String) throws -> UnsafeMutablePointer<CChar> {
            guard !text.utf8.contains(0) else { throw Error(message: "A string argument contains a NUL character.") }
            let copy = strdup(text)!
            memory.append(copy)
            return copy
        }

        func release() { memory.forEach { free($0) } }
    }
}

/// A string the library returned, freed.
private func takeString(_ text: UnsafeMutablePointer<CChar>?) -> String {
    guard let text else { return "" }
    defer { geo_free(text) }
    return String(cString: text)
}

/// An array the library returned, freed.
private func takeArray<T>(_ data: UnsafeMutablePointer<T>?, _ count: Int) -> [T] {
    guard let data else { return [] }
    defer { geo_free(data) }
    return Array(UnsafeBufferPointer(start: data, count: count))
}
