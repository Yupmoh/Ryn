#if !defined(_WIN32)

#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <signal.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#if defined(__APPLE__)
#include <libproc.h>
#endif

#if defined(__linux__)
#include <sys/syscall.h>
#endif

struct ryn_sidecar_spawn_status
{
    int error;
    int child_pid;
};

static int set_close_on_exec(int fd)
{
    int flags = fcntl(fd, F_GETFD, 0);
    if (flags == -1 || fcntl(fd, F_SETFD, flags | FD_CLOEXEC) == -1)
        return -1;
    return 0;
}

static void set_sigpipe_disposition(void (*handler)(int))
{
    struct sigaction action;
    memset(&action, 0, sizeof(action));
    action.sa_handler = handler;
    sigemptyset(&action.sa_mask);
    (void)sigaction(SIGPIPE, &action, NULL);
}
static int create_pipe(int descriptors[2])
{
#if defined(__linux__)
    return pipe2(descriptors, O_CLOEXEC);
#else
    return pipe(descriptors);
#endif
}

static int duplicate_for_watchdog(int fd)
{
#if defined(F_DUPFD_CLOEXEC)
    int duplicate = fcntl(fd, F_DUPFD_CLOEXEC, 64);
#else
    int duplicate = fcntl(fd, F_DUPFD, 64);
#endif
    if (duplicate == -1)
        return -1;
#if !defined(F_DUPFD_CLOEXEC)
    if (set_close_on_exec(duplicate) == -1)
    {
        int error = errno;
        close(duplicate);
        errno = error;
        return -1;
    }
#endif
    return duplicate;
}

static void close_from(int first, long descriptor_limit)
{
#if defined(__linux__) && defined(SYS_close_range)
    if (syscall(SYS_close_range, (unsigned int)first, UINT32_MAX, 0) == 0)
        return;
#endif
    for (long descriptor = first; descriptor < descriptor_limit; descriptor++)
        close((int)descriptor);
}

static int write_all(int fd, const void* buffer, size_t length)
{
    const unsigned char* bytes = (const unsigned char*)buffer;
    while (length > 0)
    {
        ssize_t written = write(fd, bytes, length);
        if (written > 0)
        {
            bytes += written;
            length -= (size_t)written;
            continue;
        }
        if (written == -1 && errno == EINTR)
            continue;
        return -1;
    }
    return 0;
}

static ssize_t read_retry(int fd, void* buffer, size_t length)
{
    ssize_t result;
    do
    {
        result = read(fd, buffer, length);
    }
    while (result == -1 && errno == EINTR);
    return result;
}

static int wait_for_child(pid_t child_pid, int* status)
{
    pid_t result;
    do
    {
        result = waitpid(child_pid, status, 0);
    }
    while (result == -1 && errno == EINTR);
    return result == child_pid ? 0 : -1;
}

static void terminate_group(pid_t child_pid)
{
    if (child_pid > 0)
        (void)kill(-child_pid, SIGKILL);
}

static int status_exit_code(int status)
{
    if (WIFEXITED(status))
        return WEXITSTATUS(status);
    if (WIFSIGNALED(status))
        return 128 + WTERMSIG(status);
    return 127;
}

