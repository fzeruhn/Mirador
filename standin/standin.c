/*
 * Mirador's stand-in for monado-service.exe (Mirador.md section 4.2 item 5).
 *
 * Monado's runtime DLL starts the service it was built to start (XRT_SERVICE_EXECUTABLE) from service\ below its own
 * folder when a game finds no Monado running, then retries the service's pipe every 100 ms for as long as that program
 * lives. Mirador puts this program there as mirador-standin.exe, the name Mirador's builds start, and as
 * monado-service.exe, for builds configured as upstream does. It asks Mirador (over \\.\pipe\mirador-app-<session id>)
 * to start Monado, starting Mirador first if it isn't running, waits for the answer, and:
 *   - OK:   lingers 2 s, so that the DLL's next retry finds Monado's pipe while this program still lives; exit 0;
 *   - FAIL: prints Mirador's reason and exits 1 at once, so that the game fails quickly instead of hanging.
 * Exit codes: 0 Monado is up; 1 Mirador refused or failed (its message on stderr); 3 Mirador isn't running and this
 * process is elevated; 4 Mirador could not be started; 5 Mirador did not answer within 15 s of starting; 6 no answer
 * within 120 s.
 *
 * Copyright 2026, Finn Zeruhn. Boost Software License 1.0.
 */

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif
#define WIN32_LEAN_AND_MEAN
#define _CRT_SECURE_NO_WARNINGS /* wcsncpy is used with its bound and a terminator */

#include <windows.h>
#include <aclapi.h>
#include <userenv.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>

enum
{
    EXIT_UP = 0,
    EXIT_FAILED = 1,
    EXIT_ELEVATED = 3,
    EXIT_NO_APP = 4,
    EXIT_APP_SILENT = 5,
    EXIT_NO_ANSWER = 6,
};

#define LINGER_MS 2000
#define APP_WAIT_MS 15000
#define ANSWER_WAIT_MS 120000
#define RETRY_MS 100
#define POLL_MS 50
#define MAX_LINE 256

static ULONGLONG started;

static ULONGLONG elapsed(void)
{
    return GetTickCount64() - started;
}

static void say(const char *text)
{
    fprintf(stderr, "Mirador stand-in: %s\n", text);
    fflush(stderr);
}

static void say_error(const char *what, DWORD error)
{
    fprintf(stderr, "Mirador stand-in: %s (Windows error %lu).\n", what, (unsigned long)error);
    fflush(stderr);
}

/* Whether the name is a test's pipe: mirador-standin-test- and 12 hex digits. */
static BOOL is_test_pipe(const wchar_t *name)
{
    static const wchar_t prefix[] = L"mirador-standin-test-";
    const size_t prefix_length = sizeof prefix / sizeof prefix[0] - 1;
    size_t i;
    if (wcsncmp(name, prefix, prefix_length) != 0 || wcslen(name) != prefix_length + 12)
    {
        return FALSE;
    }
    for (i = prefix_length; i < prefix_length + 12; i++)
    {
        if (!iswxdigit(name[i]))
        {
            return FALSE;
        }
    }
    return TRUE;
}

/* Whether this process runs elevated. */
static BOOL is_elevated(void)
{
    HANDLE token;
    TOKEN_ELEVATION elevation;
    DWORD size = 0;
    BOOL elevated = FALSE;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
    {
        if (GetTokenInformation(token, TokenElevation, &elevation, sizeof elevation, &size))
        {
            elevated = elevation.TokenIsElevated != 0;
        }
        CloseHandle(token);
    }
    return elevated;
}

/*
 * Whether the pipe belongs to the user this process runs as (its token's user, also when elevated): pipe names are
 * machine-wide, so another user could make one of this name first.
 */
