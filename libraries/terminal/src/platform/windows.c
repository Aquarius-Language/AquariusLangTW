/* Terminal processes on Windows: CreateProcessW with the three standard handles only (standard input an empty pipe,
 * output and error overlapped named pipes), in a job object so a timeout ends the whole process tree. One worker
 * thread waits for the process, both pipes, the timeout and cancellation together. */
#include "../terminal.h"
#if defined(TERMINAL_WINDOWS)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

typedef struct process {
    HANDLE process, job, cancel;
    HANDLE pipes[2];           /* our ends of standard output and standard error */
    pthread_t thread;
    bool cancelled;
} process;

static wchar_t *wide(const char *utf8) {
    int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
    wchar_t *text = malloc((size_t)(n > 0 ? n : 1) * sizeof *text);
    if (n <= 0 || !MultiByteToWideChar(CP_UTF8, 0, utf8, -1, text, n)) text[0] = 0;
    return text;
}

static char *narrow(const wchar_t *text) {
    int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    char *utf8 = malloc(n > 0 ? (size_t)n : 1);
    if (n <= 0 || !WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, n, NULL, NULL)) utf8[0] = 0;
    return utf8;
}

/* The system's description of an error code, without the trailing line break. */
static char *system_message(DWORD code) {
    wchar_t *text = NULL;
    FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS, NULL, code, 0,
                   (wchar_t *)&text, 0, NULL);
    char *message = text ? narrow(text) : NULL;
    LocalFree(text);
    if (!message) {
        message = malloc(32);
        snprintf(message, 32, "Error %lu.", (unsigned long)code);
    }
    for (size_t n = strlen(message); n && strchr("\r\n ", message[n - 1]); n--) message[n - 1] = 0;
    return message;
}

/* ---- The command line ---- */

typedef struct text_buffer { wchar_t *data; size_t length, capacity; } text_buffer;

static void put(text_buffer *b, wchar_t c) {
    if (b->length + 2 > b->capacity) {
        b->capacity = b->capacity ? b->capacity * 2 : 256;
        b->data = realloc(b->data, b->capacity * sizeof *b->data);
    }
    b->data[b->length++] = c;
    b->data[b->length] = 0;
}

/* One argument quoted the way CommandLineToArgvW and the C runtimes split it (the rules of .NET's ArgumentList), so
   every argument reaches the program exactly, spaces and metacharacters included; no shell is involved. */
static void put_argument(text_buffer *b, const char *utf8) {
    wchar_t *argument = wide(utf8);
    if (b->length) put(b, L' ');
    if (*argument && !wcspbrk(argument, L" \t\n\v\"")) {
        for (const wchar_t *p = argument; *p; p++) put(b, *p);
    } else {
        put(b, L'"');
        for (const wchar_t *p = argument;; p++) {
            size_t slashes = 0;
            while (*p == L'\\') { slashes++; p++; }
            if (!*p) { while (slashes--) { put(b, L'\\'); put(b, L'\\'); } break; }
            if (*p == L'"') { for (size_t i = 0; i < slashes * 2 + 1; i++) put(b, L'\\'); }
            else { for (size_t i = 0; i < slashes; i++) put(b, L'\\'); }
            put(b, *p);
        }
        put(b, L'"');
    }
    free(argument);
}

/* ---- Pipes ---- */

