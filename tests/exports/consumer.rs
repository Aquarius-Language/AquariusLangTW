// The Rust consumer of the geometry test library (tests/exports/geometry): the same calls as consumer.c through the
// Rust binding (the geometry crate of the output), printing the lines of geometry.expected. Failures arrive as Err; the
// two cases safe Rust cannot express (a NULL string or array) go through geometry::ffi. scripts/test-exports.ps1
// writes the crate's Cargo.toml (a path dependency on the output) and builds it with the pinned Rust.
use std::io::Write;

fn hundredths(x: f64) -> i32 {
    (x * 100.0) as i32
}

fn error_line<T>(what: &str, result: Result<T, geometry::Error>) {
    match result {
        Ok(_) => println!("native error {}: (none)", what),
        Err(error) => println!("native error {}: {}", what, error),
    }
}

fn last_error() -> String {
    let error = unsafe { geometry::ffi::geo_last_error() };
    if error.is_null() { "(none)".into() } else { unsafe { std::ffi::CStr::from_ptr(error) }.to_string_lossy().into_owned() }
}

fn main() -> Result<(), geometry::Error> {
    println!("native abi {}", unsafe { geometry::ffi::geo_abi_version() });
    println!("area {}", hundredths(geometry::area(3.0, 4.5)?));
    let (a, b) = (geometry::Point { x: 0.0, y: 0.0 }, geometry::Point { x: 3.0, y: 4.0 });
    println!("distance {}", hundredths(geometry::distance(&a, &b)?));
    let m = geometry::midpoint(&a, &b)?;
    println!("midpoint {} {}", hundredths(m.x), hundredths(m.y));
    let scaled: Vec<String> = geometry::scale(&[1.0, 2.5, -4.0], 2.0)?.iter().map(|v| hundredths(*v).to_string()).collect();
    println!("scale {}", scaled.join(" "));
    println!("sum {}", geometry::sum(&[1, 2, 3, 4])?);
    println!("halve {}", hundredths(geometry::halve(5.0)? as f64));
    println!("isEven {} {}", geometry::is_even(4)? as i32, geometry::is_even(7)? as i32);
    let evens: Vec<String> = geometry::evens(5)?.iter().map(|v| (*v as i32).to_string()).collect();
    println!("evens {}", evens.join(" "));
    let squares: Vec<String> = geometry::squares(3)?.iter().map(|v| v.to_string()).collect();
    println!("squares {}", squares.join(" "));
    println!("widths {}", hundredths(geometry::widths(&[0.5, 1.25])? as f64));
    println!("total {}", geometry::total(&[5000000000, 1])?);
    println!("countTrue {}", geometry::count_true(&[true, false, true])?);
    println!("greet {}", geometry::greet("世界")?);
    println!("big {}", geometry::big(3000000000)?);
    let person = geometry::make_person("Ada", 36)?;
    println!("person {} {}", person.name, person.age);
    println!("describe {}", geometry::describe(&person)?);
    std::io::stdout().flush().unwrap(); // the library writes its own output
    geometry::log("hello")?;
    println!("next {}", geometry::next()?);
    println!("next {}", geometry::next()?);

    error_line("divide", geometry::divide(1, 0));
    // Rust strings are UTF-8 by construction: invalid UTF-8 can only come through the C ABI.
    unsafe { geometry::ffi::geo_greet(b"\xff\0".as_ptr() as *const std::ffi::c_char) };
    println!("c-abi error utf8: {}", last_error());
    unsafe { geometry::ffi::geo_greet(std::ptr::null()) };
    println!("native error null string: {}", last_error());
    unsafe { geometry::ffi::geo_sum(std::ptr::null(), 3) };
    println!("c-abi error null array: {}", last_error());
    geometry::area(1.0, 1.0)?;
    println!("native error {}", if unsafe { geometry::ffi::geo_last_error() }.is_null() { "cleared" } else { "kept" });
    geometry::shutdown();
    println!("native next after shutdown {}", geometry::next()?);
    geometry::shutdown();
    Ok(())
}
