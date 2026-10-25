/* Terminal / 終端機: child processes the script asks for explicitly. terminal.c owns the module, argument validation
 * and results; the platform layer (platform/<os>.c) launches a process with pipes and collects its output on a worker
 * thread, which reports completion with terminal_job_done (delivered to the script through aqua_post). */
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

/* One Run/RunScript call. The request fields are set before the launch; the worker thread fills the result fields and
   then calls terminal_job_done; the runtime thread reads them only after that. */
typedef struct terminal_job {
    char *executable;          /* as given, or resolved to a packaged file */
    char **arguments;          /* argv[1..]: count entries */
    uint32_t count;
    char *directory;           /* working directory, or NULL for the runtime's */
    int32_t timeout_ms;

    int32_t exit_code;         /* -1 after a timeout */
    bool timed_out;
    terminal_output output, error_output;
    char *error;               /* collection failed: the message, else NULL */
    atomic_bool done;

    terminal_state *state;
    aqua_pending *pending;
    const char *function;      /* "Run" or "RunScript", for messages */
    void *platform;
    struct terminal_job *next;
} terminal_job;

/* Called once by the worker thread when the job's results are complete. */
void terminal_job_done(terminal_job *job);

#if defined(TERMINAL_PROCESSES)
/* Launches the job's process and starts its worker. False with *error (malloc'd) when the OS cannot start it. */
bool terminal_platform_start(terminal_job *job, char **error);
/* After terminal_job_done: waits for the worker and releases the process. */
void terminal_platform_finish(terminal_job *job);
/* The runtime is ending: kills the process tree, waits for the worker (which then reports nothing) and releases it. */
void terminal_platform_cancel(terminal_job *job);
#endif

#endif
