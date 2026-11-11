// The Kotlin consumer of the geometry test library (tests/exports/geometry) on Android: the same calls as consumer.c
// through the Java binding, as Kotlin code calls it, printing the lines of geometry.expected but the c-abi ones.
// scripts/test-exports.ps1 compiles it with the pinned Kotlin compiler and runs it on the device or emulator.
import org.aquariuslang.tests.geometry.Geometry

private fun hundredths(x: Double) = (x * 100).toInt()

private fun errorLine(what: String, call: () -> Unit) {
    try {
        call()
        println("native error $what: (none)")
    } catch (error: Geometry.AquariusException) {
        println("native error $what: ${error.message}")
    }
}

fun main() {
    println("native abi ${Geometry.abiVersion()}")
    println("area ${hundredths(Geometry.area(3.0, 4.5))}")
    val a = Geometry.Point(0.0, 0.0)
    val b = Geometry.Point(3.0, 4.0)
    println("distance ${hundredths(Geometry.distance(a, b))}")
    val m = Geometry.midpoint(a, b)
    println("midpoint ${hundredths(m.x)} ${hundredths(m.y)}")
    println("scale ${Geometry.scale(doubleArrayOf(1.0, 2.5, -4.0), 2.0).joinToString(" ") { hundredths(it).toString() }}")
    println("sum ${Geometry.sum(intArrayOf(1, 2, 3, 4))}")
    println("halve ${hundredths(Geometry.halve(5f).toDouble())}")
    println("isEven ${if (Geometry.isEven(4)) 1 else 0} ${if (Geometry.isEven(7)) 1 else 0}")
    println("evens ${Geometry.evens(5).joinToString(" ") { if (it) "1" else "0" }}")
    println("squares ${Geometry.squares(3).joinToString(" ")}")
    println("widths ${hundredths(Geometry.widths(floatArrayOf(0.5f, 1.25f)).toDouble())}")
    println("total ${Geometry.total(longArrayOf(5000000000L, 1L))}")
    println("countTrue ${Geometry.countTrue(booleanArrayOf(true, false, true))}")
    println("greet ${Geometry.greet("世界")}")
    println("big ${Geometry.big(3000000000L)}")
    val person = Geometry.makePerson("Ada", 36)
    println("person ${person.name} ${person.age}")
    println("describe ${Geometry.describe(person)}")
    System.out.flush() // the library writes its own output
    Geometry.log("hello")
    println("next ${Geometry.next()}")
    println("next ${Geometry.next()}")
    errorLine("divide") { Geometry.divide(1, 0) }
    errorLine("null string") { Geometry.greet(null) }
    Geometry.area(1.0, 1.0)
    println("native error cleared") // a call that returns has cleared the error: a failing one throws
    Geometry.shutdown()
    println("native next after shutdown ${Geometry.next()}")
    Geometry.shutdown()
}
