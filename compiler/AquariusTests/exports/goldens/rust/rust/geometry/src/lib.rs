//! The Rust binding of the geometry native library, written by aqua <version> from
//! main.aqua (docs/native-libraries.md). Do not edit; rebuild the library instead.
//! Calls are serialized by the library; the first one runs its top-level code. A failed call gives `Err`.
#![allow(non_snake_case)]

use std::ffi::{c_char, c_void, CStr, CString};
use std::fmt;

/// The C ABI (the library's C header), for what the safe functions do not cover.
pub mod ffi {
    #![allow(non_camel_case_types)]
    use std::ffi::{c_char, c_void};

    #[repr(C)]
    #[derive(Debug, Clone, Copy)]
    pub struct int_array { pub data: *mut i32, pub count: usize }

    #[repr(C)]
    #[derive(Debug, Clone, Copy)]
    pub struct float_array { pub data: *mut f32, pub count: usize }

    #[repr(C)]
    #[derive(Debug, Clone, Copy)]
    pub struct double_array { pub data: *mut f64, pub count: usize }

    #[repr(C)]
    #[derive(Debug, Clone, Copy)]
    pub struct bool_array { pub data: *mut bool, pub count: usize }

    #[repr(C)]
    #[derive(Debug, Clone, Copy)]
    pub struct long_array { pub data: *mut i64, pub count: usize }

    #[repr(C)]
    #[derive(Debug, Clone, Copy)]
    pub struct point {
        pub x: f64,
        pub y: f64,
    }

    #[repr(C)]
    #[derive(Debug, Clone, Copy)]
    pub struct person {
        pub name: *mut c_char,
        pub age: i32,
    }

    extern "C" {
        pub fn geo_initialize() -> bool;
        pub fn geo_shutdown();
        pub fn geo_last_error() -> *const c_char;
        pub fn geo_free(memory: *mut c_void);
        pub fn geo_abi_version() -> u32;
        pub fn geo_area(arg0: f64, arg1: f64) -> f64;
        pub fn geo_distance(a: point, b: point) -> f64;
        pub fn geo_midpoint(a: point, b: point) -> point;
        pub fn geo_scale(values: *const f64, values_count: usize, factor: f64) -> double_array;
        pub fn geo_sum(values: *const i32, values_count: usize) -> i32;
        pub fn geo_halve(x: f32) -> f32;
        pub fn geo_is_even(n: i32) -> bool;
        pub fn geo_evens(n: i32) -> bool_array;
        pub fn geo_squares(n: i32) -> long_array;
        pub fn geo_widths(values: *const f32, values_count: usize) -> f32;
        pub fn geo_total(values: *const i64, values_count: usize) -> i64;
        pub fn geo_count_true(values: *const bool, values_count: usize) -> i32;
        pub fn geo_greet(arg0: *const c_char) -> *mut c_char;
        pub fn geo_big(n: i64) -> i64;
        pub fn geo_make_person(name: *const c_char, age: i32) -> person;
        pub fn geo_describe(p: person) -> *mut c_char;
        pub fn geo_log(message: *const c_char);
        pub fn geo_divide(a: i32, b: i32) -> i32;
        pub fn geo_next() -> i32;
    }
}

/// A call the library could not complete, with its reason.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Error(pub String);

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result { f.write_str(&self.0) }
}

impl std::error::Error for Error {}

fn check() -> Result<(), Error> {
    let error = unsafe { ffi::geo_last_error() };
    if error.is_null() { Ok(()) } else { Err(Error(unsafe { CStr::from_ptr(error) }.to_string_lossy().into_owned())) }
}

fn take_string(text: *mut c_char) -> String {
    if text.is_null() { return String::new(); }
    let value = unsafe { CStr::from_ptr(text) }.to_string_lossy().into_owned();
    unsafe { ffi::geo_free(text as *mut c_void) };
    value
}

fn take_vec<T: Copy>(data: *mut T, count: usize) -> Vec<T> {
    if data.is_null() { return Vec::new(); }
    let values = unsafe { std::slice::from_raw_parts(data, count) }.to_vec();
    unsafe { ffi::geo_free(data as *mut c_void) };
    values
}

fn c_string(text: &str) -> Result<CString, Error> {
    CString::new(text).map_err(|_| Error("A string argument contains a NUL character.".into()))
}

/// A point in the plane.
#[derive(Debug, Clone, PartialEq)]
pub struct Point {
    pub x: f64,
    pub y: f64,
}

impl Point {
    fn to_c(&self) -> Result<(ffi::point, Vec<CString>), Error> {
        let strings = Vec::new();
        Ok((ffi::point { x: self.x, y: self.y }, strings))
    }

    fn from_c(value: ffi::point) -> Self {
        Point { x: value.x, y: value.y }
    }
}

/// A person with a name.
#[derive(Debug, Clone, PartialEq)]
pub struct Person {
    pub name: String,
    pub age: i32,
}

