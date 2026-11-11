// Geometry.java: the Java binding of the geometry native library, written by aqua <version>
// from main.aqua (docs/native-libraries.md). Do not edit; rebuild the library instead.
package geometry;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/** The geometry library. Calls are serialized by the library; the first one runs its top-level code. */
public final class Geometry {
    static {
        System.loadLibrary("geometry");
    }

    private Geometry() {}

    /** A call the library could not complete; the message is its reason. */
    public static final class AquariusException extends RuntimeException {
        public AquariusException(String message) { super(message); }
    }

    /** A reference that is never null. */
    @Retention(RetentionPolicy.CLASS) @Target({ElementType.METHOD, ElementType.PARAMETER, ElementType.FIELD})
    public @interface NonNull {}

    /** A reference that may be null. */
    @Retention(RetentionPolicy.CLASS) @Target({ElementType.METHOD, ElementType.PARAMETER, ElementType.FIELD})
    public @interface Nullable {}

    /**
     * A point in the plane.
     */
    public static final class Point {
        public double x;
        public double y;

        public Point() {}

        public Point(double x, double y) {
            this.x = x;
            this.y = y;
        }
    }

    /**
     * A person with a name.
     */
    public static final class Person {
        public @Nullable String name = "";
        public int age;

        public Person() {}

        public Person(@NonNull String name, int age) {
            this.name = name;
            this.age = age;
        }
    }

    /** Starts the library (runs its top-level code once). Optional: the first call starts it. */
    public static native boolean initialize();

    /** Stops the library; a later call starts it again. */
    public static native void shutdown();

    /** The C ABI version the library implements. */
    public static native int abiVersion();

    /**
     * Area of a rectangle.
     */
    public static native double area(double arg0, double arg1);

    /**
     * Distance between two points.
     */
    public static native double distance(@NonNull Point a, @NonNull Point b);

    /**
     * The point halfway between two points.
     * Both are copied in and the result is copied out.
     */
    public static native @NonNull Point midpoint(@NonNull Point a, @NonNull Point b);

    /**
     * Every value multiplied by a factor.
     */
    public static native @NonNull double[] scale(@NonNull double[] values, double factor);

    public static native int sum(@NonNull int[] values);

    public static native float halve(float x);

    public static native boolean isEven(int n);

    public static native @NonNull boolean[] evens(int n);

    public static native @NonNull long[] squares(int n);

    public static native float widths(@NonNull float[] values);

    public static native long total(@NonNull long[] values);

    public static native int countTrue(@NonNull boolean[] values);

    /**
     * Greets someone by name.
     */
    public static native @NonNull String greet(@NonNull String arg0);

    public static native long big(long n);

    public static native @NonNull Person makePerson(@NonNull String name, int age);

    public static native @NonNull String describe(@NonNull Person p);

    public static native void log(@NonNull String message);

    public static native int divide(int a, int b);

    public static native int next();
}
