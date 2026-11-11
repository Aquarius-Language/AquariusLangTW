// The Java consumer of the geometry test library (tests/exports/geometry): the same calls as consumer.c through the
// Java binding, printing the lines of geometry.expected but the c-abi ones (no Java value becomes invalid UTF-8 or a
// NULL array with a count). scripts/test-exports.ps1 runs it on the desktop JVM (Windows, Linux) and on Android.
import org.aquariuslang.tests.geometry.Geometry;

public final class Consumer {
    private static int hundredths(double x) { return (int) (x * 100); }

    private interface Call { void run(); }

    private static void errorLine(String what, Call call) {
        try {
            call.run();
            System.out.println("native error " + what + ": (none)");
        } catch (Geometry.AquariusException error) {
            System.out.println("native error " + what + ": " + error.getMessage());
        }
    }

    private static String join(int[] values) {
        StringBuilder text = new StringBuilder();
        for (int v : values) text.append(' ').append(v);
        return text.toString();
    }

    public static void main(String[] arguments) {
        System.out.println("native abi " + Geometry.abiVersion());
        System.out.println("area " + hundredths(Geometry.area(3, 4.5)));
        Geometry.Point a = new Geometry.Point(0, 0), b = new Geometry.Point(3, 4);
        System.out.println("distance " + hundredths(Geometry.distance(a, b)));
        Geometry.Point m = Geometry.midpoint(a, b);
        System.out.println("midpoint " + hundredths(m.x) + " " + hundredths(m.y));
        double[] scaled = Geometry.scale(new double[] {1, 2.5, -4}, 2);
        int[] hundreds = new int[scaled.length];
        for (int i = 0; i < scaled.length; i++) hundreds[i] = hundredths(scaled[i]);
        System.out.println("scale" + join(hundreds));
        System.out.println("sum " + Geometry.sum(new int[] {1, 2, 3, 4}));
        System.out.println("halve " + hundredths(Geometry.halve(5f)));
        System.out.println("isEven " + (Geometry.isEven(4) ? 1 : 0) + " " + (Geometry.isEven(7) ? 1 : 0));
        boolean[] evens = Geometry.evens(5);
        int[] bits = new int[evens.length];
        for (int i = 0; i < evens.length; i++) bits[i] = evens[i] ? 1 : 0;
        System.out.println("evens" + join(bits));
        StringBuilder squares = new StringBuilder("squares");
        for (long v : Geometry.squares(3)) squares.append(' ').append(v);
        System.out.println(squares);
        System.out.println("widths " + hundredths(Geometry.widths(new float[] {0.5f, 1.25f})));
        System.out.println("total " + Geometry.total(new long[] {5000000000L, 1}));
        System.out.println("countTrue " + Geometry.countTrue(new boolean[] {true, false, true}));
        System.out.println("greet " + Geometry.greet("世界"));
        System.out.println("big " + Geometry.big(3000000000L));
        Geometry.Person person = Geometry.makePerson("Ada", 36);
        System.out.println("person " + person.name + " " + person.age);
        System.out.println("describe " + Geometry.describe(person));
        System.out.flush(); // the library writes its own output
        Geometry.log("hello");
        System.out.println("next " + Geometry.next());
        System.out.println("next " + Geometry.next());
        errorLine("divide", () -> Geometry.divide(1, 0));
        errorLine("null string", () -> Geometry.greet(null));
        Geometry.area(1, 1);
        System.out.println("native error cleared"); // a call that returns has cleared the error: a failing one throws
        Geometry.shutdown();
        System.out.println("native next after shutdown " + Geometry.next());
        Geometry.shutdown();
    }
}