/* An inbound named pipe: our end reads with overlapped I/O, the child's end is inheritable. */
static bool output_pipe(HANDLE *ours, HANDLE *child) {
    static volatile LONG serial;
    wchar_t name[96];
    swprintf(name, 96, L"\\\\.\\pipe\\aquarius-terminal-%lu-%ld", (unsigned long)GetCurrentProcessId(), InterlockedIncrement(&serial));
    *ours = CreateNamedPipeW(name, PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                             PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 4096, 65536, 0, NULL);
    if (*ours == INVALID_HANDLE_VALUE) { *ours = NULL; return false; }
    SECURITY_ATTRIBUTES inherit = {sizeof inherit, NULL, TRUE};
    *child = CreateFileW(name, GENERIC_WRITE, 0, &inherit, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (*child == INVALID_HANDLE_VALUE) { *child = NULL; CloseHandle(*ours); *ours = NULL; return false; }
    return true;
}

static void close_handle(HANDLE *h) {
    if (*h) CloseHandle(*h);
    *h = NULL;
}

/* ---- The worker ---- */

typedef struct reader {
    HANDLE pipe;
    OVERLAPPED overlapped;
    char buffer[8192];
    bool open, pending;
    terminal_output *output;
} reader;

/* Starts the next read; a read that completes at once also signals the event, so completions are handled in one place. */
static void read_next(reader *r) {
    if (!r->open || r->pending) return;
    if (ReadFile(r->pipe, r->buffer, sizeof r->buffer, NULL, &r->overlapped) || GetLastError() == ERROR_IO_PENDING) r->pending = true;
    else r->open = false; /* ERROR_BROKEN_PIPE: every writer has closed it */
}

static void read_completed(reader *r) {
    DWORD count = 0;
    if (GetOverlappedResult(r->pipe, &r->overlapped, &count, FALSE)) {
        terminal_output_append(r->output, r->buffer, count);
        r->pending = false;
        read_next(r);
    } else if (GetLastError() != ERROR_IO_INCOMPLETE) {
        r->pending = false;
        r->open = false;
    }
}

static void kill_tree(process *p) {
    if (p->job) TerminateJobObject(p->job, 1);
    else TerminateProcess(p->process, 1);
}

static void *supervise(void *data) {
    terminal_job *job = data;
    process *p = job->platform;
    reader readers[2];
    for (int i = 0; i < 2; i++) {
        memset(&readers[i], 0, sizeof readers[i]);
        readers[i].pipe = p->pipes[i];
        readers[i].overlapped.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
        readers[i].open = true;
        readers[i].output = i == 0 ? &job->output : &job->error_output;
        read_next(&readers[i]);
    }
    ULONGLONG start = GetTickCount64(), exit_time = 0;
    DWORD bound = job->timeout_ms > 1000 ? (DWORD)job->timeout_ms : 1000; /* output must close this long after exit */
    bool exited = false;
    for (;;) {
        if (exited && !readers[0].open && !readers[1].open) break;
        HANDLE handles[4];
        int kinds[4];
        DWORD count = 0;
        handles[count] = p->cancel; kinds[count++] = -1;
        if (!exited) { handles[count] = p->process; kinds[count++] = -2; }
        for (int i = 0; i < 2; i++) if (readers[i].open) { handles[count] = readers[i].overlapped.hEvent; kinds[count++] = i; }
        ULONGLONG now = GetTickCount64(), limit = exited ? exit_time + bound : start + (ULONGLONG)job->timeout_ms;
        DWORD outcome = WaitForMultipleObjects(count, handles, FALSE, now >= limit ? 0 : (DWORD)(limit - now));
        if (outcome == WAIT_TIMEOUT) {
            if (!exited) {
                kill_tree(p);
                WaitForSingleObject(p->process, INFINITE);
                exited = true;
                job->timed_out = true;
                exit_time = GetTickCount64();
                continue;
            }
            job->error = strdup("Child output streams did not close.");
            break;
        }
        if (outcome >= WAIT_OBJECT_0 + count) { job->error = system_message(GetLastError()); break; }
        int kind = kinds[outcome - WAIT_OBJECT_0];
        if (kind == -1) { kill_tree(p); p->cancelled = true; break; }
        if (kind == -2) {
            DWORD code = 0;
            GetExitCodeProcess(p->process, &code);
            job->exit_code = (int32_t)code;
            exited = true;
            exit_time = GetTickCount64();
            continue;
        }
        read_completed(&readers[kind]);
    }
    for (int i = 0; i < 2; i++) {
        if (readers[i].pending) {
            DWORD count;
            CancelIoEx(readers[i].pipe, &readers[i].overlapped);
            GetOverlappedResult(readers[i].pipe, &readers[i].overlapped, &count, TRUE);
        }
        CloseHandle(readers[i].overlapped.hEvent);
        close_handle(&p->pipes[i]);
    }
    if (!p->cancelled) terminal_job_done(job);
    return NULL;
}

/* ---- terminal.h ---- */

static void release(process *p) {
    close_handle(&p->process);
    close_handle(&p->job);
    close_handle(&p->cancel);
    for (int i = 0; i < 2; i++) close_handle(&p->pipes[i]);
    free(p);
}

bool terminal_platform_start(terminal_job *job, char **error) {
    process *p = calloc(1, sizeof *p);
    SECURITY_ATTRIBUTES inherit = {sizeof inherit, NULL, TRUE};
    HANDLE input = NULL, input_writer = NULL, output = NULL, error_output = NULL;
    if (!CreatePipe(&input, &input_writer, &inherit, 0) || !output_pipe(&p->pipes[0], &output) || !output_pipe(&p->pipes[1], &error_output)) {
        *error = system_message(GetLastError());
        close_handle(&input); close_handle(&input_writer); close_handle(&output); close_handle(&error_output);
        release(p);
        return false;
    }
    close_handle(&input_writer); /* standard input is closed: commands read the end of input */

    text_buffer command = {0};
    /* The program name is always quoted, as .NET's Process did; CreateProcessW searches for it like Process.Start. */
    wchar_t *program = wide(job->executable);
    put(&command, L'"');
    for (const wchar_t *c = program; *c; c++) put(&command, *c);
    put(&command, L'"');
    free(program);
    for (uint32_t i = 0; i < job->count; i++) put_argument(&command, job->arguments[i]);

    /* Only the three standard handles are inherited, even while other threads create processes. */
    STARTUPINFOEXW startup;
    memset(&startup, 0, sizeof startup);
    startup.StartupInfo.cb = sizeof startup;
    startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
    startup.StartupInfo.hStdInput = input;
    startup.StartupInfo.hStdOutput = output;
    startup.StartupInfo.hStdError = error_output;
    SIZE_T size = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &size);
    startup.lpAttributeList = malloc(size);
    HANDLE inherited[3] = {input, output, error_output};
    BOOL ok = InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, &size) &&
              UpdateProcThreadAttribute(startup.lpAttributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, sizeof inherited, NULL, NULL);
    PROCESS_INFORMATION info = {0};
    wchar_t *directory = job->directory ? wide(job->directory) : NULL;
    if (ok) ok = CreateProcessW(NULL, command.data, NULL, NULL, TRUE, CREATE_NO_WINDOW | CREATE_SUSPENDED | EXTENDED_STARTUPINFO_PRESENT,
                                NULL, directory, &startup.StartupInfo, &info);
    DWORD code = GetLastError();
    DeleteProcThreadAttributeList(startup.lpAttributeList);
    free(startup.lpAttributeList);
    free(command.data);
    close_handle(&input); close_handle(&output); close_handle(&error_output);
    if (!ok) {
        char *reason = system_message(code);
        const char *where = job->directory;
        char cwd[MAX_PATH * 3] = "";
        if (!where) { wchar_t w[MAX_PATH]; if (GetCurrentDirectoryW(MAX_PATH, w)) { char *n = narrow(w); snprintf(cwd, sizeof cwd, "%s", n); free(n); } where = cwd; }
        size_t n = strlen(job->executable) + strlen(where) + strlen(reason) + 96;
        *error = malloc(n);
        snprintf(*error, n, "An error occurred trying to start process '%s' with working directory '%s'. %s", job->executable, where, reason);
        free(reason);
        free(directory);
        release(p);
        return false;
    }
    free(directory);
    p->job = CreateJobObjectW(NULL, NULL);
    if (p->job && !AssignProcessToJobObject(p->job, info.hProcess)) close_handle(&p->job);
    ResumeThread(info.hThread);
    CloseHandle(info.hThread);
    p->process = info.hProcess;
    p->cancel = CreateEventW(NULL, TRUE, FALSE, NULL);
    job->platform = p;
    if (pthread_create(&p->thread, NULL, supervise, job) != 0) {
        kill_tree(p);
        *error = strdup("Could not start the output collector thread.");
        release(p);
        job->platform = NULL;
        return false;
    }
    return true;
}

void terminal_platform_finish(terminal_job *job) {
    process *p = job->platform;
    if (!p) return;
    pthread_join(p->thread, NULL);
    release(p);
    job->platform = NULL;
}

void terminal_platform_cancel(terminal_job *job) {
    process *p = job->platform;
    if (!p) return;
    SetEvent(p->cancel);
    terminal_platform_finish(job);
}
#endif
