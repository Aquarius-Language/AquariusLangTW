/* Terminal processes on Linux, Android (the application's UID and sandbox) and other POSIX systems: fork and execve
 * with standard input an empty pipe and output and error on pipes, in a new process group so a timeout ends the whole
 * tree. One worker thread polls both pipes, the timeout and cancellation together. */
#include "../terminal.h"
#if defined(TERMINAL_POSIX)
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <pthread.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

extern char **environ;

typedef struct process {
    pid_t pid;
    int pipes[2];              /* our ends of standard output and standard error */
    int wake[2];               /* cancellation */
    pthread_t thread;
    bool cancelled;
} process;

static double now_ms(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (double)t.tv_sec * 1000.0 + (double)t.tv_nsec / 1e6;
}

static bool cloexec_pipe(int fds[2]) {
    if (pipe(fds) != 0) return false;
    fcntl(fds[0], F_SETFD, FD_CLOEXEC);
    fcntl(fds[1], F_SETFD, FD_CLOEXEC);
    return true;
}

static void close_fd(int *fd) {
    if (*fd >= 0) close(*fd);
    *fd = -1;
}

/* A bare program name is searched in PATH before forking (the child may only make async-signal-safe calls). */
static char *find_program(const char *name) {
    if (strchr(name, '/')) return strdup(name);
    const char *path = getenv("PATH");
    if (!path || !*path) path = strcmp(AQUA_PLATFORM_NAME, "android") == 0 ? "/system/bin" : "/usr/local/bin:/usr/bin:/bin";
    size_t name_length = strlen(name);
    for (const char *p = path;;) {
        const char *end = strchr(p, ':');
        size_t length = end ? (size_t)(end - p) : strlen(p);
        char *candidate = malloc(length + name_length + 3);
        if (length) { memcpy(candidate, p, length); candidate[length] = '/'; memcpy(candidate + length + 1, name, name_length + 1); }
        else { candidate[0] = '.'; candidate[1] = '/'; memcpy(candidate + 2, name, name_length + 1); }
        struct stat info;
        if (stat(candidate, &info) == 0 && S_ISREG(info.st_mode) && access(candidate, X_OK) == 0) return candidate;
        free(candidate);
        if (!end) break;
        p = end + 1;
    }
    return NULL;
}

static char *start_error(const terminal_job *job, int code) {
    char cwd[4096] = "";
    const char *where = job->directory ? job->directory : (getcwd(cwd, sizeof cwd) ? cwd : "");
    const char *reason = strerror(code);
    size_t n = strlen(job->executable) + strlen(where) + strlen(reason) + 96;
    char *message = malloc(n);
    snprintf(message, n, "An error occurred trying to start process '%s' with working directory '%s'. %s", job->executable, where, reason);
    return message;
}

/* ---- The worker ---- */

static void kill_tree(process *p) {
    kill(-p->pid, SIGKILL);
    kill(p->pid, SIGKILL);
}

/* Reads whatever is available; false once the pipe has ended. */
static bool drain(int fd, terminal_output *output) {
    char buffer[8192];
    for (;;) {
        ssize_t n = read(fd, buffer, sizeof buffer);
        if (n > 0) { terminal_output_append(output, buffer, (size_t)n); if ((size_t)n < sizeof buffer) return true; continue; }
        if (n < 0 && errno == EINTR) continue;
        return n < 0 && errno == EAGAIN;
    }
}

static void record_exit(terminal_job *job, int status) {
    if (WIFEXITED(status)) job->exit_code = WEXITSTATUS(status);
    else if (WIFSIGNALED(status)) job->exit_code = 128 + WTERMSIG(status);
}

static void *supervise(void *data) {
    terminal_job *job = data;
    process *p = job->platform;
    terminal_output *outputs[2] = {&job->output, &job->error_output};
    bool open[2] = {true, true}, exited = false;
    double start = now_ms(), exit_time = 0, bound = job->timeout_ms > 1000 ? job->timeout_ms : 1000;
    for (;;) {
        if (!exited) {
            int status;
            pid_t r = waitpid(p->pid, &status, WNOHANG);
            if (r == p->pid || (r < 0 && errno == ECHILD)) {
                if (r == p->pid) record_exit(job, status);
                exited = true;
                exit_time = now_ms();
            }
        }
        if (exited && !open[0] && !open[1]) break;
        double now = now_ms(), limit = exited ? exit_time + bound : start + job->timeout_ms;
        if (now >= limit) {
            if (!exited) {
                kill_tree(p);
                int status;
                while (waitpid(p->pid, &status, 0) < 0 && errno == EINTR) {}
                exited = true;
                job->timed_out = true;
                exit_time = now_ms();
                continue;
            }
            job->error = strdup("Child output streams did not close.");
            break;
        }
        struct pollfd fds[3];
        int map[3], count = 0;
        fds[count].fd = p->wake[0]; fds[count].events = POLLIN; fds[count].revents = 0; map[count++] = -1;
        for (int i = 0; i < 2; i++) if (open[i]) { fds[count].fd = p->pipes[i]; fds[count].events = POLLIN; fds[count].revents = 0; map[count++] = i; }
        /* The child's exit is not a pollable event: until it is seen, check for it every 50 ms (every 5 ms once its output
           has ended), so descendants still holding the pipes cannot hide it. */
        double wait = limit - now;
        if (!exited && wait > (count == 1 ? 5 : 50)) wait = count == 1 ? 5 : 50;
        int ready = poll(fds, (nfds_t)count, (int)(wait < 1 ? 1 : wait));
        if (ready < 0 && errno != EINTR) { job->error = strdup(strerror(errno)); kill_tree(p); break; }
        if (ready <= 0) continue;
        if (fds[0].revents) { kill_tree(p); p->cancelled = true; break; }
        for (int i = 1; i < count; i++)
            if (fds[i].revents && !drain(fds[i].fd, outputs[map[i]])) open[map[i]] = false;
    }
    if (!exited || p->cancelled) {
        int status;
        while (waitpid(p->pid, &status, 0) < 0 && errno == EINTR) {}
    }
    for (int i = 0; i < 2; i++) close_fd(&p->pipes[i]);
    if (!p->cancelled) terminal_job_done(job);
    return NULL;
}

