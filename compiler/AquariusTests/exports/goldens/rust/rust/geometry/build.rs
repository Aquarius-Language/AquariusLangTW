// Links the geometry shared library: from GEOMETRY_LIB_DIR, else the lib/ folder of the output this crate came with.
use std::path::PathBuf;

fn main() {
    let folder = match std::env::var("GEOMETRY_LIB_DIR") {
        Ok(folder) => PathBuf::from(folder),
        Err(_) => PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../lib"),
    };
    println!("cargo:rerun-if-env-changed=GEOMETRY_LIB_DIR");
    println!("cargo:rustc-link-search=native={}", folder.display());
    println!("cargo:rustc-link-lib=dylib=geometry");
}
