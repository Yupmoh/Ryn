#if !defined(_WIN32)

#include <errno.h>
#include <fcntl.h>
#include <stdlib.h>
#include <string.h>
#include <signal.h>
#include <sys/ioctl.h>
#include <sys/wait.h>
#include <unistd.h>

#if defined(__APPLE__)
#include <util.h>
#else
#include <pty.h>
#endif

int ryn_pty_resize(int fd, unsigned short rows, unsigned short cols)
{
    struct winsize ws = {0};
    ws.ws_row = rows;
    ws.ws_col = cols;
    return ioctl(fd, TIOCSWINSZ, &ws);
}

/*
 * Spawns a child with a PTY. argv/envp/cwd are fully prepared by the parent;
 * the child performs only async-signal-safe operations between fork and execve.
 * Returns 0 on success or -errno on failure.
 */
int ryn_pty_spawn(
    const char* command,
    char* const argv[],
    char* const envp[],
    const char* cwd,
    unsigned short cols,
    unsigned short rows,
    int* master_fd,
    int* child_pid)
{
    struct winsize ws;
    ws.ws_row = rows;
    ws.ws_col = cols;
    ws.ws_xpixel = 0;
    ws.ws_ypixel = 0;

    int master;
    pid_t pid = forkpty(&master, NULL, NULL, &ws);

    if (pid < 0)
        return -errno;

    if (pid == 0)
    {
        if (cwd != NULL && chdir(cwd) != 0)
            _exit(127);

        execve(command, argv, envp);
        _exit(127);
    }

    int flags = fcntl(master, F_GETFD, 0);
    if (flags != -1)
        (void)fcntl(master, F_SETFD, flags | FD_CLOEXEC);

    int status_flags = fcntl(master, F_GETFL, 0);
    if (status_flags == -1 || fcntl(master, F_SETFL, status_flags | O_NONBLOCK) == -1)
    {
        int error = errno;
        kill(pid, SIGKILL);
        close(master);
        while (waitpid(pid, NULL, 0) == -1 && errno == EINTR) {}
        return -error;
    }

    *master_fd = master;
    *child_pid = (int)pid;
    return 0;
}

#endif /* !_WIN32 */
