/* The Java binding's native half (docs/native-libraries.md): one JNI function per export, expanded from exports.def like
   exports.c, and registered with RegisterNatives in JNI_OnLoad on the binding's class (AQUA_EMBED_JAVA_CLASS, such as
   "org/example/geometry/Geometry"), so no Java_* symbol is exported. Compiled into the library when the java binding is
   built (Android, and the desktop JVM on Windows and Linux). Strings cross as UTF-16 (JNI's modified UTF-8 is not
   UTF-8); arrays as Java arrays; structs as instances of the class's nested classes with public fields; a failed call
   throws its nested AquariusException. exports.def's AQUA_JAVA_FUNCTION(name, "javaName", "signature") and
   AQUA_JAVA_STRUCT(index, "class") rows name the Java side. */
#include <jni.h>
#include <stdlib.h>
#include <string.h>
#include "aqua/embed.h"

#define EMBED_JOIN2(a, b) a##b
#define EMBED_JOIN(a, b) EMBED_JOIN2(a, b)

/* The Java VM that loaded the library; the Android layer's aqua_android_jni reads it. */
JavaVM *embed_java_vm;
static jclass exception_class;
typedef struct java_struct { jclass cls; jmethodID constructor; } java_struct;
static java_struct structs[
#define AQUA_STRUCT(index, s, binding, count) 1 +
#define AQUA_FIELD(s, index, T, f)
#define AQUA_STRUCT_END(s, index)
#define AQUA_FUNCTION(index, name, binding, R)
#define AQUA_NO_PARAMETERS
#define AQUA_PARAMETER(SEPARATOR, T, n)
#define AQUA_CALL(index, R)
#define AQUA_ARGUMENT(T, n)
#define AQUA_RETURN(R)
#define AQUA_JAVA_FUNCTION(name, java, signature)
#define AQUA_JAVA_STRUCT(index, java)
#include "exports.def"
    1];
#undef AQUA_STRUCT
#undef AQUA_FIELD
#undef AQUA_STRUCT_END

static void throw_error(JNIEnv *env) {
    const char *message = aqua_embed_last_error();
    (*env)->ThrowNew(env, exception_class, message ? message : "The library call failed.");
}

/* ---- Strings: UTF-16 on the Java side, UTF-8 on the library's ---- */
static char *utf8_of(JNIEnv *env, jstring text) {
    jsize n = (*env)->GetStringLength(env, text);
    const jchar *units = (*env)->GetStringChars(env, text, NULL);
    char *out = malloc((size_t)n * 3 + 1), *p = out;
    for (jsize i = 0; i < n; i++) {
        uint32_t c = units[i];
        if (c >= 0xd800 && c < 0xdc00 && i + 1 < n && units[i + 1] >= 0xdc00 && units[i + 1] < 0xe000) c = 0x10000 + ((c - 0xd800) << 10) + (units[++i] - 0xdc00);
        else if (c >= 0xd800 && c < 0xe000) c = 0xfffd; /* a lone surrogate */
        if (c < 0x80) *p++ = (char)c;
        else if (c < 0x800) { *p++ = (char)(0xc0 | c >> 6); *p++ = (char)(0x80 | (c & 63)); }
        else if (c < 0x10000) { *p++ = (char)(0xe0 | c >> 12); *p++ = (char)(0x80 | (c >> 6 & 63)); *p++ = (char)(0x80 | (c & 63)); }
        else { *p++ = (char)(0xf0 | c >> 18); *p++ = (char)(0x80 | (c >> 12 & 63)); *p++ = (char)(0x80 | (c >> 6 & 63)); *p++ = (char)(0x80 | (c & 63)); }
    }
    *p = 0;
    (*env)->ReleaseStringChars(env, text, units);
    return out;
}
/* A Java string of the library's UTF-8 (freed here); NULL for NULL. */
static jstring java_string(JNIEnv *env, char *utf8) {
    if (!utf8) return NULL;
    size_t n = strlen(utf8), k = 0;
    jchar *units = malloc((n + 1) * sizeof *units);
    const unsigned char *s = (const unsigned char *)utf8;
    for (size_t i = 0; i < n;) {
        uint32_t c = s[i];
        int extra = c >= 0xf0 ? 3 : c >= 0xe0 ? 2 : c >= 0xc0 ? 1 : 0;
        c = extra ? c & (0x3f >> extra) : c;
        for (int j = 1; j <= extra && i + j < n; j++) c = c << 6 | (s[i + j] & 63);
        i += (size_t)extra + 1;
        if (c >= 0x10000) { c -= 0x10000; units[k++] = (jchar)(0xd800 + (c >> 10)); units[k++] = (jchar)(0xdc00 + (c & 0x3ff)); }
        else units[k++] = (jchar)c;
    }
    jstring result = (*env)->NewString(env, units, (jsize)k);
    free(units); free(utf8);
    return result;
}
static void put_string(JNIEnv *env, jstring text) {
    if (!text) { aqua_embed_put_string(NULL); return; }
    char *utf8 = utf8_of(env, text);
    aqua_embed_put_string(utf8);
    free(utf8);
}

