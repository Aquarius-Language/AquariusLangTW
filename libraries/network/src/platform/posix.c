/* The clock and randomness of POSIX targets (Linux, Android). */
#include "../native/native.h"
#include <fcntl.h>
#include <sys/random.h>
#include <time.h>
#include <unistd.h>

int64_t net_clock_ms(void) {
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (int64_t)now.tv_sec * 1000 + now.tv_nsec / 1000000;
}

bool platform_random(void *buffer, size_t length) {
    uint8_t *p = buffer;
    while (length) {
        ssize_t n = getrandom(p, length, 0);
        if (n <= 0) {
            int fd = open("/dev/urandom", O_RDONLY | O_CLOEXEC);
            if (fd < 0) return false;
            n = read(fd, p, length);
            close(fd);
            if (n <= 0) return false;
        }
        p += n;
        length -= (size_t)n;
    }
    return true;
}