static void run_watchdog(
    const char* command,
    char* const argv[],
    char* const envp[],
    const char* cwd,
    long descriptor_limit,
    int control_source,
    int status_source,
    int exec_read_source,
    int output_source,
    int error_source,
    int exec_write_source)
{
    /* This process never returns to the managed runtime; inherited runtime handlers are invalid here. */
    struct sigaction default_action;
    memset(&default_action, 0, sizeof(default_action));
    default_action.sa_handler = SIG_DFL;
    sigemptyset(&default_action.sa_mask);
    for (int signal_number = 1; signal_number < NSIG; signal_number++)
        (void)sigaction(signal_number, &default_action, NULL);
    sigset_t empty_mask;
    sigemptyset(&empty_mask);
    (void)sigprocmask(SIG_SETMASK, &empty_mask, NULL);
    set_sigpipe_disposition(SIG_IGN);
    /* Sources are duplicated above the fixed range before fork, so these remaps cannot clobber one another. */
    if (dup2(control_source, 3) == -1 ||
        dup2(status_source, 4) == -1 ||
        dup2(exec_read_source, 5) == -1 ||
        dup2(output_source, 6) == -1 ||
        dup2(error_source, 7) == -1 ||
        dup2(exec_write_source, 8) == -1)
    {
        _exit(127);
    }

    /* The duplicates remain open above 63 unless explicitly closed; they would otherwise hold EOF pipes alive. */
    close(control_source);
    close(status_source);
    close(exec_read_source);
    close(output_source);
    close(error_source);
    close(exec_write_source);
    close(STDIN_FILENO);
    close(STDOUT_FILENO);
    close(STDERR_FILENO);
    close_from(9, descriptor_limit);

    pid_t child_pid = fork();
    if (child_pid == -1)
    {
        struct ryn_sidecar_spawn_status failed = { errno, 0 };
        (void)write_all(4, &failed, sizeof(failed));
        _exit(127);
    }

    if (child_pid == 0)
    {
        (void)setpgid(0, 0);
        set_sigpipe_disposition(SIG_DFL);
        if (dup2(6, STDOUT_FILENO) == -1 ||
            dup2(7, STDERR_FILENO) == -1 ||
            dup2(8, 3) == -1 ||
            set_close_on_exec(3) == -1)
        {
            int error = errno;
            (void)write_all(3, &error, sizeof(error));
            _exit(127);
        }

        /* Keep descriptor zero valid: helpers should read EOF, not EBADF or an unrelated later-opened file. */
        int null_input = open("/dev/null", O_RDONLY);
        if (null_input == -1 || (null_input != STDIN_FILENO && dup2(null_input, STDIN_FILENO) == -1))
        {
            int error = errno;
            (void)write_all(3, &error, sizeof(error));
            _exit(127);
        }
        if (null_input != STDIN_FILENO)
            close(null_input);
        close_from(4, descriptor_limit);
        if (cwd != NULL && cwd[0] != '\0' && chdir(cwd) == -1)
        {
            int error = errno;
            (void)write_all(3, &error, sizeof(error));
            _exit(127);
        }

        execve(command, argv, envp);
        int error = errno;
        (void)write_all(3, &error, sizeof(error));
        _exit(127);
    }

    /* The child performs the same call, closing the exec race without managed work after fork. */
    (void)setpgid(child_pid, child_pid);
    close(6);
    close(7);
    close(8);

    int exec_error = 0;
    ssize_t exec_result = read_retry(5, &exec_error, sizeof(exec_error));
    close(5);
    if (exec_result != 0)
    {
        if (exec_result < 0 || exec_result != (ssize_t)sizeof(exec_error))
            exec_error = EIO;
        terminate_group(child_pid);
        int ignored_status;
        (void)wait_for_child(child_pid, &ignored_status);
        struct ryn_sidecar_spawn_status failed = { exec_error, 0 };
        (void)write_all(4, &failed, sizeof(failed));
        _exit(127);
    }

    struct ryn_sidecar_spawn_status started = { 0, (int)child_pid };
    if (write_all(4, &started, sizeof(started)) == -1)
    {
        terminate_group(child_pid);
        int ignored_status;
        (void)wait_for_child(child_pid, &ignored_status);
        _exit(127);
    }
    close(4);

    /* The acknowledgement prevents an immediately exiting child from being reaped before managed handles exist. */
    unsigned char acknowledgement;
    if (read_retry(3, &acknowledgement, 1) != 1 || acknowledgement != 1)
    {
        terminate_group(child_pid);
        int ignored_status;
        (void)wait_for_child(child_pid, &ignored_status);
        _exit(137);
    }

    /* Descriptor 3 remains the sole watchdog control channel until exit. */
    for (;;)
    {
        struct pollfd control = { 3, POLLIN | POLLHUP | POLLERR, 0 };
        int poll_result;
        do
        {
            poll_result = poll(&control, 1, 100);
        }
        while (poll_result == -1 && errno == EINTR);

        if (poll_result < 0 || (poll_result > 0 && (control.revents & (POLLIN | POLLHUP | POLLERR)) != 0))
        {
            unsigned char ignored;
            ssize_t control_result = poll_result < 0 ? 0 : read_retry(3, &ignored, 1);
            if (control_result <= 0)
            {
                terminate_group(child_pid);
                int status;
                if (wait_for_child(child_pid, &status) == 0)
                    _exit(status_exit_code(status));
                _exit(137);
            }
        }

        siginfo_t child_info;
        child_info.si_pid = 0;
        int wait_result;
        do
        {
            wait_result = waitid(P_PID, (id_t)child_pid, &child_info, WEXITED | WNOHANG | WNOWAIT);
        }
        while (wait_result == -1 && errno == EINTR);

        if (wait_result == 0 && child_info.si_pid == child_pid)
        {
            /* Kill descendants while the unreaped root still reserves the process-group ID. */
            terminate_group(child_pid);
            int status;
            if (wait_for_child(child_pid, &status) == 0)
                _exit(status_exit_code(status));
            _exit(127);
        }
        if (wait_result == -1 && errno == ECHILD)
            _exit(127);
    }
}

