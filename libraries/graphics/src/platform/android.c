/* Android text through android.graphics (JNI on the runtime thread, aqua/android.h): a Paint with the family's Typeface
   measures the line (advance plus 2 pixels of overhang, ascent to descent) and draws it white into an ARGB_8888 Bitmap
   whose alpha is the mask. Installed faces are android.graphics.fonts.SystemFonts, named "<file>:<ttc index>" as the
   earlier Android runtime named them, next to the generic sans-serif, serif and monospace families. */
#include "platform.h"
#include <aqua/android.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define FACE_LIMIT 512

typedef struct face { char name[160]; char path[512]; int ttc, weight; bool italic; jobject typeface; } face;
static face *faces;
static int face_count;
static bool enumerated;

static bool failed(JNIEnv *env) {
    if (!(*env)->ExceptionCheck(env)) return false;
    (*env)->ExceptionClear(env);
    return true;
}

static jstring utf(JNIEnv *env, const char *text, size_t length) {
    /* NewStringUTF needs modified UTF-8; go through a byte array and String(byte[], "UTF-8") instead. */
    jbyteArray bytes = (*env)->NewByteArray(env, (jsize)length);
    if (!bytes) return NULL;
    (*env)->SetByteArrayRegion(env, bytes, 0, (jsize)length, (const jbyte *)text);
    jclass string_class = (*env)->FindClass(env, "java/lang/String");
    jmethodID make = (*env)->GetMethodID(env, string_class, "<init>", "([BLjava/lang/String;)V");
    jstring charset = (*env)->NewStringUTF(env, "UTF-8");
    jstring result = (jstring)(*env)->NewObject(env, string_class, make, bytes, charset);
    return failed(env) ? NULL : result;
}

/* Fills the face list from SystemFonts.getAvailableFonts() (once). */
static void enumerate_faces(JNIEnv *env) {
    if (enumerated) return;
    enumerated = true;
    faces = calloc(FACE_LIMIT, sizeof *faces);
    if ((*env)->PushLocalFrame(env, 64) != 0) return;
    jclass system_fonts = (*env)->FindClass(env, "android/graphics/fonts/SystemFonts");
    if (failed(env) || !system_fonts) { (*env)->PopLocalFrame(env, NULL); return; }
    jmethodID available = (*env)->GetStaticMethodID(env, system_fonts, "getAvailableFonts", "()Ljava/util/Set;");
    jobject set = (*env)->CallStaticObjectMethod(env, system_fonts, available);
    jclass set_class = (*env)->FindClass(env, "java/util/Set");
    jobjectArray array = (jobjectArray)(*env)->CallObjectMethod(env, set, (*env)->GetMethodID(env, set_class, "toArray", "()[Ljava/lang/Object;"));
    if (failed(env) || !array) { (*env)->PopLocalFrame(env, NULL); return; }
    jclass font_class = (*env)->FindClass(env, "android/graphics/fonts/Font");
    jclass style_class = (*env)->FindClass(env, "android/graphics/fonts/FontStyle");
    jclass file_class = (*env)->FindClass(env, "java/io/File");
    jmethodID get_file = (*env)->GetMethodID(env, font_class, "getFile", "()Ljava/io/File;");
    jmethodID get_ttc = (*env)->GetMethodID(env, font_class, "getTtcIndex", "()I");
    jmethodID get_style = (*env)->GetMethodID(env, font_class, "getStyle", "()Landroid/graphics/fonts/FontStyle;");
    jmethodID get_weight = (*env)->GetMethodID(env, style_class, "getWeight", "()I");
    jmethodID get_slant = (*env)->GetMethodID(env, style_class, "getSlant", "()I");
    jmethodID get_name = (*env)->GetMethodID(env, file_class, "getName", "()Ljava/lang/String;");
    jmethodID get_path = (*env)->GetMethodID(env, file_class, "getAbsolutePath", "()Ljava/lang/String;");
    jsize count = (*env)->GetArrayLength(env, array);
    for (jsize i = 0; i < count && face_count < FACE_LIMIT; i++) {
        (*env)->PushLocalFrame(env, 16);
        jobject font = (*env)->GetObjectArrayElement(env, array, i);
        jobject file = (*env)->CallObjectMethod(env, font, get_file);
        if (!failed(env) && file) {
            int ttc = (*env)->CallIntMethod(env, font, get_ttc);
            jobject style = (*env)->CallObjectMethod(env, font, get_style);
            int weight = style ? (*env)->CallIntMethod(env, style, get_weight) : 400;
            int slant = style ? (*env)->CallIntMethod(env, style, get_slant) : 0;
            jstring name = (jstring)(*env)->CallObjectMethod(env, file, get_name);
            jstring path = (jstring)(*env)->CallObjectMethod(env, file, get_path);
            if (!failed(env) && name && path) {
                const char *n = (*env)->GetStringUTFChars(env, name, NULL), *p = (*env)->GetStringUTFChars(env, path, NULL);
                face f = {0};
                snprintf(f.name, sizeof f.name, "%s:%d", n, ttc);
                snprintf(f.path, sizeof f.path, "%s", p);
                f.ttc = ttc; f.weight = weight; f.italic = slant != 0;
                bool duplicate = false;
                for (int k = 0; k < face_count; k++) if (strcmp(faces[k].name, f.name) == 0) duplicate = true;
                if (!duplicate) faces[face_count++] = f;
                (*env)->ReleaseStringUTFChars(env, name, n);
                (*env)->ReleaseStringUTFChars(env, path, p);
            }
        }
        failed(env);
        (*env)->PopLocalFrame(env, NULL);
    }
    (*env)->PopLocalFrame(env, NULL);
}