/* ---- Arrays ---- */
#define JAVA_ARRAYS(X) X(int, Int, jint, AQUA_EMBED_INT) X(long, Long, jlong, AQUA_EMBED_LONG) X(float, Float, jfloat, AQUA_EMBED_FLOAT) \
    X(double, Double, jdouble, AQUA_EMBED_DOUBLE) X(boolean, Boolean, jboolean, AQUA_EMBED_BOOL)
#define X(name, Name, type, element) \
    static void put_##name##s(JNIEnv *env, type##Array array) { \
        if (!array) { aqua_embed_put_array(NULL, 0, element); return; } \
        jsize n = (*env)->GetArrayLength(env, array); \
        type *items = (*env)->Get##Name##ArrayElements(env, array, NULL); \
        aqua_embed_put_array(items, (size_t)n, element); \
        (*env)->Release##Name##ArrayElements(env, array, items, JNI_ABORT); \
    } \
    static type##Array take_##name##s(JNIEnv *env) { \
        size_t n = 0; \
        type *items = aqua_embed_array(&n, element); \
        type##Array array = (*env)->New##Name##Array(env, (jsize)n); \
        if (array && n) (*env)->Set##Name##ArrayRegion(env, array, 0, (jsize)n, items); \
        free(items); \
        return array; \
    }
JAVA_ARRAYS(X)
#undef X

/* ---- Types: JNI type, how a value is put, a field read and a field written ---- */
#define JTYPE_INT jint
#define JTYPE_LONG jlong
#define JTYPE_FLOAT jfloat
#define JTYPE_DOUBLE jdouble
#define JTYPE_BOOL jboolean
#define JTYPE_STRING jstring
#define JTYPE_INT_ARRAY jintArray
#define JTYPE_LONG_ARRAY jlongArray
#define JTYPE_FLOAT_ARRAY jfloatArray
#define JTYPE_DOUBLE_ARRAY jdoubleArray
#define JTYPE_BOOL_ARRAY jbooleanArray
#define JTYPE_STRUCT(s) jobject
#define JTYPE_VOID void
#define JPUT_INT(env, n) aqua_embed_put_int(n)
#define JPUT_LONG(env, n) aqua_embed_put_long(n)
#define JPUT_FLOAT(env, n) aqua_embed_put_float(n)
#define JPUT_DOUBLE(env, n) aqua_embed_put_double(n)
#define JPUT_BOOL(env, n) aqua_embed_put_bool(n)
#define JPUT_STRING(env, n) put_string(env, n)
#define JPUT_INT_ARRAY(env, n) put_ints(env, n)
#define JPUT_LONG_ARRAY(env, n) put_longs(env, n)
#define JPUT_FLOAT_ARRAY(env, n) put_floats(env, n)
#define JPUT_DOUBLE_ARRAY(env, n) put_doubles(env, n)
#define JPUT_BOOL_ARRAY(env, n) put_booleans(env, n)
#define JPUT_STRUCT(s) EMBED_JOIN(put_struct_, s)
#define FIELD(env, cls, f, signature) (*env)->GetFieldID(env, cls, #f, signature)
#define JGET_INT(env, o, cls, f) aqua_embed_put_int((*env)->GetIntField(env, o, FIELD(env, cls, f, "I")))
#define JGET_LONG(env, o, cls, f) aqua_embed_put_long((*env)->GetLongField(env, o, FIELD(env, cls, f, "J")))
#define JGET_FLOAT(env, o, cls, f) aqua_embed_put_float((*env)->GetFloatField(env, o, FIELD(env, cls, f, "F")))
#define JGET_DOUBLE(env, o, cls, f) aqua_embed_put_double((*env)->GetDoubleField(env, o, FIELD(env, cls, f, "D")))
#define JGET_BOOL(env, o, cls, f) aqua_embed_put_bool((*env)->GetBooleanField(env, o, FIELD(env, cls, f, "Z")))
#define JGET_STRING(env, o, cls, f) put_string(env, (jstring)(*env)->GetObjectField(env, o, FIELD(env, cls, f, "Ljava/lang/String;")))
#define JSET_INT(env, o, cls, f, i) (*env)->SetIntField(env, o, FIELD(env, cls, f, "I"), aqua_embed_int(i))
#define JSET_LONG(env, o, cls, f, i) (*env)->SetLongField(env, o, FIELD(env, cls, f, "J"), aqua_embed_long(i))
#define JSET_FLOAT(env, o, cls, f, i) (*env)->SetFloatField(env, o, FIELD(env, cls, f, "F"), aqua_embed_float(i))
#define JSET_DOUBLE(env, o, cls, f, i) (*env)->SetDoubleField(env, o, FIELD(env, cls, f, "D"), aqua_embed_double(i))
#define JSET_BOOL(env, o, cls, f, i) (*env)->SetBooleanField(env, o, FIELD(env, cls, f, "Z"), aqua_embed_bool(i))
#define JSET_STRING(env, o, cls, f, i) (*env)->SetObjectField(env, o, FIELD(env, cls, f, "Ljava/lang/String;"), java_string(env, aqua_embed_string(i)))