__attribute__((visibility("default"))) int ryn_sidecar_wait(int watchdog_pid)
{
    int status;
    if (wait_for_child((pid_t)watchdog_pid, &status) != 0)
        return -errno;
    return status_exit_code(status);
}

/*
 * Starts a native watchdog and its process-group-owned child. The managed host retains only the control
 * write descriptor; EOF after a host crash makes the watchdog kill the whole group. Returns -errno on failure.
 */
int ryn_sidecar_spawn(
    const char* command,
    char* const argv[],
    char* const envp[],
    const char* cwd,
    int* watchdog_pid,
    int* child_pid,
    int* stdout_fd,
    int* stderr_fd,
    int* control_fd)
{
    int output_pipe[2] = { -1, -1 };
    int error_pipe[2] = { -1, -1 };
    int control_pipe[2] = { -1, -1 };
    int status_pipe[2] = { -1, -1 };
    int exec_pipe[2] = { -1, -1 };
    int watchdog_descriptors[6] = { -1, -1, -1, -1, -1, -1 };
    pid_t watchdog = -1;
    int error = 0;
    long descriptor_limit = sysconf(_SC_OPEN_MAX);
    if (descriptor_limit < 64)
        descriptor_limit = 1024;
#if defined(__APPLE__)
    /* Existing descriptors survive a lowered RLIMIT_NOFILE. Snapshot them before fork;
       new concurrent opens remain bounded by the current soft limit above. */
    int descriptor_bytes = proc_pidinfo(getpid(), PROC_PIDLISTFDS, 0, NULL, 0);
    if (descriptor_bytes <= 0)
        return -(errno != 0 ? errno : EIO);
    if (descriptor_bytes > INT32_MAX - (int)(64 * sizeof(struct proc_fdinfo)))
        return -EOVERFLOW;
    descriptor_bytes += (int)(64 * sizeof(struct proc_fdinfo));
    struct proc_fdinfo* descriptors = malloc((size_t)descriptor_bytes);
    if (descriptors == NULL)
        return -ENOMEM;
    int listed_bytes = proc_pidinfo(getpid(), PROC_PIDLISTFDS, 0, descriptors, descriptor_bytes);
    if (listed_bytes <= 0)
    {
        int list_error = errno != 0 ? errno : EIO;
        free(descriptors);
        return -list_error;
    }
    if (listed_bytes >= descriptor_bytes)
    {
        free(descriptors);
        return -EAGAIN;
    }
    for (size_t index = 0; index < (size_t)listed_bytes / sizeof(*descriptors); index++)
        if ((long)descriptors[index].proc_fd >= descriptor_limit)
            descriptor_limit = (long)descriptors[index].proc_fd + 1;
    free(descriptors);
#endif

    if (create_pipe(output_pipe) == -1 ||
        create_pipe(error_pipe) == -1 ||
        create_pipe(control_pipe) == -1 ||
        create_pipe(status_pipe) == -1 ||
        create_pipe(exec_pipe) == -1)
    {
        error = errno;
        goto fail;
    }

    int sources[6] =
    {
        control_pipe[0], status_pipe[1], exec_pipe[0],
        output_pipe[1], error_pipe[1], exec_pipe[1]
    };
    for (int index = 0; index < 6; index++)
    {
        watchdog_descriptors[index] = duplicate_for_watchdog(sources[index]);
        if (watchdog_descriptors[index] == -1)
        {
            error = errno;
            goto fail;
        }
    }

#if defined(__APPLE__)
    /* F_DUPFD_CLOEXEC closes the macOS pipe/fcntl inheritance window before either fork occurs. */
    for (int index = 0; index < 5; index++)
    {
        int read_end = index == 0 ? output_pipe[0] :
            index == 1 ? error_pipe[0] :
            index == 2 ? control_pipe[0] :
            index == 3 ? status_pipe[0] : exec_pipe[0];
        int write_end = index == 0 ? output_pipe[1] :
            index == 1 ? error_pipe[1] :
            index == 2 ? control_pipe[1] :
            index == 3 ? status_pipe[1] : exec_pipe[1];
        if (set_close_on_exec(read_end) == -1 || set_close_on_exec(write_end) == -1)
        {
            error = errno;
            goto fail;
        }
    }
#endif

    watchdog = fork();
    if (watchdog == -1)
    {
        error = errno;
        goto fail;
    }
    if (watchdog == 0)
    {
        run_watchdog(
            command,
            argv,
            envp,
            cwd,
            descriptor_limit,
            watchdog_descriptors[0],
            watchdog_descriptors[1],
            watchdog_descriptors[2],
            watchdog_descriptors[3],
            watchdog_descriptors[4],
            watchdog_descriptors[5]);
        _exit(127);
    }

    close(output_pipe[1]);
    output_pipe[1] = -1;
    close(error_pipe[1]);
    error_pipe[1] = -1;
    close(control_pipe[0]);
    control_pipe[0] = -1;
    close(status_pipe[1]);
    status_pipe[1] = -1;
    close(exec_pipe[0]);
    exec_pipe[0] = -1;
    close(exec_pipe[1]);
    exec_pipe[1] = -1;
    for (int index = 0; index < 6; index++)
    {
        close(watchdog_descriptors[index]);
        watchdog_descriptors[index] = -1;
    }

    struct ryn_sidecar_spawn_status status;
    ssize_t status_result = read_retry(status_pipe[0], &status, sizeof(status));
    close(status_pipe[0]);
    status_pipe[0] = -1;
    if (status_result != (ssize_t)sizeof(status) || status.error != 0 || status.child_pid <= 0)
    {
        error = status_result == (ssize_t)sizeof(status) && status.error != 0 ? status.error : EIO;
        close(control_pipe[1]);
        control_pipe[1] = -1;
        (void)kill(watchdog, SIGKILL);
        int ignored_status;
        (void)wait_for_child(watchdog, &ignored_status);
        goto fail;
    }

    *watchdog_pid = (int)watchdog;
    *child_pid = status.child_pid;
    *stdout_fd = output_pipe[0];
    *stderr_fd = error_pipe[0];
    *control_fd = control_pipe[1];
    output_pipe[0] = -1;
    error_pipe[0] = -1;
    control_pipe[1] = -1;
    return 0;

fail:
    for (int index = 0; index < 2; index++)
    {
        if (output_pipe[index] != -1) close(output_pipe[index]);
        if (error_pipe[index] != -1) close(error_pipe[index]);
        if (control_pipe[index] != -1) close(control_pipe[index]);
        if (status_pipe[index] != -1) close(status_pipe[index]);
        if (exec_pipe[index] != -1) close(exec_pipe[index]);
    }
    for (int index = 0; index < 6; index++)
    {
        if (watchdog_descriptors[index] != -1) close(watchdog_descriptors[index]);
    }
    return -error;
}

#endif /* !_WIN32 */