/* A local reference to the Typeface of `family` (generic names, enumerated faces, or Typeface.create's fallback). */
static jobject typeface(JNIEnv *env, const char *family) {
    jclass typeface_class = (*env)->FindClass(env, "android/graphics/Typeface");
    bool generic = strcmp(family, "sans-serif") == 0 || strcmp(family, "serif") == 0 || strcmp(family, "monospace") == 0;
    if (!generic) {
        enumerate_faces(env);
        for (int i = 0; i < face_count; i++) {
            face *f = &faces[i];
            if (strcmp(f->name, family) != 0) continue;
            if (!f->typeface) {
                jclass file_class = (*env)->FindClass(env, "java/io/File");
                jobject file = (*env)->NewObject(env, file_class, (*env)->GetMethodID(env, file_class, "<init>", "(Ljava/lang/String;)V"),
                                                 (*env)->NewStringUTF(env, f->path));
                jclass builder_class = (*env)->FindClass(env, "android/graphics/Typeface$Builder");
                jobject builder = (*env)->NewObject(env, builder_class, (*env)->GetMethodID(env, builder_class, "<init>", "(Ljava/io/File;)V"), file);
                const char *builder_type = "(I)Landroid/graphics/Typeface$Builder;";
                (*env)->CallObjectMethod(env, builder, (*env)->GetMethodID(env, builder_class, "setTtcIndex", builder_type), f->ttc);
                (*env)->CallObjectMethod(env, builder, (*env)->GetMethodID(env, builder_class, "setWeight", builder_type), f->weight);
                (*env)->CallObjectMethod(env, builder, (*env)->GetMethodID(env, builder_class, "setItalic", "(Z)Landroid/graphics/Typeface$Builder;"),
                                        (jboolean)f->italic);
                jobject built = (*env)->CallObjectMethod(env, builder, (*env)->GetMethodID(env, builder_class, "build", "()Landroid/graphics/Typeface;"));
                if (!failed(env) && built) f->typeface = (*env)->NewGlobalRef(env, built);
            }
            if (f->typeface) return (*env)->NewLocalRef(env, f->typeface);
        }
    }
    jmethodID create = (*env)->GetStaticMethodID(env, typeface_class, "create", "(Ljava/lang/String;I)Landroid/graphics/Typeface;");
    jobject result = (*env)->CallStaticObjectMethod(env, typeface_class, create, (*env)->NewStringUTF(env, family), 0);
    return failed(env) ? NULL : result;
}

/* A Paint for the family at `size` pixels. */
static jobject paint(JNIEnv *env, const char *family, float size) {
    jclass paint_class = (*env)->FindClass(env, "android/graphics/Paint");
    jobject p = (*env)->NewObject(env, paint_class, (*env)->GetMethodID(env, paint_class, "<init>", "(I)V"), 1 /* ANTI_ALIAS_FLAG */);
    if (failed(env) || !p) return NULL;
    jobject face = typeface(env, family);
    if (face) (*env)->CallObjectMethod(env, p, (*env)->GetMethodID(env, paint_class, "setTypeface", "(Landroid/graphics/Typeface;)Landroid/graphics/Typeface;"), face);
    (*env)->CallVoidMethod(env, p, (*env)->GetMethodID(env, paint_class, "setTextSize", "(F)V"), (jfloat)size);
    return failed(env) ? NULL : p;
}

typedef struct line_box { float advance, ascent, descent; } line_box;

static bool measure(JNIEnv *env, jobject p, jstring text, line_box *box) {
    jclass paint_class = (*env)->GetObjectClass(env, p);
    box->advance = (*env)->CallFloatMethod(env, p, (*env)->GetMethodID(env, paint_class, "measureText", "(Ljava/lang/String;)F"), text);
    box->ascent = (*env)->CallFloatMethod(env, p, (*env)->GetMethodID(env, paint_class, "ascent", "()F"));
    box->descent = (*env)->CallFloatMethod(env, p, (*env)->GetMethodID(env, paint_class, "descent", "()F"));
    return !failed(env);
}