/* Pass 1: a struct argument is its fields, then the struct (a null one fails the call). */
#define AQUA_STRUCT(index, s, binding, count) static void EMBED_JOIN(put_struct_, s)(JNIEnv *env, jobject value) { \
    jclass cls = structs[index].cls; \
    if (!value) { aqua_embed_fail_argument("A struct argument is null."); return; }
#define AQUA_FIELD(s, index, T, f) JGET_##T(env, value, cls, f);
#define AQUA_STRUCT_END(s, index) aqua_embed_put_struct(index); }
#include "exports.def"
#undef AQUA_STRUCT
#undef AQUA_FIELD
#undef AQUA_STRUCT_END

/* Pass 2: a struct result, a new instance field by field. */
#define AQUA_STRUCT(index, s, binding, count) static jobject EMBED_JOIN(take_struct_, s)(JNIEnv *env) { \
    jclass cls = structs[index].cls; \
    jobject value = (*env)->NewObject(env, cls, structs[index].constructor); \
    if (!value) return NULL;
#define AQUA_FIELD(s, index, T, f) JSET_##T(env, value, cls, f, index);
#define AQUA_STRUCT_END(s, index) return value; }
#include "exports.def"
#undef AQUA_STRUCT
#undef AQUA_FIELD
#undef AQUA_STRUCT_END
#undef AQUA_FUNCTION
#undef AQUA_PARAMETER
#undef AQUA_CALL
#undef AQUA_ARGUMENT
#undef AQUA_RETURN

/* Pass 3: the JNI functions. */
#define JDEFINE_VOID
#define JDEFINE_INT jint result = 0;
#define JDEFINE_LONG jlong result = 0;
#define JDEFINE_FLOAT jfloat result = 0;
#define JDEFINE_DOUBLE jdouble result = 0;
#define JDEFINE_BOOL jboolean result = JNI_FALSE;
#define JDEFINE_STRING jstring result = NULL;
#define JDEFINE_INT_ARRAY jintArray result = NULL;
#define JDEFINE_LONG_ARRAY jlongArray result = NULL;
#define JDEFINE_FLOAT_ARRAY jfloatArray result = NULL;
#define JDEFINE_DOUBLE_ARRAY jdoubleArray result = NULL;
#define JDEFINE_BOOL_ARRAY jbooleanArray result = NULL;
#define JDEFINE_STRUCT(s) jobject result = NULL;
#define JSTORE_VOID
#define JSTORE_INT result = aqua_embed_int(-1);
#define JSTORE_LONG result = aqua_embed_long(-1);
#define JSTORE_FLOAT result = aqua_embed_float(-1);
#define JSTORE_DOUBLE result = aqua_embed_double(-1);
#define JSTORE_BOOL result = aqua_embed_bool(-1) ? JNI_TRUE : JNI_FALSE;
#define JSTORE_STRING result = java_string(env, aqua_embed_string(-1));
#define JSTORE_INT_ARRAY result = take_ints(env);
#define JSTORE_LONG_ARRAY result = take_longs(env);
#define JSTORE_FLOAT_ARRAY result = take_floats(env);
#define JSTORE_DOUBLE_ARRAY result = take_doubles(env);
#define JSTORE_BOOL_ARRAY result = take_booleans(env);
#define JSTORE_STRUCT(s) result = EMBED_JOIN(take_struct_, s)(env);
#define JVALUE_VOID
#define JVALUE_INT result
#define JVALUE_LONG result
#define JVALUE_FLOAT result
#define JVALUE_DOUBLE result
#define JVALUE_BOOL result
#define JVALUE_STRING result
#define JVALUE_INT_ARRAY result
#define JVALUE_LONG_ARRAY result
#define JVALUE_FLOAT_ARRAY result
#define JVALUE_DOUBLE_ARRAY result
#define JVALUE_BOOL_ARRAY result
#define JVALUE_STRUCT(s) result
#define AQUA_STRUCT(index, s, binding, count)
#define AQUA_FIELD(s, index, T, f)
#define AQUA_STRUCT_END(s, index)
#define AQUA_FUNCTION(index, name, binding, R) static JTYPE_##R JNICALL EMBED_JOIN(jni_, name)(JNIEnv *env, jclass cls
#define AQUA_PARAMETER(SEPARATOR, T, n) , JTYPE_##T n
#define AQUA_CALL(index, R) ) { (void)cls; JDEFINE_##R if (!aqua_embed_begin(index)) { throw_error(env); return JVALUE_##R; }
#define AQUA_ARGUMENT(T, n) JPUT_##T(env, n);
#define AQUA_RETURN(R) bool ok = aqua_embed_invoke(); if (ok) { JSTORE_##R } aqua_embed_end(); \
    if (!ok) throw_error(env); return JVALUE_##R; }
