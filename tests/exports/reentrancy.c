/* A call made while a call runs on the same thread is refused with an error, not a deadlock: a timer signal calls the
   library while counter_spin runs (POSIX only: Windows has no way to interrupt a thread with a call; the check itself is
   the same C code everywhere). scripts/test-exports.ps1 runs it on Linux. */
#include "counter.h"
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <sys/time.h>

static volatile sig_atomic_t refused, served;

static void interrupt(int signal_number) {
    (void)signal_number;
    counter_increment();
    const char *error = counter_last_error();
    if (error && strstr(error, "reentrant")) refused = 1;
    else if (!error) served++; /* the signal came between two calls */
}

int main(void) {
    counter_initialize();
    struct sigaction action;
    memset(&action, 0, sizeof action);
    action.sa_handler = interrupt;
    sigaction(SIGALRM, &action, NULL);
    struct itimerval timer = {{0, 2000}, {0, 2000}};
    setitimer(ITIMER_REAL, &timer, NULL);
    for (int round = 0; round < 50 && !refused; round++) counter_spin(2000000);
    struct itimerval stop = {{0, 0}, {0, 0}};
    setitimer(ITIMER_REAL, &stop, NULL);
    int after = counter_increment();
    printf("reentrancy %s, next call %s\n", refused ? "refused" : "not seen", after == 101 + served ? "ok" : "wrong");
    counter_shutdown();
    return !(refused && after == 101 + served);
}