bool gfx_platform_text_box(const char *font, float size, const char *text, size_t length, int *width, int *height) {
    JNIEnv *env = aqua_android_jni();
    if (!env || (*env)->PushLocalFrame(env, 32) != 0) return false;
    jobject p = paint(env, font, size);
    jstring s = p ? utf(env, text, length) : NULL;
    line_box box;
    bool ok = s && measure(env, p, s, &box);
    if (ok) {
        int w = (int)ceilf(box.advance) + 2, h = (int)ceilf(box.descent - box.ascent);
        *width = w > 1 ? w : 1;
        *height = h > 1 ? h : 1;
    }
    (*env)->PopLocalFrame(env, NULL);
    return ok;
}

bool gfx_platform_text_raster(const char *font, float size, const char *text, size_t length, int width, int height, uint8_t *alpha) {
    JNIEnv *env = aqua_android_jni();
    if (!env || (*env)->PushLocalFrame(env, 32) != 0) return false;
    bool ok = false;
    jobject p = paint(env, font, size);
    jstring s = p ? utf(env, text, length) : NULL;
    line_box box;
    if (s && measure(env, p, s, &box)) {
        jclass bitmap_class = (*env)->FindClass(env, "android/graphics/Bitmap");
        jclass config_class = (*env)->FindClass(env, "android/graphics/Bitmap$Config");
        jobject argb = (*env)->GetStaticObjectField(env, config_class,
                                                    (*env)->GetStaticFieldID(env, config_class, "ARGB_8888", "Landroid/graphics/Bitmap$Config;"));
        jobject bitmap = (*env)->CallStaticObjectMethod(env, bitmap_class,
            (*env)->GetStaticMethodID(env, bitmap_class, "createBitmap", "(IILandroid/graphics/Bitmap$Config;)Landroid/graphics/Bitmap;"), width, height, argb);
        if (!failed(env) && bitmap) {
            jclass canvas_class = (*env)->FindClass(env, "android/graphics/Canvas");
            jobject canvas = (*env)->NewObject(env, canvas_class, (*env)->GetMethodID(env, canvas_class, "<init>", "(Landroid/graphics/Bitmap;)V"), bitmap);
            jclass paint_class = (*env)->GetObjectClass(env, p);
            (*env)->CallVoidMethod(env, p, (*env)->GetMethodID(env, paint_class, "setColor", "(I)V"), (jint)0xFFFFFFFF);
            (*env)->CallVoidMethod(env, canvas, (*env)->GetMethodID(env, canvas_class, "drawText", "(Ljava/lang/String;FFLandroid/graphics/Paint;)V"),
                                   s, 0.0f, -box.ascent, p);
            jintArray pixels = (*env)->NewIntArray(env, width * height);
            (*env)->CallVoidMethod(env, bitmap, (*env)->GetMethodID(env, bitmap_class, "getPixels", "([IIIIIII)V"), pixels, 0, width, 0, 0, width, height);
            if (!failed(env) && pixels) {
                jint *values = (*env)->GetIntArrayElements(env, pixels, NULL);
                for (int i = 0; i < width * height; i++) alpha[i] = (uint8_t)((uint32_t)values[i] >> 24);
                (*env)->ReleaseIntArrayElements(env, pixels, values, JNI_ABORT);
                ok = true;
            }
            (*env)->CallVoidMethod(env, bitmap, (*env)->GetMethodID(env, bitmap_class, "recycle", "()V"));
            failed(env);
        }
    }
    (*env)->PopLocalFrame(env, NULL);
    return ok;
}

int gfx_platform_text_padding(void) { return 2; }

void gfx_platform_fonts(platform_font_add add, void (*done)(void *data, const char *error), void *data) {
    JNIEnv *env = aqua_android_jni();
    if (!env) { done(data, "System font enumeration is unavailable."); return; }
    add(data, "sans-serif", "Regular");
    add(data, "serif", "Regular");
    add(data, "monospace", "Regular");
    enumerate_faces(env);
    for (int i = 0; i < face_count; i++) add(data, faces[i].name, faces[i].italic ? "Italic" : faces[i].weight >= 700 ? "Bold" : "Regular");
    done(data, NULL);
}

bool gfx_platform_font_available(const char *family) {
    if (strcmp(family, "sans-serif") == 0 || strcmp(family, "serif") == 0 || strcmp(family, "monospace") == 0) return true;
    JNIEnv *env = aqua_android_jni();
    if (!env) return false;
    enumerate_faces(env);
    for (int i = 0; i < face_count; i++) if (strcmp(faces[i].name, family) == 0) return true;
    return false;
}