static BOOL owned_by_this_user(HANDLE pipe)
{
    PSID owner = NULL;
    PSECURITY_DESCRIPTOR descriptor = NULL;
    union
    {
        TOKEN_USER user;
        BYTE bytes[SECURITY_MAX_SID_SIZE + sizeof(TOKEN_USER)];
    } me;
    HANDLE token;
    DWORD size = 0;
    BOOL same = FALSE;

    if (GetSecurityInfo(pipe, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION, &owner, NULL, NULL, NULL, &descriptor) != ERROR_SUCCESS)
    {
        return FALSE;
    }

    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
    {
        if (GetTokenInformation(token, TokenUser, &me, sizeof me, &size))
        {
            same = owner != NULL && EqualSid(owner, me.user.User.Sid);
        }
        CloseHandle(token);
    }

    LocalFree(descriptor);
    return same;
}

/* Opens the pipe, waiting while every instance is busy. INVALID_HANDLE_VALUE and *error when it can't. */
static HANDLE open_pipe(const wchar_t *name, DWORD *error)
{
    for (;;)
    {
        /* Identification only: whoever serves the pipe may learn who connected, never act as them. */
        HANDLE pipe = CreateFileW(
            name, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, NULL);
        if (pipe != INVALID_HANDLE_VALUE)
        {
            *error = ERROR_SUCCESS;
            return pipe;
        }

        *error = GetLastError();
        if (*error != ERROR_PIPE_BUSY || elapsed() >= ANSWER_WAIT_MS)
        {
            return INVALID_HANDLE_VALUE;
        }

        WaitNamedPipeW(name, 2000);
    }
}

static BOOL is_file(const wchar_t *path)
{
    DWORD attributes = GetFileAttributesW(path);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

/*
 * The Mirador this stand-in came with: installed, it sits in <app>\monado\<version>\bin\service\ (the bundled Monado)
 * or <app>\builds\<user>\<profile>\<commit>\bin\service\ (an approved build), so the nearest folder above it that holds
 * Mirador.exe, at most seven up. A copy in a user's build folder has none there.
 */
static BOOL own_app(wchar_t *path, DWORD count)
{
    DWORD length = GetModuleFileNameW(NULL, path, count);
    int i;
    if (length == 0 || length >= count)
    {
        return FALSE;
    }

    for (i = 0; i < 7; i++)
    {
        wchar_t *slash = wcsrchr(path, L'\\');
        if (slash == NULL)
        {
            return FALSE;
        }
        *slash = L'\0';
        if (wcslen(path) + wcslen(L"\\Mirador.exe") + 1 > count)
        {
            return FALSE;
        }
        wcscat(path, L"\\Mirador.exe");
        if (is_file(path))
        {
            return TRUE;
        }
        *wcsrchr(path, L'\\') = L'\0';
    }

    return FALSE;
}

/*
 * Where Mirador is: MIRADOR_APP_PATH (as it is: tests use it), else the Mirador this stand-in came with, else
 * HKCU\Software\Mirador AppPath (the one that ran last), else HKLM\SOFTWARE\Mirador AppPath. A registry path whose
 * file is gone (a deleted build that ran once) is passed over.
 */
static BOOL find_app(wchar_t *path, DWORD count)
{
    DWORD size;
    DWORD length = GetEnvironmentVariableW(L"MIRADOR_APP_PATH", path, count);
    if (length > 0 && length < count)
    {
        return TRUE;
    }

    if (own_app(path, count))
    {
        return TRUE;
    }

    size = count * (DWORD)sizeof(wchar_t);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Mirador", L"AppPath", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, NULL, path, &size) == ERROR_SUCCESS
        && is_file(path))
    {
        return TRUE;
    }

    size = count * (DWORD)sizeof(wchar_t);
    return RegGetValueW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Mirador", L"AppPath", RRF_RT_REG_SZ | RRF_SUBKEY_WOW6464KEY, NULL, path, &size) == ERROR_SUCCESS
        && is_file(path);
}

/*
 * Starts Mirador hidden (--background). It must outlive the game: outside the game's job when the job allows that, and
 * with the user's default environment rather than the game's.
 */