#include "exports.def"
#undef AQUA_FUNCTION
#undef AQUA_PARAMETER
#undef AQUA_CALL
#undef AQUA_ARGUMENT
#undef AQUA_RETURN
#undef AQUA_JAVA_FUNCTION
#undef AQUA_JAVA_STRUCT

static jboolean JNICALL lifecycle_initialize(JNIEnv *env, jclass cls) {
    (void)cls;
    if (aqua_embed_initialize()) return JNI_TRUE;
    throw_error(env);
    return JNI_FALSE;
}
static void JNICALL lifecycle_shutdown(JNIEnv *env, jclass cls) { (void)env; (void)cls; aqua_embed_shutdown(); }
static jint JNICALL lifecycle_abi_version(JNIEnv *env, jclass cls) { (void)env; (void)cls; return AQUA_EMBED_ABI_VERSION; }

/* Pass 4: the natives to register and the struct classes to find. */
#define AQUA_FUNCTION(index, name, binding, R)
#define AQUA_PARAMETER(SEPARATOR, T, n)
#define AQUA_CALL(index, R)
#define AQUA_ARGUMENT(T, n)
#define AQUA_RETURN(R)
#define AQUA_JAVA_STRUCT(index, java)
#define AQUA_JAVA_FUNCTION(name, java, signature) {java, signature, (void *)EMBED_JOIN(jni_, name)},
static const JNINativeMethod methods[] = {
    {"initialize", "()Z", (void *)lifecycle_initialize},
    {"shutdown", "()V", (void *)lifecycle_shutdown},
    {"abiVersion", "()I", (void *)lifecycle_abi_version},
#include "exports.def"
};
#undef AQUA_JAVA_FUNCTION
#undef AQUA_JAVA_STRUCT
#define AQUA_JAVA_FUNCTION(name, java, signature)
#define AQUA_JAVA_STRUCT(index, java) java,
static const char *const struct_classes[] = {
#include "exports.def"
    NULL};

/* Finds the binding's classes with the loader of the class whose static initializer loads this library, then
   registers its natives. */
JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved) {
    (void)reserved;
    JNIEnv *env;
    if ((*vm)->GetEnv(vm, (void **)&env, JNI_VERSION_1_6) != JNI_OK) return JNI_ERR;
    embed_java_vm = vm;
    jclass binding = (*env)->FindClass(env, AQUA_EMBED_JAVA_CLASS);
    jclass exception = (*env)->FindClass(env, AQUA_EMBED_JAVA_CLASS "$AquariusException");
    if (!binding || !exception) return JNI_ERR;
    exception_class = (*env)->NewGlobalRef(env, exception);
    for (size_t s = 0; struct_classes[s]; s++) {
        jclass cls = (*env)->FindClass(env, struct_classes[s]);
        if (!cls) return JNI_ERR;
        structs[s].cls = (*env)->NewGlobalRef(env, cls);
        structs[s].constructor = (*env)->GetMethodID(env, cls, "<init>", "()V");
        if (!structs[s].constructor) return JNI_ERR;
    }
    if ((*env)->RegisterNatives(env, binding, methods, (jint)(sizeof methods / sizeof *methods)) != JNI_OK) return JNI_ERR;
    return JNI_VERSION_1_6;
}
