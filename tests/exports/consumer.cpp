// The C++ consumer of the geometry test library (tests/exports/geometry): the same calls as consumer.c through the C++
// binding (geometry.hpp), printing the same lines of geometry.expected. Failures arrive as geometry::error; the two
// cases C++ types cannot express (a NULL string or array) go through the C functions.
#include "geometry.hpp"
#include <cstdio>
#include <string>
#include <vector>

static int hundredths(double x) { return static_cast<int>(x * 100); }

template <class F> static void error_line(const char *what, F call) {
    try {
        call();
        std::printf("native error %s: (none)\n", what);
    } catch (const geometry::error &error) {
        std::printf("native error %s: %s\n", what, error.what());
    }
}

int main() {
    std::printf("native abi %u\n", static_cast<unsigned>(geo_abi_version()));
    std::printf("area %d\n", hundredths(geometry::area(3, 4.5)));
    geometry::point a{0, 0}, b{3, 4};
    std::printf("distance %d\n", hundredths(geometry::distance(a, b)));
    auto m = geometry::midpoint(a, b);
    std::printf("midpoint %d %d\n", hundredths(m.x), hundredths(m.y));
    std::printf("scale");
    for (double value : geometry::scale({1, 2.5, -4}, 2)) std::printf(" %d", hundredths(value));
    std::printf("\n");
    std::printf("sum %d\n", geometry::sum({1, 2, 3, 4}));
    std::printf("halve %d\n", hundredths(geometry::halve(5.0f)));
    std::printf("isEven %d %d\n", geometry::is_even(4), geometry::is_even(7));
    std::printf("evens");
    for (bool value : geometry::evens(5)) std::printf(" %d", value);
    std::printf("\n");
    std::printf("squares");
    for (int64_t value : geometry::squares(3)) std::printf(" %lld", static_cast<long long>(value));
    std::printf("\n");
    std::printf("widths %d\n", hundredths(geometry::widths({0.5f, 1.25f})));
    std::printf("total %lld\n", static_cast<long long>(geometry::total({5000000000LL, 1})));
    std::printf("countTrue %d\n", geometry::count_true({true, false, true}));
    std::printf("greet %s\n", geometry::greet("世界").c_str());
    std::printf("big %lld\n", static_cast<long long>(geometry::big(3000000000LL)));
    auto person = geometry::make_person("Ada", 36);
    std::printf("person %s %d\n", person.name.c_str(), person.age);
    std::printf("describe %s\n", geometry::describe(person).c_str());
    std::fflush(stdout); // the library writes its own output
    geometry::log("hello");
    std::printf("next %d\n", geometry::next());
    std::printf("next %d\n", geometry::next());

    error_line("divide", [] { geometry::divide(1, 0); });
    try {
        geometry::greet("\xff");
    } catch (const geometry::error &error) {
        std::printf("c-abi error utf8: %s\n", error.what());
    }
    geo_greet(nullptr);
    std::printf("native error null string: %s\n", geo_last_error());
    geo_sum(nullptr, 3);
    std::printf("c-abi error null array: %s\n", geo_last_error());
    geometry::area(1, 1);
    std::printf("native error %s\n", geo_last_error() ? "kept" : "cleared");
    geometry::shutdown();
    std::printf("native next after shutdown %d\n", geometry::next());
    geometry::shutdown();
    return 0;
}