static BOOL start_app(const wchar_t *exe, HANDLE *app)
{
    wchar_t command[MAX_PATH + 32];
    wchar_t folder[MAX_PATH];
    wchar_t *slash;
    STARTUPINFOW startup;
    PROCESS_INFORMATION process;
    void *environment = NULL;
    HANDLE token;
    DWORD flags = CREATE_UNICODE_ENVIRONMENT | CREATE_BREAKAWAY_FROM_JOB;
    BOOL ok;

    if (swprintf(command, MAX_PATH + 32, L"\"%ls\" --background", exe) < 0)
    {
        SetLastError(ERROR_FILENAME_EXCED_RANGE);
        return FALSE;
    }

    wcsncpy(folder, exe, MAX_PATH - 1);
    folder[MAX_PATH - 1] = L'\0';
    slash = wcsrchr(folder, L'\\');
    if (slash != NULL)
    {
        *slash = L'\0';
    }

    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &token))
    {
        if (!CreateEnvironmentBlock(&environment, token, FALSE))
        {
            environment = NULL;
        }
        CloseHandle(token);
    }

    ZeroMemory(&startup, sizeof startup);
    startup.cb = sizeof startup;
    ZeroMemory(&process, sizeof process);
    ok = CreateProcessW(exe, command, NULL, NULL, FALSE, flags, environment, slash != NULL ? folder : NULL, &startup, &process);
    if (!ok && GetLastError() == ERROR_ACCESS_DENIED)
    {
        /* The game's job doesn't allow breaking away: Mirador then ends with the game, but it runs. */
        ok = CreateProcessW(exe, command, NULL, NULL, FALSE, flags & ~(DWORD)CREATE_BREAKAWAY_FROM_JOB, environment,
                            slash != NULL ? folder : NULL, &startup, &process);
    }

    if (environment != NULL)
    {
        DWORD error = GetLastError();
        DestroyEnvironmentBlock(environment);
        SetLastError(error);
    }

    if (ok)
    {
        CloseHandle(process.hThread);
        *app = process.hProcess;
    }
    return ok;
}

/* Reads one line (without its newline) within the 120 s; returns an exit code, or -1 with the line in *line. */
static int read_reply(HANDLE pipe, char *line, DWORD count)
{
    DWORD have = 0;
    for (;;)
    {
        DWORD available = 0;
        char *newline;
        if (!PeekNamedPipe(pipe, NULL, 0, NULL, &available, NULL))
        {
            say("Mirador closed the connection without answering. Its log is %LOCALAPPDATA%\\Mirador\\logs\\mirador.log.");
            return EXIT_FAILED;
        }

        if (available > 0)
        {
            DWORD got = 0;
            DWORD want = count - 1 - have;
            if (want == 0)
            {
                say("Mirador's answer is longer than the stand-in reads.");
                return EXIT_FAILED;
            }
            if (want > available)
            {
                want = available;
            }
            if (!ReadFile(pipe, line + have, want, &got, NULL))
            {
                say("Mirador closed the connection without answering. Its log is %LOCALAPPDATA%\\Mirador\\logs\\mirador.log.");
                return EXIT_FAILED;
            }

            have += got;
            line[have] = '\0';
            newline = memchr(line, '\n', have);
            if (newline != NULL)
            {
                *newline = '\0';
                if (newline > line && newline[-1] == '\r')
                {
                    newline[-1] = '\0';
                }
                return -1;
            }
            continue;
        }

        if (elapsed() >= ANSWER_WAIT_MS)
        {
            say("Mirador didn't answer within 120 s.");
            return EXIT_NO_ANSWER;
        }

        Sleep(POLL_MS);
    }
}

