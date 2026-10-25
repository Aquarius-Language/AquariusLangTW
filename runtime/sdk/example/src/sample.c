#include <aqua/aqua.h>
#include <aqua/image.h>
static void add(aqua_call *call) { aqua_return_double(call, aqua_arg_number(call, 0) + aqua_arg_number(call, 1)); }
static void greet(aqua_call *call) { aqua_return_cstring(call, "hello from " AQUA_PLATFORM_NAME); }
static void make_image(aqua_call *call) {
    uint8_t px[16] = {255,0,0,255, 0,255,0,255, 0,0,255,255, 9,9,9,9};
    aqua_return(call, aqua_image_new(aqua_call_runtime(call), 2, 2, px, AQUA_ALPHA_STRAIGHT));
}
AQUA_LIBRARY(sample) {
    aqua_class *module = aqua_define_module(library, "Sample|範例");
    aqua_define_function(module, "add|相加", add, "nn");
    aqua_define_function(module, "greet|問候", greet, "");
    aqua_define_function(module, "image|影像", make_image, "");
    aqua_define_int(module, "VERSION|版本", 1);
}
