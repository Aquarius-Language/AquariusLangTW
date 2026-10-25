/* The OpenGL scalar types of the Khronos API (khrplatform.h), for the function table in gl_functions.def. Only this
   library uses them; the functions themselves come from the driver through aqua_gl_proc. */
#ifndef OPENGL_GL_TYPES_H
#define OPENGL_GL_TYPES_H
#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32) && !defined(_WIN64)
#define GL_APIENTRY __stdcall
#else
#define GL_APIENTRY
#endif

typedef unsigned int GLenum;
typedef unsigned char GLboolean;
typedef unsigned int GLbitfield;
typedef signed char GLbyte;
typedef unsigned char GLubyte;
typedef short GLshort;
typedef unsigned short GLushort;
typedef int GLint;
typedef unsigned int GLuint;
typedef int GLsizei;
typedef float GLfloat;
typedef double GLdouble;
typedef char GLchar;
typedef intptr_t GLintptr;
typedef intptr_t GLsizeiptr;
typedef int64_t GLint64;
typedef uint64_t GLuint64;
typedef struct __GLsync *GLsync;

#endif