int main(void)
{
    wchar_t name[64];
    wchar_t app[MAX_PATH];
    char line[MAX_LINE + 2];
    DWORD session = 0;
    DWORD error = ERROR_SUCCESS;
    DWORD written = 0;
    DWORD override;
    HANDLE pipe;
    HANDLE started_app = NULL;
    int result;
    static const char request[] = "START\n";

    started = GetTickCount64();

    /*
     * MIRADOR_STANDIN_PIPE is for tests, so that they can run beside a real Mirador: only a test's own name
     * (mirador-standin-test- and 12 hex digits), so it can't lead anywhere but to a pipe, and never when elevated.
     * The owner check still applies.
     */
    override = GetEnvironmentVariableW(L"MIRADOR_STANDIN_PIPE", app, MAX_PATH);
    if (override > 0 && override < MAX_PATH && is_test_pipe(app) && !is_elevated())
    {
        swprintf(name, 64, L"\\\\.\\pipe\\%ls", app);
    }
    else
    {
        if (!ProcessIdToSessionId(GetCurrentProcessId(), &session))
        {
            session = 0;
        }
        swprintf(name, 64, L"\\\\.\\pipe\\mirador-app-%lu", (unsigned long)session);
    }

    pipe = open_pipe(name, &error);
    if (pipe == INVALID_HANDLE_VALUE && error == ERROR_FILE_NOT_FOUND)
    {
        if (is_elevated())
        {
            say("Mirador isn't running. Start Mirador first: it doesn't start from an elevated app.");
            return EXIT_ELEVATED;
        }

        if (!find_app(app, MAX_PATH))
        {
            say("Mirador isn't running, and the stand-in can't find it: start Mirador once, or reinstall it.");
            return EXIT_NO_APP;
        }

        if (!start_app(app, &started_app))
        {
            say_error("Mirador isn't running and couldn't be started", GetLastError());
            return EXIT_NO_APP;
        }

        {
            ULONGLONG asked = elapsed();
            DWORD app_exit = 0;
            for (;;)
            {
                Sleep(RETRY_MS);
                pipe = open_pipe(name, &error);
                if (pipe != INVALID_HANDLE_VALUE || error != ERROR_FILE_NOT_FOUND)
                {
                    break;
                }

                /* A Mirador that ended with an error won't answer (one that ended well handed over to another). */
                if (WaitForSingleObject(started_app, 0) == WAIT_OBJECT_0 && GetExitCodeProcess(started_app, &app_exit) && app_exit != 0)
                {
                    CloseHandle(started_app);
                    fprintf(stderr, "Mirador stand-in: Mirador was started but closed at once (exit code %lu).\n", (unsigned long)app_exit);
                    return EXIT_NO_APP;
                }

                if (elapsed() - asked >= APP_WAIT_MS)
                {
                    CloseHandle(started_app);
                    say("Mirador was started but didn't answer within 15 s.");
                    return EXIT_APP_SILENT;
                }
            }

            CloseHandle(started_app);
        }
    }

    if (pipe == INVALID_HANDLE_VALUE)
    {
        if (error == ERROR_PIPE_BUSY)
        {
            say("Mirador was too busy to answer within 120 s.");
            return EXIT_NO_ANSWER;
        }
        say_error("Mirador couldn't be reached", error);
        return EXIT_FAILED;
    }

    if (!owned_by_this_user(pipe))
    {
        CloseHandle(pipe);
        say("Another user's program holds Mirador's pipe, so Mirador can't be asked. Restart Windows.");
        return EXIT_FAILED;
    }

    if (!WriteFile(pipe, request, (DWORD)(sizeof request - 1), &written, NULL) || written != sizeof request - 1)
    {
        error = GetLastError();
        CloseHandle(pipe);
        say_error("Mirador couldn't be asked to start Monado", error);
        return EXIT_FAILED;
    }

    result = read_reply(pipe, line, (DWORD)sizeof line);
    CloseHandle(pipe);
    if (result >= 0)
    {
        return result;
    }

    if (strcmp(line, "OK") == 0)
    {
        /* The runtime DLL gives up once this program has ended: be there for its next look at Monado's pipe. */
        Sleep(LINGER_MS);
        return EXIT_UP;
    }

    if (strncmp(line, "FAIL ", 5) == 0)
    {
        say(line + 5);
        return EXIT_FAILED;
    }

    say("Mirador answered something the stand-in doesn't know.");
    return EXIT_FAILED;
}