/* ---- terminal.h ---- */

static void release(process *p) {
    for (int i = 0; i < 2; i++) { close_fd(&p->pipes[i]); close_fd(&p->wake[i]); }
    free(p);
}

bool terminal_platform_start(terminal_job *job, char **error) {
    char *program = find_program(job->executable);
    if (!program) { *error = start_error(job, ENOENT); return false; }
    process *p = calloc(1, sizeof *p);
    int input[2] = {-1, -1}, output[2] = {-1, -1}, error_output[2] = {-1, -1}, report[2] = {-1, -1};
    p->pipes[0] = p->pipes[1] = p->wake[0] = p->wake[1] = -1;
    if (!cloexec_pipe(input) || !cloexec_pipe(output) || !cloexec_pipe(error_output) || !cloexec_pipe(report) || !cloexec_pipe(p->wake)) {
        *error = strdup(strerror(errno));
        for (int i = 0; i < 2; i++) { close_fd(&input[i]); close_fd(&output[i]); close_fd(&error_output[i]); close_fd(&report[i]); }
        release(p);
        free(program);
        return false;
    }
    char **argv = calloc(job->count + 2, sizeof *argv);
    argv[0] = job->executable;
    for (uint32_t i = 0; i < job->count; i++) argv[i + 1] = job->arguments[i];

    pid_t pid = fork();
    if (pid == 0) {
        /* The child: only async-signal-safe calls until execve. */
        struct sigaction defaults;
        memset(&defaults, 0, sizeof defaults);
        defaults.sa_handler = SIG_DFL;
        sigaction(SIGPIPE, &defaults, NULL);
        sigset_t none;
        sigemptyset(&none);
        sigprocmask(SIG_SETMASK, &none, NULL);
        setpgid(0, 0);
        int code = 0;
        if (dup2(input[0], 0) < 0 || dup2(output[1], 1) < 0 || dup2(error_output[1], 2) < 0) code = errno;
        if (!code && job->directory && chdir(job->directory) != 0) code = errno;
        if (!code) { execve(program, argv, environ); code = errno; }
        ssize_t ignored = write(report[1], &code, sizeof code);
        (void)ignored;
        _exit(127);
    }
    int fork_error = errno;
    free(argv);
    free(program);
    close_fd(&input[0]); close_fd(&input[1]); /* standard input is closed: commands read the end of input */
    close_fd(&output[1]); close_fd(&error_output[1]); close_fd(&report[1]);
    if (pid < 0) {
        *error = start_error(job, fork_error);
        close_fd(&output[0]); close_fd(&error_output[0]); close_fd(&report[0]);
        release(p);
        return false;
    }
    /* The report pipe closes on a successful execve; otherwise it carries the child's errno. */
    int code = 0;
    ssize_t n;
    while ((n = read(report[0], &code, sizeof code)) < 0 && errno == EINTR) {}
    close_fd(&report[0]);
    if (n == (ssize_t)sizeof code) {
        int status;
        while (waitpid(pid, &status, 0) < 0 && errno == EINTR) {}
        *error = start_error(job, code);
        close_fd(&output[0]); close_fd(&error_output[0]);
        release(p);
        return false;
    }
    p->pid = pid;
    p->pipes[0] = output[0];
    p->pipes[1] = error_output[0];
    fcntl(p->pipes[0], F_SETFL, O_NONBLOCK);
    fcntl(p->pipes[1], F_SETFL, O_NONBLOCK);
    job->platform = p;
    if (pthread_create(&p->thread, NULL, supervise, job) != 0) {
        kill_tree(p);
        int status;
        waitpid(pid, &status, 0);
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
    char signal_byte = 1;
    ssize_t ignored = write(p->wake[1], &signal_byte, 1);
    (void)ignored;
    terminal_platform_finish(job);
}
#endif
