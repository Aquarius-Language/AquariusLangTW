// The Swift consumer of the geometry test library (tests/exports/geometry) on macOS and the iOS simulator: the same calls
// as consumer.c through the Swift binding (the output's swift/Geometry.swift and its C module), printing the lines of
// geometry.expected. Failures arrive as thrown Geometry.Error; the cases Swift's types cannot express (invalid UTF-8, a
// NULL string or array) go through the C module directly, as consumer.rs does through geometry::ffi.
// scripts/test-exports.ps1 compiles it as main.swift with the binding's file.
import Foundation
import Geometry

func hundredths(_ x: Double) -> Int32 { Int32(x * 100) }

func errorLine<T>(_ what: String, _ call: () throws -> T) {
    do {
        _ = try call()
        print("native error \(what): (none)")
    } catch let error as Geometry.Error {
        print("native error \(what): \(error.message)")
    } catch {
        print("native error \(what): \(error)")
    }
}

func lastError() -> String {
    guard let error = geo_last_error() else { return "(none)" }
    return String(cString: error)
}

do {
    print("native abi \(Geometry.abiVersion)")
    print("area \(hundredths(try Geometry.area(3, 4.5)))")
    let a = Geometry.Point(x: 0, y: 0), b = Geometry.Point(x: 3, y: 4)
    print("distance \(hundredths(try Geometry.distance(a: a, b: b)))")
    let m = try Geometry.midpoint(a: a, b: b)
    print("midpoint \(hundredths(m.x)) \(hundredths(m.y))")
    print("scale \(try Geometry.scale(values: [1, 2.5, -4], factor: 2).map { String(hundredths($0)) }.joined(separator: " "))")
    print("sum \(try Geometry.sum(values: [1, 2, 3, 4]))")
    print("halve \(hundredths(Double(try Geometry.halve(x: 5))))")
    print("isEven \(try Geometry.isEven(n: 4) ? 1 : 0) \(try Geometry.isEven(n: 7) ? 1 : 0)")
    print("evens \(try Geometry.evens(n: 5).map { $0 ? "1" : "0" }.joined(separator: " "))")
    print("squares \(try Geometry.squares(n: 3).map { String($0) }.joined(separator: " "))")
    print("widths \(hundredths(Double(try Geometry.widths(values: [0.5, 1.25]))))")
    print("total \(try Geometry.total(values: [5000000000, 1]))")
    print("countTrue \(try Geometry.countTrue(values: [true, false, true]))")
    print("greet \(try Geometry.greet("世界"))")
    print("big \(try Geometry.big(n: 3000000000))")
    let person = try Geometry.makePerson(name: "Ada", age: 36)
    print("person \(person.name) \(person.age)")
    print("describe \(try Geometry.describe(p: person))")
    fflush(stdout) // the library writes its own output
    try Geometry.log(message: "hello")
    print("next \(try Geometry.next())")
    print("next \(try Geometry.next())")

    errorLine("divide") { try Geometry.divide(a: 1, b: 0) }
    // Swift strings are valid Unicode: invalid UTF-8 and NULL can only come through the C module.
    let invalid: [CChar] = [CChar(bitPattern: 0xff), 0]
    _ = geo_greet(invalid)
    print("c-abi error utf8: \(lastError())")
    _ = geo_greet(nil)
    print("native error null string: \(lastError())")
    _ = geo_sum(nil, 3)
    print("c-abi error null array: \(lastError())")
    _ = try Geometry.area(1, 1)
    print("native error \(geo_last_error() == nil ? "cleared" : "kept")")
    Geometry.shutdown()
    print("native next after shutdown \(try Geometry.next())")
    Geometry.shutdown()
} catch {
    print("unexpected failure: \(error)")
    exit(1)
}
