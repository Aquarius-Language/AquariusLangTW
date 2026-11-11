// geometry.hpp: the C++ binding of the geometry native library, written by aqua <version>
// from main.aqua (docs/native-libraries.md). Do not edit; rebuild the library instead.
#pragma once

#include "geometry.h"

#include <cstddef>
#include <cstdint>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace geometry {

/// A call the library could not complete; what() is its reason.
class error : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

namespace detail {
inline void check() {
    if (const char *message = geo_last_error()) throw error(message);
}
inline std::string take(char *text) {
    std::string value = text ? text : "";
    geo_free(text);
    return value;
}
template <class T, class Array> std::vector<T> take_array(Array array) {
    std::vector<T> values(array.data, array.data + array.count);
    geo_free(array.data);
    return values;
}
/// std::vector<bool> stores bits; the C ABI takes bool elements.
inline std::unique_ptr<bool[]> bools(const std::vector<bool> &values) {
    auto copy = std::make_unique<bool[]>(values.size() ? values.size() : 1);
    for (std::size_t i = 0; i < values.size(); i++) copy[i] = values[i];
    return copy;
}
} // namespace detail

/// A point in the plane.
struct point {
    double x{};
    double y{};
};

namespace detail {
inline geo_point to_c(const point &value) {
    geo_point result;
    result.x = value.x;
    result.y = value.y;
    return result;
}
inline point from_c(geo_point value) {
    point result;
    result.x = value.x;
    result.y = value.y;
    return result;
}
} // namespace detail

/// A person with a name.
struct person {
    std::string name{};
    int32_t age{};
};

namespace detail {
inline geo_person to_c(const person &value) {
    geo_person result;
    result.name = const_cast<char *>(value.name.c_str());
    result.age = value.age;
    return result;
}
inline person from_c(geo_person value) {
    person result;
    result.name = take(value.name);
    result.age = value.age;
    return result;
}
} // namespace detail

/// Starts the library (runs its top-level code once). Optional: the first call starts it.
inline void initialize() {
    if (!geo_initialize()) detail::check();
}
/// Stops the library; a later call starts it again.
inline void shutdown() noexcept { geo_shutdown(); }

/// Area of a rectangle.
inline double area(double arg0, double arg1) {
    auto result = geo_area(arg0, arg1);
    detail::check();
    return result;
}

/// Distance between two points.
inline double distance(const point &a, const point &b) {
    auto result = geo_distance(detail::to_c(a), detail::to_c(b));
    detail::check();
    return result;
}

/// The point halfway between two points.
/// Both are copied in and the result is copied out.
inline point midpoint(const point &a, const point &b) {
    auto result = geo_midpoint(detail::to_c(a), detail::to_c(b));
    detail::check();
    return detail::from_c(result);
}

/// Every value multiplied by a factor.
inline std::vector<double> scale(const std::vector<double> &values, double factor) {
    auto result = geo_scale(values.data(), values.size(), factor);
    detail::check();
    return detail::take_array<double>(result);
}

inline int32_t sum(const std::vector<int32_t> &values) {
    auto result = geo_sum(values.data(), values.size());
    detail::check();
    return result;
}

inline float halve(float x) {
    auto result = geo_halve(x);
    detail::check();
    return result;
}

inline bool is_even(int32_t n) {
    auto result = geo_is_even(n);
    detail::check();
    return result;
}

inline std::vector<bool> evens(int32_t n) {
    auto result = geo_evens(n);
    detail::check();
    return detail::take_array<bool>(result);
}

inline std::vector<int64_t> squares(int32_t n) {
    auto result = geo_squares(n);
    detail::check();
    return detail::take_array<int64_t>(result);
}

inline float widths(const std::vector<float> &values) {
    auto result = geo_widths(values.data(), values.size());
    detail::check();
    return result;
}

inline int64_t total(const std::vector<int64_t> &values) {
    auto result = geo_total(values.data(), values.size());
    detail::check();
    return result;
}

inline int32_t count_true(const std::vector<bool> &values) {
    auto values_items = detail::bools(values);
    auto result = geo_count_true(values_items.get(), values.size());
    detail::check();
    return result;
}

/// Greets someone by name.
inline std::string greet(const std::string &arg0) {
    auto result = geo_greet(arg0.c_str());
    detail::check();
    return detail::take(result);
}

inline int64_t big(int64_t n) {
    auto result = geo_big(n);
    detail::check();
    return result;
}

inline person make_person(const std::string &name, int32_t age) {
    auto result = geo_make_person(name.c_str(), age);
    detail::check();
    return detail::from_c(result);
}

inline std::string describe(const person &p) {
    auto result = geo_describe(detail::to_c(p));
    detail::check();
    return detail::take(result);
}

inline void log(const std::string &message) {
    geo_log(message.c_str());
    detail::check();
}

inline int32_t divide(int32_t a, int32_t b) {
    auto result = geo_divide(a, b);
    detail::check();
    return result;
}

inline int32_t next() {
    auto result = geo_next();
    detail::check();
    return result;
}

} // namespace geometry
