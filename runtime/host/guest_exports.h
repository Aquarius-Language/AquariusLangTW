/* Guest exports the host calls (runtime/guest/runtime.c), in the order engines resolve them, with their parameter
   counts; each returns an i32. Optional ones may be missing from older apps. Also compiled into ahead-of-time application
   libraries (platform/desktop/aot_app.c). */
#ifndef AQUA_GUEST_EXPORTS_H
#define AQUA_GUEST_EXPORTS_H
#define GUEST_EXPORTS(X) \
    X(initialize, 1) X(execute, 4) X(invoke, 3) X(run, 2) X(result, 1) X(status, 1) X(resume, 2) \
    X(release_execution, 1) X(pin, 1) X(unpin, 1) X(value, 0) X(text, 1) X(allocate, 1) X(reserve, 1) \
    X(array, 1) X(array_values, 1) X(array_arguments, 1) X(array_resize, 2) X(hash, 1) X(hash_resize, 2) X(scope, 1) X(scope_set, 3) \
    X(scope_get, 2) X(collect, 0) X(collection_due, 0) X(collection_generation, 0) X(native_live, 1) X(pending, 0) \
    X(thread_module, 0) X(portable_module, 1) X(thread_snapshot_size, 0) X(thread_reserve, 1) X(thread_enter, 3) \
    X(thread_encode, 1) X(thread_decode, 1) X(thread_free_packet, 1)
#endif