impl Person {
    fn to_c(&self) -> Result<(ffi::person, Vec<CString>), Error> {
        let mut strings = Vec::new();
        strings.push(c_string(&self.name)?);
        let name = strings.last().unwrap().as_ptr() as *mut c_char;
        Ok((ffi::person { name, age: self.age }, strings))
    }

    fn from_c(value: ffi::person) -> Self {
        Person { name: take_string(value.name), age: value.age }
    }
}

/// Starts the library (runs its top-level code once). Optional: the first call starts it.
pub fn initialize() -> Result<(), Error> {
    if unsafe { ffi::geo_initialize() } { Ok(()) } else { check().and(Err(Error("The library did not start.".into()))) }
}

/// Stops the library; a later call starts it again.
pub fn shutdown() {
    unsafe { ffi::geo_shutdown() }
}

/// Area of a rectangle.
pub fn area(arg0: f64, arg1: f64) -> Result<f64, Error> {
    let result = unsafe { ffi::geo_area(arg0, arg1) };
    check()?;
    Ok(result)
}

/// Distance between two points.
pub fn distance(a: &Point, b: &Point) -> Result<f64, Error> {
    let (a, _a_strings) = a.to_c()?;
    let (b, _b_strings) = b.to_c()?;
    let result = unsafe { ffi::geo_distance(a, b) };
    check()?;
    Ok(result)
}

/// The point halfway between two points.
/// Both are copied in and the result is copied out.
pub fn midpoint(a: &Point, b: &Point) -> Result<Point, Error> {
    let (a, _a_strings) = a.to_c()?;
    let (b, _b_strings) = b.to_c()?;
    let result = unsafe { ffi::geo_midpoint(a, b) };
    check()?;
    Ok(Point::from_c(result))
}

/// Every value multiplied by a factor.
pub fn scale(values: &[f64], factor: f64) -> Result<Vec<f64>, Error> {
    let result = unsafe { ffi::geo_scale(values.as_ptr(), values.len(), factor) };
    check()?;
    Ok(take_vec(result.data, result.count))
}

pub fn sum(values: &[i32]) -> Result<i32, Error> {
    let result = unsafe { ffi::geo_sum(values.as_ptr(), values.len()) };
    check()?;
    Ok(result)
}

pub fn halve(x: f32) -> Result<f32, Error> {
    let result = unsafe { ffi::geo_halve(x) };
    check()?;
    Ok(result)
}

pub fn is_even(n: i32) -> Result<bool, Error> {
    let result = unsafe { ffi::geo_is_even(n) };
    check()?;
    Ok(result)
}

pub fn evens(n: i32) -> Result<Vec<bool>, Error> {
    let result = unsafe { ffi::geo_evens(n) };
    check()?;
    Ok(take_vec(result.data, result.count))
}

pub fn squares(n: i32) -> Result<Vec<i64>, Error> {
    let result = unsafe { ffi::geo_squares(n) };
    check()?;
    Ok(take_vec(result.data, result.count))
}

pub fn widths(values: &[f32]) -> Result<f32, Error> {
    let result = unsafe { ffi::geo_widths(values.as_ptr(), values.len()) };
    check()?;
    Ok(result)
}

pub fn total(values: &[i64]) -> Result<i64, Error> {
    let result = unsafe { ffi::geo_total(values.as_ptr(), values.len()) };
    check()?;
    Ok(result)
}

pub fn count_true(values: &[bool]) -> Result<i32, Error> {
    let result = unsafe { ffi::geo_count_true(values.as_ptr(), values.len()) };
    check()?;
    Ok(result)
}

/// Greets someone by name.
pub fn greet(arg0: &str) -> Result<String, Error> {
    let arg0 = c_string(arg0)?;
    let result = unsafe { ffi::geo_greet(arg0.as_ptr()) };
    check()?;
    Ok(take_string(result))
}

pub fn big(n: i64) -> Result<i64, Error> {
    let result = unsafe { ffi::geo_big(n) };
    check()?;
    Ok(result)
}

pub fn make_person(name: &str, age: i32) -> Result<Person, Error> {
    let name = c_string(name)?;
    let result = unsafe { ffi::geo_make_person(name.as_ptr(), age) };
    check()?;
    Ok(Person::from_c(result))
}

pub fn describe(p: &Person) -> Result<String, Error> {
    let (p, _p_strings) = p.to_c()?;
    let result = unsafe { ffi::geo_describe(p) };
    check()?;
    Ok(take_string(result))
}

pub fn log(message: &str) -> Result<(), Error> {
    let message = c_string(message)?;
    unsafe { ffi::geo_log(message.as_ptr()) };
    check()
}

pub fn divide(a: i32, b: i32) -> Result<i32, Error> {
    let result = unsafe { ffi::geo_divide(a, b) };
    check()?;
    Ok(result)
}

pub fn next() -> Result<i32, Error> {
    let result = unsafe { ffi::geo_next() };
    check()?;
    Ok(result)
}

