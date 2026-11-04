/* Terminal / 終端機: child processes the script asks for explicitly. terminal.c owns the module, argument validation,
 * results and the Terminal.Process objects of Start; the platform layer (platform/<os>.c) launches a process with
 * pipes and collects its output on a worker thread, which hands output to terminal_job_output and reports completion
 * with terminal_job_done (both delivered to the script through aqua_post). */
#ifndef TERMINAL_H
#define TERMINAL_H

#include <aqua/aqua.h>
#include <stdatomic.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Targets that can run child processes; everywhere else IsSupported() is false. */
#if defined(AQUA_OS_WINDOWS)
#define TERMINAL_WINDOWS 1
#elif defined(AQUA_OS_LINUX) || defined(AQUA_OS_ANDROID) || defined(AQUA_OS_MACOS)
#define TERMINAL_POSIX 1
#endif
#if defined(TERMINAL_WINDOWS) || defined(TERMINAL_POSIX)
#define TERMINAL_PROCESSES 1
#endif

/* Each stream keeps at most this many UTF-16 code units (the language's string length), and keeps draining after. */
#define TERMINAL_OUTPUT_LIMIT (1024 * 1024)

typedef struct terminal_output {
    char *data;            /* UTF-8 */
    size_t length, capacity;
    size_t units;          /* UTF-16 code units retained */
    bool full;             /* the limit was reached: later bytes are discarded */
} terminal_output;

/* Appends bytes a stream produced, keeping whole UTF-8 sequences up to TERMINAL_OUTPUT_LIMIT units. */
void terminal_output_append(terminal_output *output, const char *bytes, size_t length);

typedef struct terminal_state terminal_state;
typedef struct terminal_process terminal_process;

/* Bytes of one stream not yet handed to the script's callback (Start's OnOutput / OnError). */
typedef struct terminal_chunk { char *data; size_t length, capacity; } terminal_chunk;

/* One Run/RunScript/Start call. The request fields are set before the launch; the worker thread fills the result
   fields and then calls terminal_job_done; the runtime thread reads them only after that. Streamed chunks are shared
   under `lock`. */
typedef struct terminal_job {
    char *executable;          /* as given, or resolved to a packaged file */
    char **arguments;          /* argv[1..]: count entries */
    uint32_t count;
    char *directory;           /* working directory, or NULL for the runtime's */
    int32_t timeout_ms;        /* 0: no limit (Start without TimeoutMs) */
    char **variables;          /* Environment: "NAME=value" sets, "NAME" removes; the rest is the runtime's */
    uint32_t variable_count;

    int32_t exit_code;         /* -1 after a timeout */
    bool timed_out;
    terminal_output output, error_output;
    char *error;               /* collection failed: the message, else NULL */
    atomic_bool done;

    bool streams[2];           /* standard output / error go to a callback as they arrive */
    aqua_ref callbacks[2];
    terminal_chunk chunks[2];
    atomic_flag lock;
    bool calling;              /* a callback is running (runtime thread only) */

    terminal_state *state;
    aqua_pending *pending;     /* Run / RunScript: the waiting script */
    aqua_pending **waiters;    /* Start: Process.Wait calls */
    uint32_t waiter_count;
    terminal_process *process; /* Start: the script's Terminal.Process object, while it lives */
    const char *function;      /* "Run", "RunScript" or "Start", for messages */
    void *platform;
    struct terminal_job *next;
} terminal_job;

/* Called by the worker thread with bytes a stream (0 output, 1 error) produced. */
void terminal_job_output(terminal_job *job, int stream, const char *bytes, size_t length);
/* Called once by the worker thread when the job's results are complete. */
void terminal_job_done(terminal_job *job);

#if defined(TERMINAL_PROCESSES)
/* Launches the job's process and starts its worker. False with *error (malloc'd) when the OS cannot start it. */
bool terminal_platform_start(terminal_job *job, char **error);
/* After terminal_job_done: waits for the worker and releases the process. */
void terminal_platform_finish(terminal_job *job);
/* The runtime is ending: kills the process tree, waits for the worker (which then reports nothing) and releases it. */
void terminal_platform_cancel(terminal_job *job);
/* Process.Kill: ends the process tree; the worker still collects the rest and reports the exit as usual. */
void terminal_platform_kill(terminal_job *job);
#endif

#endif
